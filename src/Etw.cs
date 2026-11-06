using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace MatchPing
{
	/// <summary>Where the game sends its UDP, from Windows' own network event tracing: the
	/// Microsoft-Windows-Kernel-Network provider writes one event per packet sent, with the sending process. It is
	/// built into Windows (no driver to install), read-only, and needs administrator rights. Nothing here touches the
	/// game's process: Windows reports what goes through its network stack.</summary>
	sealed class Etw : IDisposable
	{
		static readonly Guid KernelNetwork = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
		readonly string SessionName;
		const ushort UdpV4Sent = 42;            // "UDPv4: %2 bytes transmitted from %4:%6 to %3:%5"
		const ushort UdpV4Received = 43;        // "UDPv4: %2 bytes received from %4:%6 to %3:%5"
		const ulong KeywordIpv4 = 0x10;
		// events 42 and 43: PID u32, size u32, daddr u32, saddr u32, dport u16, sport u16, ... (addresses and ports
		// in network byte order)
		const int OffPid = 0, OffDaddr = 8, OffSaddr = 12, OffDport = 16, OffSport = 18;
		// THE FINALS' own latency checks: while matchmaking, the game sends bursts of small UDP packets to port 7600 on
		// candidate servers in every region (the one it then plays on among them) and times the answers
		public const int CheckPort = 7600;
		// x64 layouts: EVENT_TRACE_PROPERTIES is 120 bytes (+ room for the names), EVENT_TRACE_LOGFILEW 448
		const int PropsSize = 120, NamesSize = 2048, LogfileSize = 448;

		readonly object gate = new object();
		Dictionary<ulong, int> counts = new Dictionary<ulong, int>();
		readonly Dictionary<ulong, Queue<long>> pending = new Dictionary<ulong, Queue<long>>();   // the game's checks waiting for an answer
		readonly List<Check> checks = new List<Check>();                                           // and their round trips
		volatile int[] pids = new int[0];
		ulong session, trace;
		IntPtr props, logfile, loggerName;
		Thread thread;
		EventRecordCallback callback;   // kept alive while Windows calls it

		public string Error { get; private set; }
		public bool Running { get; private set; }
		/// <summary>--etw-test: every UDPv4 send event (pid, the first bytes of its data).</summary>
		public Action<int, byte[]> Dump;
		/// <summary>--beacon-test: every UDPv4 send and receive event (id, QueryPerformanceCounter time, the first 20
		/// bytes of its data).</summary>
		public Action<ushort, long, byte[]> Raw;

		public Etw(string session = "MatchPing") { SessionName = session; }

		/// <summary>The game's processes: only their packets are counted.</summary>
		public void SetPids(int[] p) => pids = p ?? new int[0];

		/// <summary>Packets the game sent since the last Take, by destination (Key: address and port).</summary>
		public Dictionary<ulong, int> Take()
		{
			lock (gate)
			{
				var c = counts;
				counts = new Dictionary<ulong, int>();
				return c;
			}
		}

		public static IPAddress Address(ulong key) => new IPAddress((long)(uint)(key >> 16));
		public static int Port(ulong key) => (int)(key & 0xffff);

		public bool Start()
		{
			if (Running) return true;
			if (IntPtr.Size != 8) { Error = "needs the 64-bit build"; return false; }
			// a session left behind by a crash has the same name: stop it first
			props = Props();
			ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
			Marshal.FreeHGlobal(props);
			props = Props();
			int rc = StartTraceW(out session, SessionName, props);
			if (rc != 0)
			{
				Error = rc == 5 ? "needs administrator rights" : $"Windows network tracing did not start (error {rc})";
				Release();
				return false;
			}
			var provider = KernelNetwork;
			rc = EnableTraceEx2(session, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, KeywordIpv4, 0, 0, IntPtr.Zero);
			if (rc != 0)
			{
				Error = $"Windows network tracing refused the network events (error {rc})";
				Stop();
				return false;
			}
			logfile = Marshal.AllocHGlobal(LogfileSize);
			Zero(logfile, LogfileSize);
			loggerName = Marshal.StringToHGlobalUni(SessionName);
			Marshal.WriteIntPtr(logfile, 8, loggerName);                                                    // LoggerName
			Marshal.WriteInt32(logfile, 28, PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD);   // ProcessTraceMode
			callback = OnEvent;
			Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));             // EventRecordCallback
			trace = OpenTraceW(logfile);
			if (trace == ulong.MaxValue)
			{
				Error = $"Windows network tracing could not be read (error {Marshal.GetLastWin32Error()})";
				trace = 0;
				Stop();
				return false;
			}
			ulong t = trace;
			thread = new Thread(() => ProcessTrace(new[] { t }, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "etw" };
			thread.Start();
			Running = true;
			Error = null;
			return true;
		}

		public void Stop()
		{
			if (session != 0 && props != IntPtr.Zero) ControlTraceW(session, null, props, EVENT_TRACE_CONTROL_STOP);
			if (trace != 0) CloseTrace(trace);
			thread?.Join(3000);
			session = trace = 0;
			thread = null;
			Running = false;
			Release();
			lock (gate) { counts.Clear(); pending.Clear(); }
		}

		public void Dispose() => Stop();

		void Release()
		{
			if (props != IntPtr.Zero) { Marshal.FreeHGlobal(props); props = IntPtr.Zero; }
			if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
			if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
		}

		/// <summary>The round trips the game itself measured to port 7600 in the last minutes (address, ms).</summary>
		public List<KeyValuePair<IPAddress, double>> Checks(int minutes)
		{
			long since = DateTime.UtcNow.ToFileTimeUtc() - minutes * 600000000L;
			lock (gate)
				return checks.Where(c => c.Ts >= since).Select(c => new KeyValuePair<IPAddress, double>(new IPAddress(c.Addr), c.Ms)).ToList();
		}

		void OnEvent(IntPtr rec)
		{
			try
			{
				ushort id = (ushort)Marshal.ReadInt16(rec, 40);                  // EventHeader.EventDescriptor.Id
				if (id != UdpV4Sent && id != UdpV4Received) return;
				int len = (ushort)Marshal.ReadInt16(rec, 86);                    // UserDataLength
				IntPtr d = Marshal.ReadIntPtr(rec, 96);                          // UserData
				if (len < 20 || d == IntPtr.Zero) return;
				long ts = Marshal.ReadInt64(rec, 16);                            // EventHeader.TimeStamp: 100 ns (FILETIME, UTC)
				if (Raw != null)
				{
					var raw = new byte[20];
					Marshal.Copy(d, raw, 0, 20);
					Raw(id, ts, raw);
				}
				int pid = Marshal.ReadInt32(d, OffPid);
				if (Dump != null && id == UdpV4Sent)
				{
					var b = new byte[Math.Min(len, 32)];
					Marshal.Copy(d, b, 0, b.Length);
					Dump(pid, b);
				}
				var p = pids;
				bool game = false;
				for (int i = 0; i < p.Length; i++) if (p[i] == pid) { game = true; break; }
				if (!game) return;
				uint daddr = (uint)Marshal.ReadInt32(d, OffDaddr), saddr = (uint)Marshal.ReadInt32(d, OffSaddr);
				int dport = Marshal.ReadByte(d, OffDport) << 8 | Marshal.ReadByte(d, OffDport + 1);
				int sport = Marshal.ReadByte(d, OffSport) << 8 | Marshal.ReadByte(d, OffSport + 1);
				ulong to = (ulong)daddr << 16 | (uint)dport;
				lock (gate)
				{
					if (id == UdpV4Sent)
					{
						counts.TryGetValue(to, out int c);
						counts[to] = c + 1;
						if (dport == CheckPort)   // one of the game's checks: wait for its answer
						{
							if (!pending.TryGetValue(to, out var q)) pending[to] = q = new Queue<long>();
							q.Enqueue(ts);
							while (q.Count > 8) q.Dequeue();
						}
					}
					else
					{
						// an answer from port 7600 (the other end is on whichever side has it), paired with the oldest check
						// waiting for it
						ulong from = (ulong)saddr << 16 | (uint)sport;
						ulong key = sport == CheckPort && pending.ContainsKey(from) ? from : dport == CheckPort && pending.ContainsKey(to) ? to : 0;
						if (key != 0 && pending[key].Count > 0)
						{
							double ms = (ts - pending[key].Dequeue()) / 10000.0;
							if (ms > 0 && ms < 2000) checks.Add(new Check { Ts = ts, Addr = (uint)(key >> 16), Ms = ms });
							if (checks.Count > 5000) checks.RemoveRange(0, 1000);
						}
					}
				}
			}
			catch (Exception) { }
		}

		struct Check { public long Ts; public uint Addr; public double Ms; }

		static IntPtr Props()
		{
			int size = PropsSize + NamesSize;
			IntPtr p = Marshal.AllocHGlobal(size);
			Zero(p, size);
			Marshal.WriteInt32(p, 0, size);                 // Wnode.BufferSize
			Marshal.WriteInt32(p, 40, 1);                   // Wnode.ClientContext: QueryPerformanceCounter time
			Marshal.WriteInt32(p, 44, 0x00020000);          // Wnode.Flags: WNODE_FLAG_TRACED_GUID
			Marshal.WriteInt32(p, 48, 64);                  // BufferSize (KB)
			Marshal.WriteInt32(p, 52, 4);                   // MinimumBuffers
			Marshal.WriteInt32(p, 56, 32);                  // MaximumBuffers
			Marshal.WriteInt32(p, 64, 0x00000100);          // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
			Marshal.WriteInt32(p, 68, 1);                   // FlushTimer (s)
			Marshal.WriteInt32(p, 116, PropsSize);          // LoggerNameOffset
			return p;
		}

		static void Zero(IntPtr p, int size)
		{
			for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
		}

		const uint EVENT_TRACE_CONTROL_STOP = 1, EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
		const int PROCESS_TRACE_MODE_REAL_TIME = 0x00000100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

		[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventRecordCallback(IntPtr record);
		[DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
		[DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint code);
		[DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
		[DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(IntPtr logfile);
		[DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
		[DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);
	}
}
