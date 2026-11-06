using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace MatchPing
{
	/// <summary>Measures on this PC.
	/// - The match server is the address the game sends the most UDP to (Windows' network tracing, see Etw).
	/// - Game servers do not answer pings, so when the match starts one traceroute finds the last router on the way
	///   that does; it is pinged once a second (~0.4 MB an hour): that shows the line's spikes and losses as they come.
	/// - That router is usually where the cloud's network begins, near the player: Google carries the rest of the way
	///   on its own backbone, which does not answer pings. But while matchmaking the game itself times candidate
	///   servers (UDP to port 7600; the one it then plays on among them), and the tracing sees those packets go and
	///   come back. The difference between that round trip and the router's is added to every ping. Nothing extra is
	///   sent for it, and nothing goes to anyone but the routers on the way.
	/// - When nothing on the way answers pings, the game's own round trip is shown, as measured before the match.</summary>
	sealed class Direct : ISource
	{
		static readonly byte[] Payload = new byte[16];
		const int MinRate = 10;        // packets a second: a match (the game sends 30-60), not a menu's odd packet
		const int SwitchAfter = 3;     // seconds another server must lead before it is the match server
		const int OverAfter = 20;      // seconds without a match flow: the match is over
		const int ChecksFor = 10;      // minutes the game's own checks count for a match

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
		double? offset;                // ms from that router on to the server (added to its pings)
		double? gameRtt;               // the game's own round trip, before the match
		bool gameExact;                // to this very server (else to others in its region)
		bool calibrating, calibrated;

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
				if (target != null)
				{
					ping = target;
					if (targetHop > 0 && !calibrating && !calibrated)
					{
						calibrating = true;
						var t = target;
						var ip = Etw.Address(server);
						var p = place;
						ThreadPool.QueueUserWorkItem(_ => Calibrate(g, t, ip, p));
					}
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
			offset = gameRtt = null;
			gameExact = false;
			routing = calibrating = calibrated = false;
			samples.Clear();
			gen++;
		}

		void Route(int g, IPAddress ip, Regions.Place p)
		{
			var (found, hop, isIn) = Probe(regions, ip, p?.Provider, null);
			var (rtt, exact) = GameRtt(ip, p);
			lock (gate)
			{
				if (g != gen) return;
				routing = false;
				target = found;
				targetHop = hop;
				inNetwork = isIn;
				gameRtt = rtt;
				gameExact = exact;
			}
		}

		/// <summary>What the game measured before the match: to this very server (its port 7600), else the best to the
		/// other servers it checked in the same region; the lowest of the bursts.</summary>
		(double? rtt, bool exact) GameRtt(IPAddress ip, Regions.Place p)
		{
			var checks = etw.Checks(ChecksFor);
			var mine = checks.Where(c => c.Key.Equals(ip)).Select(c => c.Value).ToList();
			if (mine.Count > 0) return (mine.Min(), true);
			if (p == null) return (null, false);
			var near = checks.Where(c => regions.Lookup(c.Key)?.Region == p.Region).Select(c => c.Value).ToList();
			return near.Count > 0 ? (near.Min(), false) : ((double?)null, false);
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

		/// <summary>The way on from the router to the server: the game's round trip minus the router's (the lowest of
		/// 5 pings now). Once per match: the game measures only while matchmaking.</summary>
		void Calibrate(int g, IPAddress hop, IPAddress ip, Regions.Place p)
		{
			double? h = null;
			for (int i = 0; i < 5; i++)
			{
				var r = Echo(hop, 1);
				if (r != null && (h == null || r < h)) h = r;
				Thread.Sleep(100);
			}
			var (rtt, exact) = GameRtt(ip, p);
			lock (gate)
			{
				if (g != gen) return;
				calibrating = false;
				calibrated = true;
				if (rtt != null) { gameRtt = rtt; gameExact = exact; }
				if (h != null && gameRtt != null) offset = Math.Max(0, gameRtt.Value - h.Value);
			}
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
					if (g == gen) Add(new Sample { Ts = now, Ping = ms });   // the router's own; Read adds the way on
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
				string game = gameRtt == null ? null : $"the game measured {gameRtt:0} ms to {(gameExact ? "this server" : "other " + r.Label + " servers")} before the match";
				if (routing) r.Status = "finding a router near the server…";
				else if (target != null && targetHop > 0 && !calibrated) r.Status = "measuring…";
				if (r.Status != null) { info.Add(r.Status); r.Info = info.ToArray(); return r; }

				if (target == null)
				{
					// nothing on the way answers pings: what the game measured, if anything
					info.Add("Nothing on the way to the server answers pings" + (game != null ? "; " + game : ""));
					r.Info = info.ToArray();
					if (gameRtt == null) { r.Status = "nothing on the way to the server answers pings"; return r; }
					r.Now = gameRtt;
					r.Level = Level.Normal;
					r.Note = "measured by the game before the match";
					return r;
				}
				if (targetHop == 0) info.Add("Pinging the server itself, once a second");
				else
				{
					string where = inNetwork ? "where " + place?.Network + " begins" : "before " + (place?.Network ?? "the server's network");
					info.Add($"Pinging {target} once a second (hop {targetHop}, {where})");
					if (offset != null) info.Add($"+ {offset:0} ms on to the server: {game}");
					else r.Note = $"hop {targetHop} only, not the whole way";
				}
				r.Info = info.ToArray();
				// the way on, added to every ping kept (also those from before it was known): the line stays one line
				double add = offset ?? 0;
				Stats.Fill(r, samples.Select(s => new Sample { Ts = s.Ts, Measured = s.Measured, Ping = s.Ping + add }).ToList(), Stats.Now(), 4);
				return r;
			}
		}
	}
}
