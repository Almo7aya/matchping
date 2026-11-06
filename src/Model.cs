using System;
using System.Collections.Generic;
using System.Linq;

namespace MatchPing
{
	/// <summary>One ping, by the second it was sent (Ping null: no reply; Measured false: nothing was sent).</summary>
	sealed class Sample
	{
		public long Ts;
		public double? Ping;
		public bool Measured = true;
	}

	enum Level { Normal, High, Bad, Unknown }

	/// <summary>What the overlay shows.</summary>
	sealed class Reading
	{
		public bool InMatch;              // a match is on (the overlay shows only then, unless told otherwise)
		public string Status;             // instead of numbers: what is going on ("finding a router near the server…")
		public string Label = "";         // the server's region
		public string Note;               // a caveat ("hop 9 only, not the whole way")
		public double? Now;               // the latest ping
		public bool Lost;                 // the latest ping got no reply
		public double? Median, Jitter, Loss;   // the last 60 s
		public Level Level = Level.Unknown;
		public List<Sample> Last60 = new List<Sample>();
		public string[] Info = new string[0];  // for the tray menu
	}

	/// <summary>Where the pings come from: measured on this PC (Direct), or made up (Demo).</summary>
	interface ISource
	{
		bool Active { get; set; }   // measure (true) or rest
		Reading Read();
		void Start();
	}

	static class Stats
	{
		public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		/// <summary>The numbers from 1 s pings (oldest first). fresh: how old (s) the newest may be to count as now.</summary>
		public static void Fill(Reading r, List<Sample> samples, long now, int fresh)
		{
			long last = samples.Count > 0 ? samples[samples.Count - 1].Ts : now;
			r.Last60 = samples.Where(s => s.Ts > last - 60).ToList();
			var measured = r.Last60.Where(s => s.Measured).ToList();
			var ok = measured.Where(s => s.Ping != null).Select(s => s.Ping.Value).ToList();
			if (ok.Count >= 5)
			{
				r.Median = Median(ok);
				double j = 0;
				for (int i = 1; i < ok.Count; i++) j += Math.Abs(ok[i] - ok[i - 1]);
				r.Jitter = j / (ok.Count - 1);
			}
			if (measured.Count >= 5) r.Loss = 100.0 * (measured.Count - ok.Count) / measured.Count;

			var latest = samples.Count > 0 ? samples[samples.Count - 1] : null;
			if (latest != null && latest.Measured && now - latest.Ts <= fresh)
			{
				r.Now = latest.Ping;
				r.Lost = latest.Ping == null;
			}

			// the colour: against the last minute's median (a spike is 50 ms over it)
			bool recentLoss = measured.Any(s => s.Ping == null && s.Ts > last - 10);
			if (r.Lost) r.Level = Level.Bad;
			else if (r.Now == null) r.Level = Level.Unknown;
			else if (r.Now >= 300 || r.Median != null && r.Now >= r.Median + 50) r.Level = Level.Bad;
			else if (r.Median != null && r.Now >= r.Median + 25 || recentLoss) r.Level = Level.High;
			else r.Level = Level.Normal;
		}

		static double Median(List<double> v)
		{
			var s = v.OrderBy(x => x).ToList();
			int n = s.Count;
			return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
		}
	}
}
