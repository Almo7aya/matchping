using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MatchPing
{
	/// <summary>Measures on this PC.
	/// - The match server is the address the game sends the most UDP to (Windows' network tracing, see Etw).
	/// - Game servers do not answer pings, so when the match starts one traceroute finds the last router on the way
	///   that does; it is pinged once a second (~0.4 MB an hour): that shows the line's spikes and losses as they come.
	/// - That router is usually where the cloud's network begins, near the player: Google carries the rest of the way
	///   on its own backbone, which does not answer pings. So at the start and every 5 minutes the round trip to the
	///   server's region itself is measured (Google Cloud: GCPing's HTTPS service in that region; AWS: a TCP handshake
	///   with DynamoDB there) and the difference is added to every ping.
	/// - When nothing on the way answers pings, the region's round trip is measured every 5 s instead.</summary>
	sealed class Direct : ISource
	{
		static readonly byte[] Payload = new byte[16];
		const int MinRate = 10;        // packets a second: a match (the game sends 30-60), not a menu's odd packet
		const int SwitchAfter = 3;     // seconds another server must lead before it is the match server
		const int OverAfter = 20;      // seconds without a match flow: the match is over
		const int Recalibrate = 300;   // seconds between measurements of the way on to the region
		const int RegionEvery = 5;     // seconds between region measurements when nothing on the way answers

		readonly Etw etw = new Etw();
		readonly Regions regions;
		readonly Func<int[]> gamePids;
		readonly object gate = new object();
		readonly List<Sample> samples = new List<Sample>();
		readonly AutoResetEvent wake = new AutoResetEvent(false);
		volatile bool active;

		ulong server, candidate;       // the match flow (Etw key) and one that may take over
		int streak;
		long lastFlow;
		Regions.Place place;
		int gen;                       // a new match server makes older pings and measurements void
		bool routing;                  // looking for something to ping
		IPAddress target;              // what is pinged once a second
		int targetHop;                 // its hop (0: the server itself)
		bool inNetwork;                // a router inside the server's cloud
		double? offset;                // ms from that router on to the region (added to its pings)
		long calibratedAt;
		bool calibrating, calibrationFailed;
		bool regionMode, regionBusy;   // nothing on the way answers: the region's round trip every 5 s

		public Direct(Regions regions, Func<int[]> gamePids)
		{
			this.regions = regions;
			this.gamePids = gamePids;
		}

		public bool Active
		{
			get => active;
			set { active = value; if (value) wake.Set(); }
		}

		public void Start() => new Thread(Loop) { IsBackground = true, Name = "direct" }.Start();

		void Loop()
		{
			while (true)
			{
				if (!active)
				{
					if (etw.Running) { etw.Stop(); lock (gate) Forget(); }
					wake.WaitOne();
					continue;
				}
				var t0 = DateTime.UtcNow;
				if (!etw.Running && !etw.Start()) { wake.WaitOne(10000); continue; }   // the reading shows etw.Error
				etw.SetPids(gamePids());
				try { Tick(); } catch (Exception) { }
				int wait = 1000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
				if (wait > 0) Thread.Sleep(wait);
			}
		}

		void Tick()
		{
			long now = Stats.Now();
			ulong top = 0;
			int most = 0;
			foreach (var kv in etw.Take())
				if (kv.Value > most) { top = kv.Key; most = kv.Value; }
			IPAddress ping = null;
			int g;
			lock (gate)
			{
				if (most >= MinRate)
				{
					lastFlow = now;
					if (server == 0) Switch(top);
					else if (top == server) streak = 0;
					else if (top == candidate) { if (++streak >= SwitchAfter) Switch(top); }
					else { candidate = top; streak = 1; }
				}
				if (server != 0 && now - lastFlow > OverAfter) Forget();
				g = gen;
				var p = place;
				if (target != null)
				{
					ping = target;
					if (targetHop > 0 && p?.Endpoint != null && !calibrating && now - calibratedAt >= Recalibrate)
					{
						calibrating = true;
						var t = target;
						ThreadPool.QueueUserWorkItem(_ => Calibrate(g, t, p));
					}
				}
				else if (regionMode && !regionBusy && now % RegionEvery == 0)
				{
					regionBusy = true;
					ThreadPool.QueueUserWorkItem(_ => RegionSample(g, p, now));
				}
			}
			if (ping != null) Ping(g, ping, now);
		}

		void Switch(ulong key)
		{
			Forget();
			server = key;
			var ip = Etw.Address(key);
			place = regions.Lookup(ip);
			routing = true;
			int g = gen;
			var p = place;
			ThreadPool.QueueUserWorkItem(_ => Route(g, ip, p));
		}

		void Forget()
		{
			server = candidate = 0;
			streak = 0;
			place = null;
			target = null;
			targetHop = 0;
			inNetwork = false;
			offset = null;
			calibratedAt = 0;
			routing = calibrating = calibrationFailed = regionMode = regionBusy = false;
			samples.Clear();
			gen++;
		}

		void Route(int g, IPAddress ip, Regions.Place p)
		{
			var (found, hop, isIn) = Probe(regions, ip, p?.Provider, null);
			lock (gate)
			{
				if (g != gen) return;
				routing = false;
				target = found;
				targetHop = hop;
				inNetwork = isIn;
				regionMode = found == null && p?.Endpoint != null;
			}
		}

		/// <summary>What to ping for a server: the server when it answers pings; else the last router on the way that
		/// does (hop: its distance, 0 for the server; inNetwork: inside the server's cloud). log: --probe.</summary>
		public static (IPAddress target, int hop, bool inNetwork) Probe(Regions regions, IPAddress ip, string provider, Action<string> log)
		{
			try
			{
				if (Echo(ip, 3) != null) { log?.Invoke($"the server {ip} answers pings"); return (ip, 0, true); }
				log?.Invoke($"the server {ip} does not answer pings: looking for the last router on the way that does");
				var hops = Trace(ip);
				foreach (var h in hops) log?.Invoke($"  hop {h.Key,2}  {h.Value}{(regions.InNetwork(h.Value, provider) ? "  (the server's cloud)" : "")}");
				int tried = 0;
				foreach (var h in hops.OrderByDescending(h => h.Key))
				{
					if (Private(h.Value)) continue;
					var rtt = Echo(h.Value, 2);
					log?.Invoke($"  hop {h.Key} {h.Value}: {(rtt != null ? $"answers pings ({rtt:0} ms)" : "no reply to pings")}");
					if (rtt != null) return (h.Value, h.Key, regions.InNetwork(h.Value, provider));
					if (++tried >= 8) break;
				}
			}
			catch (Exception e) { log?.Invoke("error: " + e.Message); }
			return (null, 0, false);
		}

		/// <summary>The way on from the router to the region: the region's round trip minus the router's (the lowest
		/// of a few of each, measured together).</summary>
		void Calibrate(int g, IPAddress hop, Regions.Place p)
		{
			var (o, _, _) = Offset(hop, p);
			lock (gate)
			{
				if (g != gen) return;
				calibrating = false;
				calibratedAt = Stats.Now();
				if (o != null) { offset = o; calibrationFailed = false; }
				else if (offset == null) calibrationFailed = true;
			}
		}

		public static (double? offset, double? hop, double? region) Offset(IPAddress hop, Regions.Place p)
		{
			double? h = null;
			for (int i = 0; i < 5; i++)
			{
				var r = Echo(hop, 1);
				if (r != null && (h == null || r < h)) h = r;
				Thread.Sleep(100);
			}
			var reg = RegionRtt(p, 5);
			return (h != null && reg != null ? Math.Max(0, reg.Value - h.Value) : (double?)null, h, reg);
		}

		void RegionSample(int g, Regions.Place p, long ts)
		{
			var ms = RegionRtt(p, 1);
			lock (gate)
			{
				if (g != gen) return;
				regionBusy = false;
				Add(new Sample { Ts = ts, Ping = ms });
			}
		}

		static DateTime lastHttps = DateTime.MinValue;

		/// <summary>The round trip to a region itself (the lowest of n). Google Cloud: HTTPS requests to GCPing's service
		/// there on one kept-alive connection, timed to the first byte of its tiny answer (a request that has to open
		/// the connection is not counted). AWS: TCP handshakes with DynamoDB's endpoint there.</summary>
		public static double? RegionRtt(Regions.Place p, int n)
		{
			if (p?.Endpoint == null) return null;
			double? best = null;
			try
			{
				if (p.Provider == "gcp")
				{
					bool warm = (DateTime.UtcNow - lastHttps).TotalSeconds < 60;
					for (int i = warm ? 1 : 0; i <= n; i++)
					{
						var req = (HttpWebRequest)WebRequest.Create(p.Endpoint + "/api/ping");
						req.Timeout = 5000;
						req.UserAgent = "MatchPing";
						var sw = Stopwatch.StartNew();
						using (var resp = req.GetResponse())
						{
							double ms = sw.Elapsed.TotalMilliseconds;
							using (var s = resp.GetResponseStream()) s.CopyTo(Stream.Null);
							if (i > 0 && (best == null || ms < best)) best = ms;
						}
						lastHttps = DateTime.UtcNow;
					}
				}
				else
				{
					var ip = Dns.GetHostAddresses(p.Endpoint).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
					if (ip == null) return null;
					for (int i = 0; i < n; i++)
						using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
						{
							var sw = Stopwatch.StartNew();
							var ar = s.BeginConnect(ip, 443, null, null);
							if (ar.AsyncWaitHandle.WaitOne(3000))
							{
								try { s.EndConnect(ar); double ms = sw.Elapsed.TotalMilliseconds; if (best == null || ms < best) best = ms; }
								catch (SocketException) { }
							}
						}
				}
			}
			catch (Exception) { }
			return best;
		}

		static double? Echo(IPAddress ip, int tries)
		{
			for (int i = 0; i < tries; i++)
				using (var p = new Ping())
					try { var r = p.Send(ip, 1000, Payload); if (r.Status == IPStatus.Success) return r.RoundtripTime; }
					catch (PingException) { }
			return null;
		}

		/// <summary>The routers on the way (hop -> address): all 30 hops at once, twice (routers drop some).</summary>
		static SortedDictionary<int, IPAddress> Trace(IPAddress ip)
		{
			var hops = new SortedDictionary<int, IPAddress>();
			for (int round = 0; round < 2; round++)
			{
				var tasks = Enumerable.Range(1, 30).Select(ttl => Task.Run(async () =>
				{
					using (var p = new Ping())
						try { return (ttl, reply: await p.SendPingAsync(ip, 1500, Payload, new PingOptions(ttl, true))); }
						catch (PingException) { return (ttl, reply: (PingReply)null); }
				})).ToArray();
				Task.WaitAll(tasks);
				foreach (var t in tasks)
				{
					var r = t.Result.reply;
					if (r != null && r.Status == IPStatus.TtlExpired && r.Address != null) hops[t.Result.ttl] = r.Address;
				}
			}
			return hops;
		}

		static bool Private(IPAddress ip)
		{
			var b = ip.GetAddressBytes();
			return b[0] == 10 || b[0] == 127 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168 ||
				b[0] == 100 && b[1] >= 64 && b[1] < 128 || b[0] == 169 && b[1] == 254;
		}

		void Ping(int g, IPAddress t, long now)
		{
			var p = new Ping();
			p.SendPingAsync(t, 2000, Payload).ContinueWith(task =>
			{
				double? ms = task.Status == TaskStatus.RanToCompletion && task.Result.Status == IPStatus.Success ? task.Result.RoundtripTime : (double?)null;
				lock (gate)
					if (g == gen) Add(new Sample { Ts = now, Ping = ms + (offset ?? 0) });
				p.Dispose();
			});
		}

		void Add(Sample s)
		{
			int i = samples.Count;
			while (i > 0 && samples[i - 1].Ts > s.Ts) i--;   // replies can come back out of order
			samples.Insert(i, s);
			samples.RemoveAll(x => x.Ts <= s.Ts - 120);
		}

		public Reading Read()
		{
			lock (gate)
			{
				var r = new Reading();
				if (etw.Error != null) { r.Status = "can't see the game's traffic: " + etw.Error; r.Info = new[] { "Measuring on this PC: " + etw.Error }; return r; }
				if (server == 0) { r.Status = "waiting for a match"; r.Info = new[] { "Measuring on this PC · waiting for a match" }; return r; }
				var ip = Etw.Address(server);
				r.InMatch = true;
				r.Label = place?.Name ?? ip.ToString();
				var info = new List<string> { $"Match server {ip}:{Etw.Port(server)}" + (place != null ? $" · {place.Cloud} {place.Region} ({place.Name})" : "") };
				if (routing) r.Status = "finding a router near the server…";
				else if (target == null && !regionMode) r.Status = "nothing on the way to the server answers pings";
				else if (target != null && targetHop > 0 && offset == null && !calibrationFailed && place?.Endpoint != null) r.Status = $"measuring the way on to {r.Label}…";
				if (r.Status != null) { info.Add(r.Status); r.Info = info.ToArray(); return r; }

				if (regionMode)
				{
					r.Note = "every 5 s";
					info.Add($"Nothing on the way answers pings: the round trip to {place.Name} every {RegionEvery} s ({new Uri(place.Endpoint.Contains("://") ? place.Endpoint : "tcp://" + place.Endpoint).Host})");
				}
				else if (targetHop == 0) info.Add("Pinging the server itself, once a second");
				else
				{
					string where = inNetwork ? "where " + place?.Network + " begins" : "before " + (place?.Network ?? "the server's network");
					info.Add($"Pinging {target} once a second (hop {targetHop}, {where})");
					if (offset != null) info.Add($"+ {offset:0} ms on to {r.Label} (measured at the start and every {Recalibrate / 60} min)");
					else r.Note = $"hop {targetHop} only, not the whole way";
				}
				r.Info = info.ToArray();
				Stats.Fill(r, samples, Stats.Now(), regionMode ? RegionEvery + 3 : 4);
				return r;
			}
		}
	}
}
