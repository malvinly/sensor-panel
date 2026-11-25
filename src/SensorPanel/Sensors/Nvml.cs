using System.Runtime.InteropServices;

namespace SensorPanel.Sensors;

/// <summary>
/// The gpu.* keys of an NVIDIA card, read from nvml.dll, which comes with the driver.
/// LibreHardwareMonitor's GPU poll takes ~77 ms (two PCIe throughput samples of 20 ms each,
/// NVAPI, D3DKMT) to produce four or five values; these calls take ~0.4 ms, most of it the
/// fan, so the fan is only asked for when a widget shows it.
/// </summary>
internal sealed class Nvml : IDisposable
{
    /// <summary>The keys this can read, by the same names and in the same units as the GPU lines of SensorHub's Maps.</summary>
    public static readonly string[] Keys = ["gpu.load", "gpu.temp", "gpu.power", "gpu.fan", "gpu.mem.used", "gpu.mem.total"];

    private const int Success = 0, NotSupported = 3;    // nvmlReturn_t
    private const int ReopenEveryMs = 10_000;

    private IntPtr _device;
    private bool _open = true;      // nvmlInit_v2 has succeeded and nvmlShutdown is still owed
    private long _reopenDue;        // Environment.TickCount64 before which a failed read does not reopen
    private bool _failing;          // a failure has been logged, and no read has succeeded since

    private Nvml(IntPtr device) => _device = device;

    /// <summary>The first NVIDIA GPU, or null when there is no NVIDIA driver or card.</summary>
    public static Nvml? Open()
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            if (nvmlDeviceGetHandleByIndex_v2(0, out var device) == 0) return new Nvml(device);
            nvmlShutdown();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>
    /// Reads these keys into the snapshot. A key NVML cannot give is removed, so its widget shows "--".
    /// A session can go bad while the app runs (after the PC sleeps, every call fails), so a failure
    /// other than "not supported" reopens NVML and reads again, at most every 10 s.
    /// </summary>
    public void Read(IEnumerable<string> keys, Dictionary<string, double> snapshot)
    {
        int error = ReadAll(keys, snapshot);
        if (error != Success && Environment.TickCount64 >= _reopenDue)
        {
            _reopenDue = Environment.TickCount64 + ReopenEveryMs;
            if (!_failing) Log.Info($"NVML stopped answering ({ErrorName(error)}): reopening it, then every {ReopenEveryMs / 1000} s until it answers.");
            _failing = true;
            if (Reopen()) error = ReadAll(keys, snapshot);
        }
        if (error == Success && _failing)
        {
            Log.Info("NVML is answering again.");
            _failing = false;
        }
    }

    /// <returns>The first error other than "not supported", or <see cref="Success"/>.</returns>
    private int ReadAll(IEnumerable<string> keys, Dictionary<string, double> snapshot)
    {
        int firstError = Success;
        foreach (var key in keys)
        {
            var (code, value) = Read(key);
            if (code == Success) snapshot[key] = value;
            else snapshot.Remove(key);
            if (code is not (Success or NotSupported) && firstError == Success) firstError = code;
        }
        return firstError;
    }

    private (int Code, double Value) Read(string key)
    {
        const double GB = 1024.0 * 1024 * 1024;
        try
        {
            return key switch
            {
                "gpu.load" => (nvmlDeviceGetUtilizationRates(_device, out var u), u.Gpu),
                // Marked deprecated in nvml.h in favour of nvmlDeviceGetTemperatureV, which gives the same
                // whole degrees but is missing from older drivers. This one is in every driver.
                "gpu.temp" => (nvmlDeviceGetTemperature(_device, 0 /* NVML_TEMPERATURE_GPU */, out uint t), t),
                "gpu.power" => (nvmlDeviceGetPowerUsage(_device, out uint mw), mw / 1000.0),
                "gpu.fan" => FanRpm(),
                // v1 "used" includes the memory the driver reserves, as LHM's figure did; v2's does not.
                "gpu.mem.used" => (nvmlDeviceGetMemoryInfo(_device, out var m), m.Used / GB),
                "gpu.mem.total" => (nvmlDeviceGetMemoryInfo(_device, out var m), m.Total / GB),
                _ => (NotSupported, 0),
            };
        }
        catch (EntryPointNotFoundException) { return (NotSupported, 0); }    // nvmlDeviceGetFanSpeedRPM: only newer drivers have it
    }

    private (int Code, double Value) FanRpm()
    {
        var fan = new FanSpeed { Version = (uint)Marshal.SizeOf<FanSpeed>() | 1u << 24 };   // NVML_STRUCT_VERSION(..., 1)
        return (nvmlDeviceGetFanSpeedRPM(_device, ref fan), fan.Speed);
    }

    /// <summary>Ends this session and starts a new one on the first GPU. False if NVML or the GPU is not there (yet).</summary>
    private bool Reopen()
    {
        if (_open) nvmlShutdown();
        _open = nvmlInit_v2() == Success;
        if (!_open || nvmlDeviceGetHandleByIndex_v2(0, out var device) != Success) return false;
        _device = device;
        return true;
    }

    private static string ErrorName(int code) => $"error {code}, {Marshal.PtrToStringAnsi(nvmlErrorString(code))}";

    public void Dispose()
    {
        if (_open) nvmlShutdown();
        _open = false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct FanSpeed { public uint Version, Fan, Speed; }

    // System32 only: the app runs elevated, and the default search would look beside the exe first.
    private const string Dll = "nvml.dll";
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlInit_v2();
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlShutdown();
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr nvmlErrorString(int result);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeed fanSpeed);
}
