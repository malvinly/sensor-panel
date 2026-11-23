using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SensorPanel.Sensors;

/// <summary>
/// The "fps" key: frames presented per second by the process that owns the foreground window,
/// counted from the kernel's Present event (Microsoft-Windows-DxgKrnl, id 184) through a real-time
/// ETW session. That event fires once per Present for every graphics API (D3D9-12, Vulkan, OpenGL),
/// in the presenting process. Starting the session needs administrator rights.
///
/// TODO: test a Vulkan game (and ideally an OpenGL one). Checked so far: D3D11 test windows
/// (flip and blt model) and Kingdom Come, where event 184 matched the game's DXGI presents one for one
/// and the reading matched NVIDIA's overlay (120 fps). Vulkan and OpenGL are not yet confirmed.
///
/// What it cannot see: apps that draw through DirectComposition (browsers, Electron) present
/// nothing and read "--", as do UWP apps whose window belongs to ApplicationFrameHost. A process
/// that presents to several windows or swap chains reads their sum.
///
/// The session outlives a process that is killed. The next start finds it by name and stops it,
/// unless another SensorPanel is running and using it (a --png run is allowed beside the tray instance).
/// </summary>
internal sealed class FrameCounter : IDisposable
{
    private const string SessionName = "SensorPanel-Fps";
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const ushort PresentEventId = 184;
    private const ulong PresentKeyword = 0x8000000;

    // Native code holds a pointer to this delegate, so it lives as long as the class. The counter
    // an event belongs to comes back through EVENT_RECORD.UserContext (the logfile's Context).
    private static readonly EventRecordCallback Callback = OnEvent;
    private static bool _warned;        // a failed start is retried on every config switch, but raises one balloon

    private readonly ulong _session;
    private readonly IntPtr _properties, _loggerName;
    private GCHandle _self;
    private ulong _trace;
    private Thread? _thread;
    private volatile bool _disposing;

    /// <summary>The session stopped from outside (logman, another tool): this counter counts nothing more. SensorHub then starts a new one.</summary>
    public bool Ended => _ended;
    private volatile bool _ended;

    private readonly Lock _lock = new();
    private volatile int _pid;          // the process whose presents are counted
    private long _count, _lastStamp;    // presents since _pid was set, and the QPC time of the last one
    private long _readCount, _readStamp;  // the same two at the previous Read that had frames
    private double? _rate;

    private FrameCounter(ulong session, IntPtr properties, IntPtr loggerName) =>
        (_session, _properties, _loggerName) = (session, properties, loggerName);

    /// <summary>Starts the ETW session, or returns null (logged) when Windows refuses it.</summary>
    public static FrameCounter? Start()
    {
        IntPtr properties = Marshal.AllocHGlobal(PropertiesSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(SessionName);

        // The session name is machine-wide. When it exists, it is either left over from a run that was
        // killed, or in use by another SensorPanel. Only the first is stopped: stopping the second
        // would silently end that instance's fps.
        int error = StartTraceW(out ulong session, SessionName, Properties(properties));
        bool inUse = error == 183 /* ERROR_ALREADY_EXISTS */ && AnotherInstanceRuns();
        if (error == 183 && !inUse)
        {
            ControlTraceW(0, SessionName, Properties(properties), ControlStop);
            error = StartTraceW(out session, SessionName, Properties(properties));
        }
        if (error == 0) error = EnablePresentEvent(session);

        var counter = error == 0 ? new FrameCounter(session, properties, loggerName) : null;
        if (counter is not null) error = counter.Open();

        if (error != 0)
        {
            if (error != 183) ControlTraceW(session, null, Properties(properties), ControlStop);
            if (counter is not null) counter._self.Free();
            Marshal.FreeHGlobal(properties);
            Marshal.FreeHGlobal(loggerName);
            Log.Warn(inUse
                ? "fps: another SensorPanel is already counting frames, so it shows \"--\" here."
                : $"fps: cannot start the ETW session (Windows error {error}), so it shows \"--\".", notify: !_warned);
            _warned = true;
            return null;
        }
        return counter;
    }

    /// <summary>Opens the session for reading and starts the thread that ETW calls back on.</summary>
    private int Open()
    {
        _self = GCHandle.Alloc(this);
        var logfile = new LogFile
        {
            LoggerName = _loggerName,
            // RAW_TIMESTAMP keeps event timestamps in QPC ticks (Wnode.ClientContext = 1), the clock
            // Read compares them with; without it ProcessTrace converts them to FILETIME.
            ProcessTraceMode = 0x100 /* REAL_TIME */ | 0x10000000 /* EVENT_RECORD */ | 0x1000 /* RAW_TIMESTAMP */,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            Context = GCHandle.ToIntPtr(_self),
        };
        _trace = OpenTraceW(ref logfile);
        if (_trace == ulong.MaxValue) return Marshal.GetLastPInvokeError();

        _thread = new Thread(() =>
        {
            int result = ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero);
            _ended = true;
            if (!_disposing) Log.Warn($"fps: the ETW session ended (Windows error {result}), so fps shows \"--\" until the config is picked again.");
        }) { IsBackground = true, Name = "ETW presents" };
        _thread.Start();
        return 0;
    }

    /// <summary>
    /// Frames per second of the foreground process, called once per tick. The rate is measured
    /// between the events' own timestamps, not between calls: ETW delivers events in bursts, up to
    /// FlushTimer (1 s) late, so a call can find no new frames. It then returns the previous rate,
    /// until nothing has arrived for 3 s, which means the process stopped presenting: null.
    /// When the foreground process changes, counting starts over: null for that call, and for the
    /// next, which only takes the first timestamp to measure from. With no foreground window at all
    /// (briefly, while switching windows), the current process goes on being counted.
    /// </summary>
    public double? Read()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
        lock (_lock)
        {
            if (pid != _pid && pid != 0)
            {
                _pid = pid;
                (_count, _lastStamp, _readCount, _readStamp, _rate) = (0, 0, 0, 0, null);
                return null;
            }
            if (_count > _readCount)
            {
                if (_readStamp != 0 && _lastStamp > _readStamp)
                    _rate = (_count - _readCount) * (double)Stopwatch.Frequency / (_lastStamp - _readStamp);
                (_readCount, _readStamp) = (_count, _lastStamp);
            }
            else if (Stopwatch.GetTimestamp() - _lastStamp > 3 * Stopwatch.Frequency)
                (_rate, _readStamp) = (null, 0);    // the gap is not a frame time either
            return _rate;
        }
    }

    private static void OnEvent(IntPtr record)
    {
        // EVENT_RECORD: EventHeader.ProcessId at 12, .TimeStamp at 16, .EventDescriptor.Id at 40; UserContext at 104.
        // The session is filtered to id 184; checking it too keeps a filter that is not honoured from counting other events as frames.
        if ((ushort)Marshal.ReadInt16(record, 40) != PresentEventId) return;
        var counter = (FrameCounter)GCHandle.FromIntPtr(Marshal.ReadIntPtr(record, 104)).Target!;
        if (Marshal.ReadInt32(record, 12) != counter._pid) return;
        long stamp = Marshal.ReadInt64(record, 16);
        lock (counter._lock) { counter._count++; counter._lastStamp = stamp; }
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        // A session that already ended is not stopped: its handle may by now belong to another session.
        if (!Ended && ControlTraceW(_session, null, Properties(_properties), ControlStop) == 0)
        {
            int lost = Marshal.ReadInt32(_properties, 88) + Marshal.ReadInt32(_properties, 100);   // EventsLost + RealTimeBuffersLost, filled in by the stop
            if (lost > 0) Log.Info($"fps: ETW dropped {lost} events or buffers while counting, so some readings were low.");
        }

        CloseTrace(_trace);     // ProcessTrace returns once it has delivered what is left
        // The callback uses the handle and ProcessTrace the logfile's strings until it returns. If it
        // has not, after a margin far above one buffer's delivery, they are leaked rather than freed under it.
        if (_thread!.Join(2000))
        {
            _self.Free();
            Marshal.FreeHGlobal(_properties);
            Marshal.FreeHGlobal(_loggerName);
        }
        else Log.Info("fps: the ETW thread did not stop within 2 s; its few hundred bytes are left allocated.");
    }

    private static bool AnotherInstanceRuns()
    {
        using var self = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(self.ProcessName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 1;
    }

    /// <summary>Enables only DxgKrnl's Present event: the provider's Present keyword alone is ~1,000 events a second at idle.</summary>
    private static int EnablePresentEvent(ulong session)
    {
        // EVENT_FILTER_EVENT_ID { BOOLEAN FilterIn; UCHAR Reserved; USHORT Count; USHORT Events[1]; }
        IntPtr ids = Marshal.AllocHGlobal(6);
        Marshal.WriteByte(ids, 0, 1);
        Marshal.WriteByte(ids, 1, 0);
        Marshal.WriteInt16(ids, 2, 1);
        Marshal.WriteInt16(ids, 4, (short)PresentEventId);
        var filter = new FilterDescriptor { Ptr = (ulong)ids, Size = 6, Type = 0x80000200 /* EVENT_FILTER_TYPE_EVENT_ID */ };
        IntPtr filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FilterDescriptor>());
        Marshal.StructureToPtr(filter, filterPtr, false);
        var parameters = new EnableParameters { Version = 2, EnableFilterDesc = filterPtr, FilterDescCount = 1 };
        try
        {
            return EnableTraceEx2(session, DxgKrnl, 1 /* ENABLE */, 4 /* informational */, PresentKeyword, 0, 0, ref parameters);
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(ids);
        }
    }

    // EVENT_TRACE_PROPERTIES (120 bytes) followed by room for the session name, which Windows copies there.
    private const int PropertiesSize = 120 + 2 * 64;
    private const uint ControlStop = 1;

    /// <summary>Fills the EVENT_TRACE_PROPERTIES block; Windows writes into it, so every call starts it afresh.</summary>
    private static IntPtr Properties(IntPtr p)
    {
        for (int i = 0; i < PropertiesSize; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, PropertiesSize);   // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext: QPC timestamps, as Stopwatch (kept by RAW_TIMESTAMP in Open)
        Marshal.WriteInt32(p, 44, 0x20000);         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        // Present events from every process, ~80 bytes each. Two 8 KB buffers are the usual cost; up to
        // 16 are added only while the reading thread falls behind (a CPU-bound game), instead of dropping events.
        Marshal.WriteInt32(p, 48, 8);               // BufferSize, KB
        Marshal.WriteInt32(p, 52, 2);               // MinimumBuffers
        Marshal.WriteInt32(p, 56, 16);              // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);           // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);               // FlushTimer, s: the most an event waits to be delivered; Read's 3 s limit allows for it
        Marshal.WriteInt32(p, 116, 120);            // LoggerNameOffset
        return p;
    }

    private delegate void EventRecordCallback(IntPtr record);

    /// <summary>EVENT_TRACE_LOGFILEW (x64), only the fields that are set.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct LogFile
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
        [FieldOffset(440)] public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FilterDescriptor { public ulong Ptr; public uint Size, Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnableParameters
    {
        public uint Version, EnableProperty, ControlFlags;
        public Guid SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    // System32 only, as in Nvml: the app runs elevated, and the default search would look beside the exe first.
    private const string Advapi = "advapi32.dll", User32 = "user32.dll";
    [DllImport(Advapi, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int StartTraceW(out ulong session, string name, IntPtr properties);
    [DllImport(Advapi, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int ControlTraceW(ulong session, string? name, IntPtr properties, uint control);
    [DllImport(Advapi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int EnableTraceEx2(ulong session, in Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, ref EnableParameters parameters);
    [DllImport(Advapi, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern ulong OpenTraceW(ref LogFile logfile);
    [DllImport(Advapi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport(Advapi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int CloseTrace(ulong trace);
    [DllImport(User32), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetForegroundWindow();
    [DllImport(User32), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
}
