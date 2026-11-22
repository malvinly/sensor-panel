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

    private readonly IntPtr _device;

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

    /// <summary>Reads these keys into the snapshot. A key NVML cannot give is removed, so its widget shows "--".</summary>
    public void Read(IEnumerable<string> keys, Dictionary<string, double> snapshot)
    {
        foreach (var key in keys)
            if (Read(key) is double value) snapshot[key] = value;
            else snapshot.Remove(key);
    }

    private double? Read(string key)
    {
        const double GB = 1024.0 * 1024 * 1024;
        try
        {
            return key switch
            {
                "gpu.load" => nvmlDeviceGetUtilizationRates(_device, out var u) == 0 ? u.Gpu : null,
                // Marked deprecated in nvml.h in favour of nvmlDeviceGetTemperatureV, which gives the same
                // whole degrees but is missing from older drivers. This one is in every driver.
                "gpu.temp" => nvmlDeviceGetTemperature(_device, 0 /* NVML_TEMPERATURE_GPU */, out uint t) == 0 ? t : null,
                "gpu.power" => nvmlDeviceGetPowerUsage(_device, out uint mw) == 0 ? mw / 1000.0 : null,
                "gpu.fan" => FanRpm(),
                // v1 "used" includes the memory the driver reserves, as LHM's figure did; v2's does not.
                "gpu.mem.used" => nvmlDeviceGetMemoryInfo(_device, out var m) == 0 ? m.Used / GB : null,
                "gpu.mem.total" => nvmlDeviceGetMemoryInfo(_device, out var m) == 0 ? m.Total / GB : null,
                _ => null,
            };
        }
        catch (EntryPointNotFoundException) { return null; }    // nvmlDeviceGetFanSpeedRPM: only newer drivers have it
    }

    private double? FanRpm()
    {
        var fan = new FanSpeed { Version = (uint)Marshal.SizeOf<FanSpeed>() | 1u << 24 };   // NVML_STRUCT_VERSION(..., 1)
        return nvmlDeviceGetFanSpeedRPM(_device, ref fan) == 0 ? fan.Speed : null;
    }

    public void Dispose() => nvmlShutdown();

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct FanSpeed { public uint Version, Fan, Speed; }

    // System32 only: the app runs elevated, and the default search would look beside the exe first.
    private const string Dll = "nvml.dll";
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlInit_v2();
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlShutdown();
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeed fanSpeed);
}
