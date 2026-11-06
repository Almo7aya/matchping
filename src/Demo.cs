using System;
using System.Collections.Generic;

namespace MatchPing
{
	/// <summary>--demo: made-up pings to look at the overlay without a game; and the fixed readings the snapshots
	/// draw. Never mixed with real measurements.</summary>
	sealed class Demo : ISource
	{
		readonly List<Sample> samples = new List<Sample>();
		readonly Random rnd = new Random(7);
		public bool Active { get; set; }
		public void Start() { }

		public Reading Read()
		{
			long now = Stats.Now();
			if (samples.Count == 0)
				for (long t = now - 60; t < now; t++) samples.Add(Make(t));
			while (samples[samples.Count - 1].Ts < now) samples.Add(Make(samples[samples.Count - 1].Ts + 1));
			samples.RemoveAll(s => s.Ts <= now - 120);
			var r = new Reading { InMatch = true, Label = "Netherlands", Info = new[] { "Demo: made-up pings" } };
			Stats.Fill(r, samples, now, 4);
			return r;
		}

		Sample Make(long t)
		{
			double v = 38 + rnd.NextDouble() * 7;
			if (t % 37 == 0) v += 70;
			return new Sample { Ts = t, Ping = t % 53 == 0 ? (double?)null : v };
		}

		/// <summary>A fixed reading: normal, high, lost, before (only to a router on the way, not calibrated), finding,
		/// menus.</summary>
		public static Reading Fake(string kind)
		{
			var r = new Reading { InMatch = kind != "menus", Label = "Netherlands" };
			if (kind == "menus") { r.Label = ""; r.Status = "waiting for a match"; return r; }
			if (kind == "finding") { r.Status = "finding a router near the server…"; return r; }
			var rng = new Random(3);
			for (int i = 0; i < 60; i++)
			{
				double? v = 37 + rng.NextDouble() * 9;
				if (i == 21 || i == 22) v += 90;
				if (kind == "lost" && i == 59) v = null;
				if (kind == "high" && i >= 57) v = 69 + i - 57;
				if (i == 40) v = null;
				r.Last60.Add(new Sample { Ts = 1000 + i, Ping = v });
			}
			r.Median = 41; r.Jitter = 3.8; r.Loss = kind == "lost" ? 3.3 : 1.7;
			switch (kind)
			{
				case "lost": r.Lost = true; r.Level = Level.Bad; break;
				case "high": r.Now = 71; r.Level = Level.High; break;
				case "before": r.Now = 23; r.Level = Level.Normal; r.Note = "hop 9 only, not the whole way"; break;
				default: r.Now = 40; r.Level = Level.Normal; break;
			}
			return r;
		}
	}
}
