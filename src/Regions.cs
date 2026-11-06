using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;

namespace MatchPing
{
	/// <summary>Which cloud region a server is in, from the address lists Google and AWS publish (downloaded once a
	/// week, ~3 MB, kept in %LOCALAPPDATA%\MatchPing\ranges.txt); and whether a router is in that cloud's network.</summary>
	sealed class Regions
	{
		public sealed class Place
		{
			public string Provider, Region;   // gcp europe-west4, aws eu-west-1
			public string Name;               // Netherlands
			public string Cloud => Provider == "gcp" ? "Google Cloud" : Provider == "aws" ? "AWS" : Provider;
			public string Network => Provider == "gcp" ? "Google's network" : Provider == "aws" ? "Amazon's network" : "the server's network";
		}

		struct Range { public uint Net; public int Len; public string Provider, Region; }

		readonly object gate = new object();
		List<Range> ranges = new List<Range>();
		static string FilePath => Path.Combine(Settings.LocalDir, "ranges.txt");

		/// <summary>The cached lists; a new download in the background when they are a week old (wait: now).</summary>
		public void Load(bool wait = false)
		{
			try { if (File.Exists(FilePath)) Use(File.ReadAllLines(FilePath)); } catch (Exception) { }
			if (!File.Exists(FilePath) || File.GetLastWriteTimeUtc(FilePath) < DateTime.UtcNow.AddDays(-7))
				if (wait) Update(); else new Thread(Update) { IsBackground = true, Name = "ranges" }.Start();
		}

		public int Count { get { lock (gate) return ranges.Count; } }

		/// <summary>The cloud region of a server, or null.</summary>
		public Place Lookup(IPAddress ip)
		{
			uint n = Num(ip);
			Range best = default;
			bool found = false;
			lock (gate)
				foreach (var r in ranges)
					if ((r.Provider == "gcp" || r.Provider == "aws") && In(n, r) && (!found || r.Len > best.Len)) { best = r; found = true; }
			return found ? new Place { Provider = best.Provider, Region = best.Region, Name = Name(best.Provider, best.Region) } : null;
		}

		/// <summary>Whether an address belongs to that cloud (gcp: anything Google, aws: anything Amazon).</summary>
		public bool InNetwork(IPAddress ip, string provider)
		{
			if (provider == null) return false;
			uint n = Num(ip);
			lock (gate)
				foreach (var r in ranges)
					if (In(n, r) && (provider == "gcp" ? r.Provider == "gcp" || r.Provider == "google" : r.Provider == "aws" || r.Provider == "amazon"))
						return true;
			return false;
		}

		void Update()
		{
			try
			{
				var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
				var lines = new List<string>();
				foreach (var p in Prefixes(js, "https://www.gstatic.com/ipranges/goog.json"))
					if (p.TryGetValue("ipv4Prefix", out var v)) lines.Add($"{v} google -");
				foreach (var p in Prefixes(js, "https://www.gstatic.com/ipranges/cloud.json"))
					if (p.TryGetValue("ipv4Prefix", out var v)) lines.Add($"{v} gcp {p["scope"]}");
				foreach (var p in Prefixes(js, "https://ip-ranges.amazonaws.com/ip-ranges.json"))
				{
					string svc = p.TryGetValue("service", out var s) ? s as string : "";
					if (svc == "EC2" || svc == "AMAZON") lines.Add($"{p["ip_prefix"]} {(svc == "EC2" ? "aws" : "amazon")} {p["region"]}");
				}
				if (lines.Count < 1000) return;   // something went wrong: keep what there is
				Directory.CreateDirectory(Settings.LocalDir);
				File.WriteAllLines(FilePath, lines);
				Use(lines.ToArray());
			}
			catch (Exception) { }   // offline: regions show as addresses until the next start
		}

		static IEnumerable<Dictionary<string, object>> Prefixes(JavaScriptSerializer js, string url)
		{
			if (js.DeserializeObject(Download(url)) is Dictionary<string, object> d && d.TryGetValue("prefixes", out var p) && p is object[] list)
				return list.OfType<Dictionary<string, object>>();
			return Enumerable.Empty<Dictionary<string, object>>();
		}

		static string Download(string url)
		{
			var req = (HttpWebRequest)WebRequest.Create(url);
			req.Timeout = 60000;
			req.UserAgent = "MatchPing";
			req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
			using (var resp = req.GetResponse())
			using (var r = new StreamReader(resp.GetResponseStream()))
				return r.ReadToEnd();
		}

		void Use(string[] lines)
		{
			var list = new List<Range>(lines.Length);
			foreach (var line in lines)
			{
				var f = line.Split(' ');
				if (f.Length < 3 || f[0] == "url") continue;
				var c = f[0].Split('/');
				if (c.Length != 2 || !IPAddress.TryParse(c[0], out var a) || !int.TryParse(c[1], out int len)) continue;
				list.Add(new Range { Net = Num(a), Len = len, Provider = f[1], Region = f[2] });
			}
			lock (gate) ranges = list;
		}

		static bool In(uint n, Range r) => r.Len == 0 || (n ^ r.Net) >> (32 - r.Len) == 0;

		static uint Num(IPAddress ip)
		{
			var b = ip.GetAddressBytes();
			return b.Length == 4 ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : 0;
		}

		public static string Name(string provider, string region)
		{
			if (region == null) return "";
			var t = provider == "gcp" ? Gcp : provider == "aws" ? Aws : null;
			return t != null && t.TryGetValue(region, out var n) ? n : region;
		}

		static readonly Dictionary<string, string> Gcp = new Dictionary<string, string>
		{
			["europe-west1"] = "Belgium", ["europe-west2"] = "London", ["europe-west3"] = "Frankfurt", ["europe-west4"] = "Netherlands",
			["europe-west6"] = "Zurich", ["europe-west8"] = "Milan", ["europe-west9"] = "Paris", ["europe-west10"] = "Berlin",
			["europe-west12"] = "Turin", ["europe-north1"] = "Finland", ["europe-north2"] = "Stockholm", ["europe-central2"] = "Warsaw",
			["europe-southwest1"] = "Madrid", ["us-central1"] = "Iowa", ["us-east1"] = "South Carolina", ["us-east4"] = "Virginia",
			["us-east5"] = "Columbus", ["us-south1"] = "Dallas", ["us-west1"] = "Oregon", ["us-west2"] = "Los Angeles",
			["us-west3"] = "Salt Lake City", ["us-west4"] = "Las Vegas", ["northamerica-northeast1"] = "Montréal",
			["northamerica-northeast2"] = "Toronto", ["northamerica-south1"] = "Querétaro", ["southamerica-east1"] = "São Paulo",
			["southamerica-west1"] = "Santiago", ["asia-east1"] = "Taiwan", ["asia-east2"] = "Hong Kong", ["asia-northeast1"] = "Tokyo",
			["asia-northeast2"] = "Osaka", ["asia-northeast3"] = "Seoul", ["asia-south1"] = "Mumbai", ["asia-south2"] = "Delhi",
			["asia-southeast1"] = "Singapore", ["asia-southeast2"] = "Jakarta", ["australia-southeast1"] = "Sydney",
			["australia-southeast2"] = "Melbourne", ["me-west1"] = "Tel Aviv", ["me-central1"] = "Doha", ["me-central2"] = "Dammam",
			["africa-south1"] = "Johannesburg",
		};

		static readonly Dictionary<string, string> Aws = new Dictionary<string, string>
		{
			["us-east-1"] = "N. Virginia", ["us-east-2"] = "Ohio", ["us-west-1"] = "N. California", ["us-west-2"] = "Oregon",
			["ca-central-1"] = "Canada", ["ca-west-1"] = "Calgary", ["mx-central-1"] = "Mexico", ["sa-east-1"] = "São Paulo",
			["eu-west-1"] = "Ireland", ["eu-west-2"] = "London", ["eu-west-3"] = "Paris", ["eu-central-1"] = "Frankfurt",
			["eu-central-2"] = "Zurich", ["eu-north-1"] = "Stockholm", ["eu-south-1"] = "Milan", ["eu-south-2"] = "Spain",
			["me-south-1"] = "Bahrain", ["me-central-1"] = "UAE", ["il-central-1"] = "Tel Aviv", ["af-south-1"] = "Cape Town",
			["ap-south-1"] = "Mumbai", ["ap-south-2"] = "Hyderabad", ["ap-southeast-1"] = "Singapore", ["ap-southeast-2"] = "Sydney",
			["ap-southeast-3"] = "Jakarta", ["ap-southeast-4"] = "Melbourne", ["ap-southeast-5"] = "Malaysia", ["ap-southeast-7"] = "Thailand",
			["ap-northeast-1"] = "Tokyo", ["ap-northeast-2"] = "Seoul", ["ap-northeast-3"] = "Osaka", ["ap-east-1"] = "Hong Kong",
		};
	}
}
