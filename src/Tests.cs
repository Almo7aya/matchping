using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace MatchPing
{
	/// <summary>Checks that need administrator rights and a real game.</summary>
	static class Tests
	{
		struct Ev { public bool Sent; public long Ts; public uint Daddr, Saddr; public int Dport, Sport, Size; }

		/// <summary>--beacon-test file: records the game's UDP besides the match traffic (sends and receives, with their
		/// times) from the menus until 150 s into a match, then pairs each packet sent to an address with the next one
		/// back from it: does the game keep measuring its regions during a match, and what round trips does it see?
		/// Only watches; sends nothing.</summary>
		public static void BeaconTest(string file)
		{
			var outp = new List<string> { $"MatchPing --beacon-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
			var events = new List<Ev>();
			int[] pids = Game();
			var etw = new Etw("MatchPing-test");
			etw.Raw = (id, ts, b) =>
			{
				int pid = BitConverter.ToInt32(b, 0);
				var p = pids;
				if (Array.IndexOf(p, pid) < 0) return;
				var e = new Ev
				{
					Sent = id == 42, Ts = ts, Size = BitConverter.ToInt32(b, 4),
					Daddr = BitConverter.ToUInt32(b, 8), Saddr = BitConverter.ToUInt32(b, 12),
					Dport = b[16] << 8 | b[17], Sport = b[18] << 8 | b[19],
				};
				lock (events) events.Add(e);
			};
			etw.SetPids(pids);
			if (!etw.Start()) { File.WriteAllLines(file, outp.Concat(new[] { "start failed: " + etw.Error })); return; }
			long t0 = Stopwatch.GetTimestamp();
			outp.Add($"{DateTime.Now:HH:mm:ss} recording; game pids {string.Join(",", pids)}; waiting for a match (up to 30 min)");
			File.WriteAllLines(file, outp);

			ulong server = 0;
			long matchTs = 0;   // event times are 100 ns steps (FILETIME, UTC)
			for (int s = 0; s < 1800 + 150; s++)
			{
				Thread.Sleep(1000);
				if (s % 5 == 0) { pids = Game(); etw.SetPids(pids); }
				foreach (var kv in etw.Take())
					if (server == 0 && kv.Value >= 10)
					{
						server = kv.Key;
						matchTs = DateTime.UtcNow.ToFileTimeUtc();
						outp.Add($"{DateTime.Now:HH:mm:ss} match server {Etw.Address(kv.Key)}:{Etw.Port(kv.Key)}; recording 150 s more");
						File.WriteAllLines(file, outp);
					}
				if (server != 0 && DateTime.UtcNow.ToFileTimeUtc() - matchTs > 150 * 10000000L) break;
				if (server == 0 && s >= 1800) break;
			}
			etw.Stop();
			if (server == 0) { outp.Add("no match in 30 min"); File.WriteAllLines(file, outp); return; }

			List<Ev> all;
			lock (events) all = events.OrderBy(e => e.Ts).ToList();
			// this PC's address: the source of most packets sent
			uint local = all.Where(e => e.Sent).GroupBy(e => e.Saddr).OrderByDescending(g => g.Count()).First().Key;
			ulong Key(uint a, int port) => (ulong)a << 16 | (uint)port;
			ulong RemoteOf(Ev e) => e.Sent ? Key(e.Daddr, e.Dport) : e.Daddr == local ? Key(e.Saddr, e.Sport) : Key(e.Daddr, e.Dport);
			double Sec(long ts) => (ts - matchTs) / 1e7;   // seconds from the match start
			var regions = new Regions();
			regions.Load();

			var byRemote = all.GroupBy(RemoteOf).ToList();
			var sv = byRemote.FirstOrDefault(g => g.Key == server);
			outp.Add($"local address {new IPAddress(local)}; {all.Count} game UDP events; match server: {sv?.Count(e => e.Sent)} sent, {sv?.Count(e => !e.Sent)} received");
			outp.Add("other addresses (seconds are from the match start; negative = in the menus):");
			foreach (var g in byRemote.Where(g => g.Key != server).OrderBy(g => Etw.Port(g.Key)).ThenByDescending(g => g.Count()))
			{
				var sent = g.Where(e => e.Sent).ToList();
				var got = g.Where(e => !e.Sent).ToList();
				// each packet sent, paired with the next one back within a second
				var rtts = new List<double>();
				int j = 0;
				foreach (var s in sent)
				{
					while (j < got.Count && got[j].Ts <= s.Ts) j++;
					if (j < got.Count && got[j].Ts - s.Ts < 10000000L) { rtts.Add((got[j].Ts - s.Ts) / 10000.0); j++; }
				}
				var ip = Etw.Address(g.Key);
				var place = regions.Lookup(ip);
				double first = Sec(g.First().Ts), last = Sec(g.Last().Ts);
				int inMatch = sent.Count(e => Sec(e.Ts) >= 0);
				rtts.Sort();
				outp.Add($"  {ip}:{Etw.Port(g.Key)} {(place != null ? place.Cloud + " " + place.Region + " (" + place.Name + ")" : "")}");
				outp.Add($"     sent {sent.Count} ({inMatch} during the match), received {got.Count}, sizes {string.Join("/", sent.Select(e => e.Size).Distinct().Take(4))}, from {first:0} s to {last:0} s");
				if (rtts.Count > 0)
					outp.Add($"     round trips: {rtts.Count} paired, min {rtts[0]:0.0} ms, median {rtts[rtts.Count / 2]:0.0} ms, max {rtts[rtts.Count - 1]:0.0} ms");
				var times = sent.Select(e => Sec(e.Ts)).ToList();
				if (times.Count > 1)
					outp.Add("     sends at s: " + string.Join(" ", times.Take(40).Select(t => t.ToString("0"))) + (times.Count > 40 ? " …" : ""));
			}
			File.WriteAllLines(file, outp);
		}

		static int[] Game()
		{
			var ps = Process.GetProcessesByName("Discovery");
			var ids = ps.Select(p => p.Id).ToArray();
			foreach (var p in ps) p.Dispose();
			return ids;
		}
	}
}
