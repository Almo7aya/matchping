using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MatchPing
{
	/// <summary>The overlay's face: the ping, big, in white (as usual), amber (25 ms over the last minute's median, or
	/// a loss in the last 10 s) or red (50 ms over it, 300 ms or more, or no reply); the last 60 s as a line against
	/// that median; the server's region, loss and jitter under it. Used by the window and by the snapshots.</summary>
	sealed class OverlayView : Border
	{
		static readonly FontFamily Font = new FontFamily("Segoe UI Variable Display, Segoe UI");
		static readonly Brush White = Frozen(Colors.White), Amber = Frozen(Color.FromRgb(0xff, 0xc2, 0x4d)),
			Red = Frozen(Color.FromRgb(0xff, 0x6b, 0x6b)), Grey = Frozen(Color.FromRgb(0xb4, 0xb4, 0xb4)),
			LineBrush = Frozen(Color.FromArgb(0xdd, 0xff, 0xff, 0xff)), FaintBrush = Frozen(Color.FromArgb(0x66, 0xff, 0xff, 0xff));
		readonly TextBlock ping, details, hint;
		readonly Run value, unit;
		readonly Canvas graph;
		readonly ScaleTransform scale = new ScaleTransform(1, 1);

		public OverlayView()
		{
			CornerRadius = new CornerRadius(8);
			Padding = new Thickness(10, 4, 10, 6);
			LayoutTransform = scale;
			SnapsToDevicePixels = true;
			value = new Run("—") { FontSize = 26, FontWeight = FontWeights.SemiBold };
			unit = new Run(" ms") { FontSize = 13 };
			ping = new TextBlock { FontFamily = Font, Foreground = Grey, MinWidth = 70 };
			ping.Inlines.Add(value);
			ping.Inlines.Add(unit);
			Typography.SetNumeralAlignment(ping, FontNumeralAlignment.Tabular);
			details = new TextBlock { FontFamily = Font, FontSize = 11.5, Foreground = White, Opacity = 0.85, Margin = new Thickness(0, -1, 0, 0) };
			Typography.SetNumeralAlignment(details, FontNumeralAlignment.Tabular);
			hint = new TextBlock { FontFamily = Font, FontSize = 11, Foreground = Amber, Margin = new Thickness(0, 2, 0, 2),
				Text = "Drag to move · right-click to lock", Visibility = Visibility.Collapsed };
			graph = new Canvas { Width = 90, Height = 28, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

			// the hint (moving), the number with the graph next to it, the line under them
			var grid = new StackPanel();
			var row = new StackPanel { Orientation = Orientation.Horizontal };
			row.Children.Add(ping);
			row.Children.Add(graph);
			grid.Children.Add(hint);
			grid.Children.Add(row);
			grid.Children.Add(details);
			// readable over any scene, with the box or without it
			grid.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0.9, Color = Colors.Black };
			Child = grid;
		}

		/// <summary>Size, box and parts after the settings; moving: an outline and a box to grab, and the hint.</summary>
		public void Configure(Settings s, bool moving)
		{
			scale.ScaleX = scale.ScaleY = s.Scale;
			double alpha = moving ? Math.Max(s.Backdrop, 0.6) : s.Backdrop;
			Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0x10, 0x10, 0x12));
			BorderBrush = moving ? Amber : null;
			BorderThickness = new Thickness(moving ? 1.5 : 0);
			hint.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
			graph.Visibility = s.Graph ? Visibility.Visible : Visibility.Collapsed;
			details.Visibility = s.Details ? Visibility.Visible : Visibility.Collapsed;
		}

		public void Show(Reading r)
		{
			var color = r.Level == Level.Bad ? Red : r.Level == Level.High ? Amber : r.Level == Level.Normal ? White : Grey;
			ping.Foreground = color;
			if (r.Lost) { value.Text = "lost"; unit.Text = ""; }
			else if (r.Now != null) { value.Text = Math.Round(r.Now.Value).ToString(CultureInfo.InvariantCulture); unit.Text = " ms"; }
			else { value.Text = "—"; unit.Text = ""; }
			details.Text = Detail(r);
			DrawGraph(r, color);
		}

		static string Detail(Reading r)
		{
			var parts = new List<string> { r.Label };
			if (r.Status != null) parts.Add(r.Status);
			else
			{
				if (r.Note != null) parts.Add(r.Note);
				if (r.Loss != null) parts.Add(string.Format(CultureInfo.InvariantCulture, "loss {0:0.#} %", r.Loss));
				if (r.Jitter != null) parts.Add(string.Format(CultureInfo.InvariantCulture, "jitter {0:0} ms", r.Jitter));
			}
			return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
		}

		void DrawGraph(Reading r, Brush color)
		{
			graph.Children.Clear();
			var pts = r.Last60;
			var vals = pts.Where(p => p.Ping != null).Select(p => p.Ping.Value).ToList();
			if (pts.Count < 2 || vals.Count == 0) return;
			long t1 = pts[pts.Count - 1].Ts, t0 = t1 - 59;
			double med = r.Median ?? vals.Average();
			// calm: a flat line low in the box; a spike stands out against the median
			double lo = Math.Min(vals.Min(), med - 10), hi = Math.Max(vals.Max(), med + 40);
			double W = graph.Width, H = graph.Height;
			double X(long t) => (t - t0) / 59.0 * W;
			double Y(double v) => H - 1 - (Math.Min(v, hi) - lo) / (hi - lo) * (H - 2);

			graph.Children.Add(new Line { X1 = 0, X2 = W, Y1 = Y(med), Y2 = Y(med), Stroke = FaintBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } });
			Polyline cur = null;
			foreach (var p in pts)
			{
				if (p.Ts < t0) continue;
				if (!p.Measured) { cur = null; continue; }
				if (p.Ping == null)   // no reply: a red bar the full height
				{
					graph.Children.Add(new Line { X1 = X(p.Ts), X2 = X(p.Ts), Y1 = 0, Y2 = H, Stroke = Red, StrokeThickness = 1.5 });
					cur = null;
					continue;
				}
				if (cur == null)
				{
					cur = new Polyline { Stroke = LineBrush, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
					graph.Children.Add(cur);
				}
				cur.Points.Add(new Point(X(p.Ts), Y(p.Ping.Value)));
			}
			var last = pts.LastOrDefault(p => p.Ping != null);
			if (last != null)
			{
				var dot = new Ellipse { Width = 5, Height = 5, Fill = color };
				Canvas.SetLeft(dot, X(last.Ts) - 2.5);
				Canvas.SetTop(dot, Y(last.Ping.Value) - 2.5);
				graph.Children.Add(dot);
			}
		}

		static Brush Frozen(Color c)
		{
			var b = new SolidColorBrush(c);
			b.Freeze();
			return b;
		}
	}

	/// <summary>The overlay window: on top of everything (the game in windowed full screen or DirectX 12 full screen),
	/// never focused, clicks go through it to the game, not in Alt+Tab or the taskbar. It never touches the game's
	/// process. Move mode makes it grabbable; its place is saved.</summary>
	sealed class OverlayWindow : Window
	{
		readonly Settings set;
		public readonly OverlayView View = new OverlayView();
		public event Action MovingChanged;
		IntPtr hwnd;

		public bool Moving { get; private set; }

		public OverlayWindow(Settings s)
		{
			set = s;
			Title = "MatchPing";
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			Topmost = true;
			ShowInTaskbar = false;
			ShowActivated = false;
			ResizeMode = ResizeMode.NoResize;
			SizeToContent = SizeToContent.WidthAndHeight;
			WindowStartupLocation = WindowStartupLocation.Manual;
			Content = View;
			View.Configure(s, false);
			Place();
			SourceInitialized += (o, e) => { hwnd = new WindowInteropHelper(this).Handle; Styles(); };
			MouseLeftButtonDown += (o, e) =>
			{
				if (!Moving) return;
				try { DragMove(); } catch (InvalidOperationException) { }
				set.X = Left; set.Y = Top; set.Save();
			};
			MouseRightButtonUp += (o, e) => { if (Moving) SetMoving(false); };
			// a game that puts itself on top now and then: back on top every 2 s
			var top = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
			top.Tick += (o, e) =>
			{
				if (IsVisible && hwnd != IntPtr.Zero)
					SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
			};
			top.Start();
		}

		public void Configure() => View.Configure(set, Moving);

		/// <summary>Move mode on (grab it with the mouse) or off (clicks go through; the place is saved).</summary>
		public void SetMoving(bool on)
		{
			Moving = on;
			if (hwnd != IntPtr.Zero) Styles();
			View.Configure(set, on);
			if (!on) { set.X = Left; set.Y = Top; set.Save(); }
			MovingChanged?.Invoke();
		}

		/// <summary>The saved place, or the top left corner when there is none or its screen is gone.</summary>
		void Place()
		{
			double x = set.X, y = set.Y;
			double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
			double w = SystemParameters.VirtualScreenWidth, h = SystemParameters.VirtualScreenHeight;
			if (double.IsNaN(x) || double.IsNaN(y) || x < l - 40 || y < t - 20 || x > l + w - 40 || y > t + h - 20) { x = 24; y = 24; }
			Left = x;
			Top = y;
		}

		void Styles()
		{
			int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
			ex = Moving ? ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) : ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
			SetWindowLong(hwnd, GWL_EXSTYLE, ex);
		}

		const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
		static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
		const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
		[DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
		[DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
		[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
	}
}
