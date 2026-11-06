using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("MatchPing")]
[assembly: AssemblyProduct("MatchPing")]
[assembly: AssemblyDescription("Live ping overlay for THE FINALS")]
[assembly: AssemblyCopyright("MIT License")]
// the version attributes come from VERSION: build.ps1 writes them into dist\obj\Version.cs

namespace MatchPing
{
	/// <summary>MatchPing: your ping to the match server, on top of THE FINALS. Lives in the tray; shows while a match is
	/// on. Ctrl+Alt+P turns it on and off. Seeing which server the game talks to needs administrator rights (Windows'
	/// network tracing): it asks once, at the start. Arguments: --demo (made-up pings), --probe ip [file] (where it
	/// would ping for that server, and why), --snapshot file.png (every state drawn into a picture, nothing shown),
	/// --etw-test file.txt [--wait-match] (checks Windows' network tracing and writes what it saw).</summary>
	static class App
	{
		public const string Version = Build.Version;
		public const string Repo = "https://github.com/Almo7aya/matchping";

		static Settings set;
		static Regions regions;
		static ISource source;
		static bool demo, admin;
		static OverlayWindow win;
		static Forms.NotifyIcon tray;
		static HwndSource keys;
		static bool gameRunning, hotkey;
		static int[] gamePids = new int[0];
		static Reading last = new Reading();
		static int ticks;
		static Forms.ToolStripMenuItem miOn, miMenus, miMove, miDetails, miGraph, miStartup;
		static Forms.ToolStripMenuItem[] miInfo, miSizes, miBoxes;
		static readonly double[] Sizes = { 0.8, 1, 1.25, 1.5, 2 };
		static readonly double[] Boxes = { 0, 0.35, 0.55, 0.75 };
		const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

		[STAThread]
		static void Main(string[] args)
		{
			string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
			if (Arg("--write-icon") is string ico) { WriteIcon(ico); return; }
			if (Arg("--snapshot") is string snap) { SnapshotStates(snap); return; }
			if (Arg("--etw-test") is string test) { EtwTest(test, args.Contains("--wait-match")); return; }
			if (Arg("--beacon-test") is string beacon) { Tests.BeaconTest(beacon); return; }
			if (Arg("--probe") is string probe) { Probe(probe, Arg(probe) ?? "matchping-probe.txt"); return; }
			demo = args.Contains("--demo");
			bool elevated = args.Contains("--elevated");
			string id = @"Local\MatchPing" + (demo ? "Demo" : "");
			if (!elevated && Mutex.TryOpenExisting(id, out var other)) { other.Dispose(); return; }   // one runs already: it is in the tray

			set = Settings.Load();
			// seeing the game's traffic needs administrator rights: ask once, now (a no: it goes on and says so)
			if (!demo && !elevated && !IsAdmin() && Elevate(args)) return;

			using (var mutex = new Mutex(false, id))
			{
				bool mine;
				try { mine = mutex.WaitOne(elevated ? 8000 : 0); } catch (AbandonedMutexException) { mine = true; }
				if (!mine) return;
				try { Run(); } finally { mutex.ReleaseMutex(); }
			}
		}

		static void Run()
		{
			var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
			app.DispatcherUnhandledException += (s, e) => e.Handled = true;   // an overlay must not pop up errors over a game
			admin = IsAdmin();
			regions = new Regions();
			if (!demo) regions.Load();
			source = demo ? (ISource)new Demo() : new Direct(regions, () => gamePids);
			source.Start();

			win = new OverlayWindow(set);
			win.MovingChanged += () => { Menu(); Tick(); };
			tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "MatchPing", ContextMenuStrip = BuildMenu(), Visible = true };
			tray.DoubleClick += (s, e) => Toggle();
			Hotkey();
			Menu();

			var ui = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			ui.Tick += (s, e) => Tick();
			ui.Start();
			var games = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
			games.Tick += (s, e) => CheckGame();
			games.Start();
			CheckGame();
			Tick();
			if (!File.Exists(Settings.FilePath) || demo)
			{
				set.Save();
				tray.ShowBalloonTip(8000, "MatchPing", "It shows your ping over THE FINALS during matches. Ctrl+Alt+P turns it on and off; " +
					"right-click this icon to move it or change its size.", Forms.ToolTipIcon.Info);
			}
			app.Run();

			source.Active = false;
			Thread.Sleep(300);   // lets the measuring thread stop Windows' network tracing
			tray.Visible = false;
			tray.Dispose();
			keys?.Dispose();
		}

		/// <summary>Once a second: measure while the game runs, show the overlay during matches.</summary>
		static void Tick()
		{
			bool game = demo || gameRunning;
			source.Active = set.Enabled && game;
			var r = source.Read();
			bool show = win.Moving || set.Enabled && game && (r.InMatch || set.InMenus);
			if (show)
			{
				win.View.Show(r);
				if (!win.IsVisible) win.Show();
			}
			else if (win.IsVisible) win.Hide();
			string state = !set.Enabled ? "off (Ctrl+Alt+P)" : !game ? "waiting for THE FINALS" : r.Lost ? "lost" : r.Now != null && r.Status == null ? Math.Round(r.Now.Value) + " ms" : r.Status ?? "…";
			string text = "MatchPing · " + state;
			tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;
			last = r;
			if (game && ++ticks % 5 == 0)
				try { Directory.CreateDirectory(Settings.LocalDir); File.WriteAllText(Path.Combine(Settings.LocalDir, "status.txt"), Details(r)); } catch (Exception) { }
			if (miInfo != null)
			{
				var info = (!demo && !admin ? new[] { "Needs administrator rights to see the game's traffic: restart MatchPing" } : r.Info)
					.Concat(Enumerable.Repeat("", miInfo.Length)).Take(miInfo.Length).ToArray();
				for (int i = 0; i < miInfo.Length; i++) { miInfo[i].Text = info[i]; miInfo[i].Visible = info[i].Length > 0; }
			}
		}

		/// <summary>What it measures and the numbers, for status.txt (every 5 s while the game runs) and "Copy details".</summary>
		static string Details(Reading r)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			string N(double? v) => v != null ? v.Value.ToString("0.#", inv) : "-";
			var lines = new List<string>
			{
				$"MatchPing {Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · administrator: {admin} · game running: {gameRunning}",
				$"shown: {(r.Lost ? "lost" : N(r.Now) + " ms")} · median {N(r.Median)} · jitter {N(r.Jitter)} · loss {N(r.Loss)} % · {r.Level}" +
					(r.Status != null ? " · " + r.Status : "") + (r.Note != null ? " · " + r.Note : ""),
			};
			lines.AddRange(r.Info);
			lines.Add("last pings (s ago: ms): " + string.Join("  ", r.Last60.Skip(Math.Max(0, r.Last60.Count - 15))
				.Select(s => $"{Stats.Now() - s.Ts}:{(s.Ping != null ? N(s.Ping) : "x")}")));
			return string.Join(Environment.NewLine, lines) + Environment.NewLine;
		}

		static void CheckGame()
		{
			bool was = gameRunning;
			try
			{
				var ps = Process.GetProcessesByName(set.Game);
				gamePids = ps.Select(p => p.Id).ToArray();
				foreach (var p in ps) p.Dispose();
			}
			catch (Exception) { gamePids = new int[0]; }
			gameRunning = gamePids.Length > 0;
			if (was != gameRunning) Tick();
		}

		static void Toggle()
		{
			set.Enabled = !set.Enabled;
			set.Save();
			Menu();
			Tick();
		}

		// ---- administrator rights ----

		static bool IsAdmin()
		{
			using (var id = WindowsIdentity.GetCurrent())
				return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
		}

		/// <summary>Start again with administrator rights (the UAC prompt); false when the user said no.</summary>
		static bool Elevate(string[] args)
		{
			try
			{
				string a = string.Join(" ", args.Select(x => "\"" + x.Replace("\"", "") + "\"").Concat(new[] { "--elevated" }));
				Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, a) { UseShellExecute = true, Verb = "runas" })?.Dispose();
				return true;
			}
			catch (Win32Exception) { return false; }
		}

		// ---- the tray menu ----

		static Forms.ContextMenuStrip BuildMenu()
		{
			var m = new Forms.ContextMenuStrip();
			var head = Item(m, "MatchPing " + Version, null);
			head.Enabled = false;
			miInfo = Enumerable.Range(0, 3).Select(_ => { var i = Item(m, "", null); i.Enabled = false; return i; }).ToArray();
			m.Items.Add(new Forms.ToolStripSeparator());
			miOn = Item(m, "Show the ping overlay", Toggle);
			miMenus = Item(m, "Also outside matches", () => { set.InMenus = !set.InMenus; Changed(); });
			m.Items.Add(new Forms.ToolStripSeparator());
			miMove = Item(m, "Move the overlay", () => win.SetMoving(!win.Moving));
			var size = new Forms.ToolStripMenuItem("Size");
			miSizes = Sizes.Select(v => Sub(size, v == 1 ? "Normal" : Math.Round(v * 100) + " %", () => { set.Scale = v; Changed(); })).ToArray();
			m.Items.Add(size);
			var box = new Forms.ToolStripMenuItem("Background");
			miBoxes = Boxes.Select((v, i) => Sub(box, new[] { "None (text shadow only)", "Light", "Medium", "Dark" }[i], () => { set.Backdrop = v; Changed(); })).ToArray();
			m.Items.Add(box);
			miDetails = Item(m, "Region, loss and jitter line", () => { set.Details = !set.Details; Changed(); });
			miGraph = Item(m, "Last 60 s graph", () => { set.Graph = !set.Graph; Changed(); });
			m.Items.Add(new Forms.ToolStripSeparator());
			miStartup = Item(m, "Start with Windows", () => Startup(!Startup()));
			Item(m, "Copy details (for a bug report)", () => { try { Forms.Clipboard.SetText(Details(last)); } catch (Exception) { } });
			Item(m, "About MatchPing (GitHub)", () => Open(Repo));
			m.Items.Add(new Forms.ToolStripSeparator());
			Item(m, "Quit", () => Application.Current.Shutdown());
			m.Opening += (s, e) => { Menu(); Tick(); };
			return m;
		}

		static void Changed()
		{
			set.Save();
			win.Configure();
			Menu();
			Tick();
		}

		static void Menu()
		{
			if (miOn == null) return;
			miOn.Checked = set.Enabled;
			miOn.Text = "Show the ping overlay" + (hotkey ? " (Ctrl+Alt+P)" : "");
			miMenus.Checked = set.InMenus;
			miMove.Text = win.Moving ? "Lock the overlay here" : "Move the overlay";
			miDetails.Checked = set.Details;
			miGraph.Checked = set.Graph;
			for (int i = 0; i < Sizes.Length; i++) miSizes[i].Checked = Math.Abs(set.Scale - Sizes[i]) < 0.01;
			for (int i = 0; i < Boxes.Length; i++) miBoxes[i].Checked = Math.Abs(set.Backdrop - Boxes[i]) < 0.01;
			miStartup.Checked = Startup();
		}

		static Forms.ToolStripMenuItem Item(Forms.ContextMenuStrip m, string text, Action a)
		{
			var i = new Forms.ToolStripMenuItem(text);
			if (a != null) i.Click += (s, e) => a();
			m.Items.Add(i);
			return i;
		}

		static Forms.ToolStripMenuItem Sub(Forms.ToolStripMenuItem parent, string text, Action a)
		{
			var i = new Forms.ToolStripMenuItem(text);
			i.Click += (s, e) => a();
			parent.DropDownItems.Add(i);
			return i;
		}

		static void Open(string url)
		{
			try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch (Exception) { }
		}

		// ---- start with Windows: a scheduled task when it runs with administrator rights (no UAC prompt at each
		// sign-in), else the Run key ----

		static bool Startup()
		{
			try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) if (k?.GetValue("MatchPing") != null) return true; } catch (Exception) { }
			return Schtasks("/Query /TN MatchPing") == 0;
		}

		static void Startup(bool on)
		{
			string exe = Assembly.GetEntryAssembly().Location;
			try { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("MatchPing", false); } catch (Exception) { }
			if (Schtasks("/Query /TN MatchPing") == 0) Schtasks("/Delete /TN MatchPing /F");
			if (on)
			{
				if (IsAdmin())
				{
					string user = WindowsIdentity.GetCurrent().Name;
					string xml = Path.Combine(Path.GetTempPath(), "MatchPing-task.xml");
					File.WriteAllText(xml, $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>MatchPing: the ping overlay for THE FINALS, at sign-in</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(exe)}</Command></Exec></Actions>
</Task>", Encoding.Unicode);
					Schtasks($"/Create /F /TN MatchPing /XML \"{xml}\"");
					try { File.Delete(xml); } catch (Exception) { }
				}
				else
					try { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("MatchPing", "\"" + exe + "\""); } catch (Exception) { }
			}
			Menu();
		}

		static string Esc(string s) => System.Security.SecurityElement.Escape(s);

		static int Schtasks(string args)
		{
			try
			{
				using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
				{
					p.StandardOutput.ReadToEnd();
					p.WaitForExit(10000);
					return p.ExitCode;
				}
			}
			catch (Exception) { return -1; }
		}

		// ---- Ctrl+Alt+P, on a message-only window ----

		static void Hotkey()
		{
			keys = new HwndSource(new HwndSourceParameters("MatchPingKeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
			keys.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
			{
				if (msg == 0x0312 && w.ToInt64() == 1) { Toggle(); handled = true; }   // WM_HOTKEY
				return IntPtr.Zero;
			});
			hotkey = RegisterHotKey(keys.Handle, 1, 0x1 | 0x2 | 0x4000, 0x50);   // Alt + Ctrl, no repeat, P
		}

		[DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);

		// ---- --etw-test file [--wait-match]: is Windows' network tracing readable here, and does it decode? With
		// --wait-match it then waits (up to 30 min) for a match and writes which server the game talked to ----

		static void EtwTest(string file, bool waitMatch)
		{
			var lines = new List<string>();
			int own = Process.GetCurrentProcess().Id;
			int[] game = Process.GetProcessesByName("Discovery").Select(p => p.Id).ToArray();
			int total = 0;
			var etw = new Etw
			{
				Dump = (pid, b) =>
				{
					lock (lines)
					{
						total++;
						if ((pid == own || game.Contains(pid)) && lines.Count < 12)
							lines.Add($"  pid {pid}{(pid == own ? " (this test)" : " (the game)")}: {BitConverter.ToString(b)}");
					}
				}
			};
			etw.SetPids(game.Concat(new[] { own }).ToArray());
			var outp = new List<string> { $"MatchPing {Version} --etw-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}", $"administrator: {IsAdmin()}, this test pid {own}, game pids {string.Join(",", game)}" };
			if (!etw.Start()) { outp.Add("start failed: " + etw.Error); File.WriteAllLines(file, outp); return; }
			Thread.Sleep(1500);
			using (var u = new UdpClient())
				for (int i = 0; i < 5; i++) { u.Send(new byte[] { 1, 2, 3 }, 3, "192.0.2.1", 7777); Thread.Sleep(100); }   // TEST-NET-1: nobody answers
			Thread.Sleep(4500);
			var counts = etw.Take();
			lock (lines)
			{
				outp.Add($"UDPv4 send events seen (all processes): {total}");
				outp.Add("decoded, this test and the game (expect 192.0.2.1:7777 x5 from the test):");
				foreach (var kv in counts.OrderByDescending(k => k.Value)) outp.Add($"  {Etw.Address(kv.Key)}:{Etw.Port(kv.Key)}  {kv.Value} packets");
				outp.Add("raw event data (first bytes):");
				outp.AddRange(lines);
			}
			File.WriteAllLines(file, outp);
			if (waitMatch)
			{
				etw.Dump = null;
				var regions = new Regions();
				regions.Load();
				outp.Add($"{DateTime.Now:HH:mm:ss} waiting for a match (the game sending 10+ packets a second to one address)…");
				File.WriteAllLines(file, outp);
				var seen = new Dictionary<ulong, int>();
				int found = -1;
				for (int s = 0; s < 1800; s++)
				{
					Thread.Sleep(1000);
					if (s % 5 == 0) etw.SetPids(Process.GetProcessesByName("Discovery").Select(p => p.Id).ToArray());
					foreach (var kv in etw.Take())
					{
						seen.TryGetValue(kv.Key, out int c);
						seen[kv.Key] = c + kv.Value;
						if (kv.Value >= 10 && found < 0) { found = s; outp.Add($"{DateTime.Now:HH:mm:ss} a match: {Etw.Address(kv.Key)}:{Etw.Port(kv.Key)} ({kv.Value} packets in that second)"); }
					}
					if (found >= 0 && s - found >= 10) break;
				}
				outp.Add(found < 0 ? "no match in 30 min" : "what the game sent in the last ~10 s, by address:");
				foreach (var kv in seen.OrderByDescending(k => k.Value).Take(8))
				{
					var ip = Etw.Address(kv.Key);
					var place = regions.Lookup(ip);
					outp.Add($"  {ip}:{Etw.Port(kv.Key)}  {kv.Value} packets{(place != null ? $"  {place.Cloud} {place.Region} ({place.Name})" : "")}");
				}
			}
			etw.Stop();
			File.WriteAllLines(file, outp);
		}

		// ---- --probe <server> [file]: where MatchPing would ping for that server, and why ----

		static void Probe(string server, string file)
		{
			var outp = new List<string> { $"MatchPing {Version} --probe {server} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
			if (!IPAddress.TryParse(server, out var ip)) { File.WriteAllLines(file, new[] { "not an IPv4 address: " + server }); return; }
			var regions = new Regions();
			regions.Load(wait: true);
			var place = regions.Lookup(ip);
			outp.Add($"address lists: {regions.Count} ranges; the server is in {(place != null ? $"{place.Cloud} {place.Region} ({place.Name})" : "no known cloud")}");
			var (target, hop, inNetwork) = Direct.Probe(regions, ip, place?.Provider, outp.Add);
			outp.Add(target == null ? "result: nothing on the way answers pings" :
				$"result: ping {target} once a second" + (hop == 0 ? " (the server)" : $" (hop {hop}, {(inNetwork ? "where " + place?.Network + " begins" : "before " + (place?.Network ?? "the server's network"))})"));
			if (target != null && hop > 0)
				outp.Add("in a match, the way on from that router to the server comes from the game's own checks while matchmaking " +
					"(they need the game and administrator rights, so --probe can't show them)");
			File.WriteAllLines(file, outp);
		}

		// ---- snapshots: draw into a picture, show nothing ----

		static void SnapshotStates(string file)
		{
			var s = new Settings();
			var panel = new StackPanel { Margin = new Thickness(18) };
			void Add(string kind, double box, bool moving)
			{
				s.Backdrop = box;
				var v = new OverlayView { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
				v.Configure(s, moving);
				v.Show(Demo.Fake(kind));
				panel.Children.Add(v);
			}
			foreach (var k in new[] { "normal", "high", "lost", "before", "finding", "menus" }) Add(k, 0.55, false);
			Add("normal", 0, false);
			Add("normal", 0.55, true);
			var root = new Border
			{
				Background = new Media.LinearGradientBrush(Media.Color.FromRgb(0xb9, 0xcf, 0xe0), Media.Color.FromRgb(0x35, 0x3a, 0x33), 90),
				Child = panel,
			};
			root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
			root.Arrange(new Rect(root.DesiredSize));
			root.UpdateLayout();
			var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, Media.PixelFormats.Pbgra32);
			bmp.Render(root);
			var enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(bmp));
			using (var f = File.Create(file)) enc.Save(f);
		}

		// ---- the icon: a ping line ending in a dot, on dark ----

		static Bitmap Draw(int size)
		{
			var bmp = new Bitmap(size, size);
			using (var g = Graphics.FromImage(bmp))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				using (var bg = new SolidBrush(Color.FromArgb(0x1d, 0x23, 0x2b)))
				using (var path = Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
					g.FillPath(bg, path);
				float s = size;
				var line = new[] { new PointF(s * .14f, s * .62f), new PointF(s * .32f, s * .62f), new PointF(s * .42f, s * .30f),
					new PointF(s * .54f, s * .74f), new PointF(s * .64f, s * .52f), new PointF(s * .74f, s * .52f) };
				using (var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.085f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
					g.DrawLines(pen, line);
				float d = s * 0.2f;
				using (var dot = new SolidBrush(Color.FromArgb(0x3d, 0xd6, 0x8c)))
					g.FillEllipse(dot, s * .74f - d / 2 + s * 0.04f, s * .52f - d / 2, d, d);
			}
			return bmp;
		}

		static GraphicsPath Rounded(RectangleF r, float radius)
		{
			var p = new GraphicsPath();
			float d = radius * 2;
			p.AddArc(r.X, r.Y, d, d, 180, 90);
			p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
			p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
			p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
			p.CloseFigure();
			return p;
		}

		static Icon TrayIcon() => Icon.FromHandle(Draw(32).GetHicon());

		static void WriteIcon(string file)
		{
			// an .ico with PNG images: header, one 16-byte entry per size, then the images
			var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
			var pngs = sizes.Select(s => { using (var b = Draw(s)) using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); } }).ToArray();
			using (var w = new BinaryWriter(File.Create(file)))
			{
				w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
				int offset = 6 + 16 * sizes.Length;
				for (int i = 0; i < sizes.Length; i++)
				{
					w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
					w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
					w.Write(pngs[i].Length); w.Write(offset);
					offset += pngs[i].Length;
				}
				foreach (var p in pngs) w.Write(p);
			}
		}
	}
}
