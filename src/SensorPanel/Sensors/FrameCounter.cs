using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SensorPanel.Sensors;

/// <summary>
/// The "fps" key: frames presented per second by the process that owns the foreground window,
/// counted from the kernel's Present event (Microsoft-Windows-DxgKrnl, id 184) through a real-time
/// ETW session. That event fires once per Present for every graphics API (D3D9-12, Vulkan, OpenGL),
/// in the presenting process. Apps that draw through DirectComposition instead (browsers, Electron)
/// present nothing and read "--". Starting the session needs administrator rights.
/// </summary>
internal sealed class FrameCounter : IDisposable
{
    private const string SessionName = "SensorPanel-Fps";
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const ushort PresentEventId = 184;
    private const ulong PresentKeyword = 0x8000000;

    // ETW calls back on the ProcessTrace thread; one counter exists at a time.
    private static FrameCounter? _current;
    private static readonly EventRecordCallback Callback = OnEvent;     // kept alive: native code holds a pointer to it

    private readonly ulong _session, _trace;
    private readonly IntPtr _properties, _loggerName;
    private readonly Thread _thread;

    private readonly Lock _lock = new();
    private volatile int _pid;          // the process whose presents are counted
    private long _count, _lastStamp;    // presents since _pid was set, and the QPC time of the last one
    private long _readCount, _readStamp;  // the same two at the previous Read that had frames
    private double? _fps;

    private FrameCounter(ulong session, ulong trace, IntPtr properties, IntPtr loggerName)
    {
        (_session, _trace, _properties, _loggerName) = (session, trace, properties, loggerName);
        _thread = new Thread(() => ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "ETW presents" };
    }

    /// <summary>Starts the ETW session, or returns null (logged) when Windows refuses it.</summary>
    public static FrameCounter? Start()
    {
        IntPtr properties = Marshal.AllocHGlobal(PropertiesSize);
        IntPtr loggerName = Marshal.StringToHGlobalUni(SessionName);
        ulong session = 0;

        // The session name is machine-wide. When it exists, it is either left over from a run that was
        // killed, or in use by another SensorPanel (a --png run is allowed beside the tray instance).
        // Only the first is stopped: stopping the second would silently end that instance's fps.
        int error = StartTraceW(out session, SessionName, Properties(properties));
        bool inUse = error == 183 /* ERROR_ALREADY_EXISTS */ && AnotherInstanceRuns();
        if (error == 183 && !inUse)
        {
            ControlTraceW(0, SessionName, Properties(properties), ControlStop);
            error = StartTraceW(out session, SessionName, Properties(properties));
        }
        if (error == 0) error = EnablePresentEvent(session);

        ulong trace = ulong.MaxValue;
        if (error == 0)
        {
            var logfile = new LogFile
            {
                LoggerName = loggerName,
                // RAW_TIMESTAMP keeps event timestamps in QPC ticks (Wnode.ClientContext = 1), the clock
                // Read compares them with; without it ProcessTrace converts them to FILETIME.
                ProcessTraceMode = 0x100 /* REAL_TIME */ | 0x10000000 /* EVENT_RECORD */ | 0x1000 /* RAW_TIMESTAMP */,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            };
            trace = OpenTraceW(ref logfile);
            if (trace == ulong.MaxValue) error = Marshal.GetLastPInvokeError();
        }

        if (error != 0)
        {
            if (session != 0) ControlTraceW(session, null, Properties(properties), ControlStop);
            Marshal.FreeHGlobal(properties);
            Marshal.FreeHGlobal(loggerName);
            Log.Warn(inUse
                ? "fps: another SensorPanel is already counting frames, so it shows \"--\" here."
                : $"fps: cannot start the ETW session (Windows error {error}{(error == 5 ? ", needs administrator rights" : "")}), so it shows \"--\".");
            return null;
        }

        var counter = new FrameCounter(session, trace, properties, loggerName);
        _current = counter;
        counter._thread.Start();
        return counter;
    }

    /// <summary>
    /// Frames per second of the foreground process since the last call, or null when it is not
    /// presenting. Computed from the events' own timestamps: ETW delivers them in bursts, up to a second late.
    /// </summary>
    public double? Read()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
        lock (_lock)
        {
            if (pid != _pid)
            {
                _pid = pid;
                (_count, _lastStamp, _readCount, _readStamp, _fps) = (0, 0, 0, 0, null);
                return null;
            }
            if (_count > _readCount)
            {
                if (_readStamp != 0 && _lastStamp > _readStamp)
                    _fps = (_count - _readCount) * (double)Stopwatch.Frequency / (_lastStamp - _readStamp);
                (_readCount, _readStamp) = (_count, _lastStamp);
            }
            else if (Stopwatch.GetTimestamp() - _lastStamp > 3 * Stopwatch.Frequency)
                (_fps, _readStamp) = (null, 0);     // nothing for longer than ETW's delivery delay: it stopped presenting, and the gap is not a frame time
            return _fps;
        }
    }

    private static void OnEvent(IntPtr record)
    {
        var counter = _current;
        // EVENT_RECORD.EventHeader: ProcessId at 12, TimeStamp at 16. The session only delivers id 184.
        if (counter is null || Marshal.ReadInt32(record, 12) != counter._pid) return;
        long stamp = Marshal.ReadInt64(record, 16);
        lock (counter._lock) { counter._count++; counter._lastStamp = stamp; }
    }

    public void Dispose()
    {
        ControlTraceW(_session, null, Properties(_properties), ControlStop);     // ProcessTrace returns
        _thread.Join(2000);
        CloseTrace(_trace);
        _current = null;
        Marshal.FreeHGlobal(_properties);
        Marshal.FreeHGlobal(_loggerName);
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
        Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext: QPC timestamps, as Stopwatch (kept by RAW_TIMESTAMP in Start)
        Marshal.WriteInt32(p, 44, 0x20000);         // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 4);               // BufferSize, KB: one present event is ~80 bytes
        Marshal.WriteInt32(p, 52, 2);               // MinimumBuffers
        Marshal.WriteInt32(p, 56, 4);               // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);           // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);               // FlushTimer, s: the most an event waits to be delivered
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

    private const string Advapi = "advapi32.dll";
    [DllImport(Advapi, CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong session, string name, IntPtr properties);
    [DllImport(Advapi, CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong session, string? name, IntPtr properties, uint control);
    [DllImport(Advapi)] private static extern int EnableTraceEx2(ulong session, in Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, ref EnableParameters parameters);
    [DllImport(Advapi, SetLastError = true)] private static extern ulong OpenTraceW(ref LogFile logfile);
    [DllImport(Advapi)] private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport(Advapi)] private static extern int CloseTrace(ulong trace);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
}
