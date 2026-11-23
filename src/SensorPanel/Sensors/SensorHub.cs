using LibreHardwareMonitor.Hardware;

namespace SensorPanel.Sensors;

/// <summary>
/// Polls hardware into a flat snapshot of dotted keys ("cpu.load" -> 41.5).
/// Widgets bind to these keys by name. A key that could not be read is simply absent.
///
/// To add a key: either add a line to <see cref="Maps"/> (a LibreHardwareMonitor sensor,
/// find its name with `SensorPanel sensors`), or compute it in <see cref="AddDerived"/> and
/// list what it is computed from in <see cref="DerivedFrom"/>.
///
/// An NVIDIA GPU is read through <see cref="Nvml"/> instead. LHM then does not open the GPU
/// at all, so the GPU lines of Maps find nothing. "lhmGpu" in settings.json turns that off.
///
/// "fps" comes from <see cref="FrameCounter"/>, which is only started while a widget shows it.
/// </summary>
public sealed class SensorHub : IDisposable
{
    public Dictionary<string, double> Snapshot { get; } = [];

    /// <param name="Key">Snapshot key.</param>
    /// <param name="Hardware">LHM hardware kind(s) to look in.</param>
    /// <param name="Name">Exact LHM sensor name.</param>
    /// <param name="Scale">Multiplier applied to the raw value.</param>
    /// <param name="Device">Optional exact hardware name, e.g. a network adapter.</param>
    private sealed record Map(string Key, HardwareType[] Hardware, SensorType Type, string Name, double Scale = 1, string? Device = null);

    private static readonly HardwareType[] Cpu = [HardwareType.Cpu];
    private static readonly HardwareType[] Gpu = [HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel];
    private static readonly HardwareType[] Mem = [HardwareType.Memory];
    private static readonly HardwareType[] Net = [HardwareType.Network];
    private static readonly HardwareType[] Board = [HardwareType.SuperIO];
    private const double BytesToMB = 1.0 / (1024 * 1024);

    // When a key is listed more than once, the first sensor that exists wins.
    // Keys that share a first segment ("gpu.*") should all come from one device: sensorIntervals is
    // set per segment, and a device is polled as a whole.
    private static readonly Map[] Maps =
    [
        new("cpu.load",      Cpu, SensorType.Load,        "CPU Total"),
        new("cpu.temp",      Cpu, SensorType.Temperature, "Core (Tctl/Tdie)"),   // AMD
        new("cpu.temp",      Cpu, SensorType.Temperature, "CPU Package"),        // Intel
        new("cpu.freq",      Cpu, SensorType.Clock,       "Cores (Average)", 1 / 1000.0), // GHz
        new("cpu.power",     Cpu, SensorType.Power,       "Package"),            // AMD
        new("cpu.power",     Cpu, SensorType.Power,       "CPU Package"),        // Intel

        new("gpu.load",      Gpu, SensorType.Load,        "GPU Core"),
        new("gpu.temp",      Gpu, SensorType.Temperature, "GPU Core"),
        new("gpu.power",     Gpu, SensorType.Power,       "GPU Package"),
        new("gpu.fan",       Gpu, SensorType.Fan,         "GPU Fan 1"),                 // RPM
        new("gpu.mem.used",  Gpu, SensorType.SmallData,   "GPU Memory Used",  1 / 1024.0), // GB
        new("gpu.mem.total", Gpu, SensorType.SmallData,   "GPU Memory Total", 1 / 1024.0), // GB

        new("mem.used",      Mem, SensorType.Data,        "Memory Used",      Device: "Total Memory"), // GB
        new("mem.free",      Mem, SensorType.Data,        "Memory Available", Device: "Total Memory"), // GB
        new("mem.load",      Mem, SensorType.Load,        "Memory",           Device: "Total Memory"), // %

        new("net.up",        Net, SensorType.Throughput,  "Upload Speed",   BytesToMB, Device: "Ethernet"), // MB/s
        new("net.down",      Net, SensorType.Throughput,  "Download Speed", BytesToMB, Device: "Ethernet"), // MB/s

        // Motherboard fan headers (RPM). Names are board-specific; these are a Gigabyte X870's.
        new("fan.cpu",       Board, SensorType.Fan,       "CPU Fan"),
        new("fan.sys1",      Board, SensorType.Fan,       "System Fan #1"),
        new("fan.sys2",      Board, SensorType.Fan,       "System Fan #2"),
    ];

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsMemoryEnabled = true,
        IsNetworkEnabled = true,
        IsMotherboardEnabled = true,    // the fan.* keys; board temperatures, for keys you add yourself
    };

    private readonly List<(string Key, ISensor Sensor, double Scale)> _found = [];  // each key's sensor, looked up once
    // "Every" is how many ticks pass between polls of a device: 1 = every tick.
    private List<(string Key, ISensor Sensor, double Scale, int Every)> _bound = [];
    private List<(IHardware Hardware, int Every)> _polled = [];     // only hardware that a key actually reads
    private readonly Nvml? _nvml;               // null: the GPU comes from LHM
    private List<string> _nvmlKeys = [];        // the gpu.* keys in use
    private int _nvmlEvery = 1;
    private long _tick;
    private bool _disk;                         // a disk.* key is in use
    private long _diskDue;                      // Environment.TickCount64 at which disk space is read again; 0 = next tick
    private FrameCounter? _fps;                 // open only while a widget shows "fps"

    /// <summary>The first segment of each hardware key ("cpu", "gpu", ...): what sensorIntervals can name.</summary>
    public static IEnumerable<string> Groups => Maps.Select(m => Group(m.Key)).Distinct();

    private static string Group(string key) => key.Split('.')[0];

    /// <summary>Every key read from a hardware sensor; the `sensors` command binds them all.</summary>
    public static IEnumerable<string> HardwareKeys => Maps.Select(m => m.Key);

    /// <param name="refreshMs">Length of one tick. The caller runs <see cref="Update"/> once per tick.</param>
    /// <param name="intervals">Slower poll intervals in ms by key group, e.g. "gpu" -> 3000.</param>
    /// <param name="keys">The keys the widgets read; only these are bound, and only their devices polled.</param>
    /// <param name="lhmGpu">Read the GPU through LHM even when NVML is there ("lhmGpu" in settings.json).</param>
    public SensorHub(int refreshMs, IReadOnlyDictionary<string, int> intervals, IEnumerable<string> keys, bool lhmGpu)
    {
        _nvml = lhmGpu ? null : Nvml.Open();
        _computer.IsGpuEnabled = _nvml is null;     // once LHM polls the GPU at all, it pays the whole 77 ms
        _computer.Open();
        var all = AllHardware().ToList();

        // LHM keeps a 24-hour history of every sensor's values, which nothing here reads. Turn it
        // off, also for the sensors that a device only lists once it has been polled (below).
        foreach (var hardware in all)
        {
            foreach (var sensor in hardware.Sensors) sensor.ValuesTimeWindow = TimeSpan.Zero;
            hardware.SensorAdded += sensor => sensor.ValuesTimeWindow = TimeSpan.Zero;
        }

        // Some devices (the motherboard's SuperIO chips) only list a sensor once it has
        // produced a value, so poll everything once before looking sensors up by name.
        foreach (var hardware in all) hardware.Update();

        foreach (var map in Maps)
        {
            if (_found.Any(f => f.Key == map.Key)) continue;
            var sensor = all
                .Where(h => map.Hardware.Contains(h.HardwareType) && (map.Device is null || h.Name == map.Device))
                .SelectMany(h => h.Sensors)
                .FirstOrDefault(s => s.SensorType == map.Type && s.Name == map.Name);
            if (sensor is not null) _found.Add((map.Key, sensor, map.Scale));
        }

        try { Configure(refreshMs, intervals, keys); }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// Sets the tick length, poll intervals and keys in use; the tray app calls this when it switches config.
    /// The hardware stays open: a second SensorHub cannot stand in, because closing one breaks
    /// the other (LibreHardwareMonitor shares state between them).
    /// If this throws, nothing has changed.
    /// </summary>
    public void Configure(int refreshMs, IReadOnlyDictionary<string, int> intervals, IEnumerable<string> keys)
    {
        // A computed key needs the keys it is computed from.
        var used = keys.SelectMany(k => DerivedFrom.GetValueOrDefault(k, []).Prepend(k)).ToHashSet();

        // A group's interval can only slow its device down, and is rounded up to whole ticks.
        int Every(string group)
        {
            int ms = Math.Max(refreshMs, intervals.GetValueOrDefault(group, refreshMs));
            return (ms + refreshMs - 1) / refreshMs;
        }
        var bound = _found.Where(f => used.Contains(f.Key)).Select(f => (f.Key, f.Sensor, f.Scale, Every: Every(Group(f.Key)))).ToList();

        List<string> nvmlKeys = _nvml is null ? [] : Nvml.Keys.Where(used.Contains).ToList();
        int nvmlEvery = Every("gpu");
        if (nvmlKeys.Count > 0 && nvmlEvery > 1) Log.Info($"Polling the GPU (NVML) every {nvmlEvery * refreshMs} ms.");

        var polled = new List<(IHardware Hardware, int Every)>();
        foreach (var device in bound.GroupBy(b => b.Sensor.Hardware))
        {
            // Only possible when two key groups read from the same device (see the note on Maps).
            if (device.Select(b => b.Every).Distinct().Count() > 1)
                throw new InvalidDataException(
                    $"sensorIntervals: these keys are all read from one device ({device.Key.Name}), which is polled as a whole, " +
                    "so their groups must share one interval: " +
                    string.Join(", ", device.Select(b => $"{b.Key} = {b.Every * refreshMs} ms")) + ".");

            int every = device.First().Every;
            polled.Add((device.Key, every));
            if (every > 1) Log.Info($"Polling {device.Key.Name} every {every * refreshMs} ms.");
        }

        Snapshot.Clear();   // a key the new config no longer reads would otherwise keep its last value
        (_bound, _polled, _nvmlKeys, _nvmlEvery, _tick) = (bound, polled, nvmlKeys, nvmlEvery, 0);     // tick 0 polls every device
        (_disk, _diskDue) = (used.Any(k => Group(k) == "disk"), 0);     // and the disk, since its keys were just cleared

        if (used.Contains("fps")) _fps ??= FrameCounter.Start();
        else { _fps?.Dispose(); _fps = null; }
    }

    public void Update()
    {
        foreach (var (hardware, every) in _polled)
            if (_tick % every == 0) hardware.Update();

        foreach (var (key, sensor, scale, every) in _bound)
        {
            if (_tick % every != 0) continue;   // device not polled this tick: the key keeps its last value

            // Without elevation LHM reports 0 for these instead of nothing; 0 is never a real reading.
            bool bogus = sensor.Value == 0 && sensor.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Clock;
            if (sensor.Value is float v && !bogus) Snapshot[key] = v * scale;
            else Snapshot.Remove(key);
        }
        if (_tick % _nvmlEvery == 0) _nvml?.Read(_nvmlKeys, Snapshot);
        if (_fps?.Read() is double fps) Snapshot["fps"] = fps;
        else Snapshot.Remove("fps");
        _tick++;
        AddDerived();
    }

    /// <summary>
    /// The read keys that a key in <see cref="AddDerived"/> is computed from. They are polled
    /// whenever that key is in use, even if no widget shows them; list them here, or it reads "--".
    /// </summary>
    private static readonly Dictionary<string, string[]> DerivedFrom = new()
    {
        ["mem.total"] = ["mem.used", "mem.free"],
    };

    /// <summary>
    /// Values that are computed rather than read. This is the extension point: there is no
    /// expression language in the config, so anything derived gets a few lines here and a key.
    /// </summary>
    private void AddDerived()
    {
        if (Snapshot.TryGetValue("mem.used", out var used) && Snapshot.TryGetValue("mem.free", out var free))
            Snapshot["mem.total"] = used + free;

        // Each read of drive space is a GetDiskFreeSpaceEx call, for numbers that move over
        // minutes; between reads the keys keep their values in the snapshot.
        if (_disk && Environment.TickCount64 >= _diskDue)
        {
            const double GB = 1024.0 * 1024 * 1024;
            var c = new DriveInfo("C");
            long size = c.TotalSize, available = c.AvailableFreeSpace;
            Snapshot["disk.total"] = size / GB;
            Snapshot["disk.free"] = available / GB;
            Snapshot["disk.used"] = (size - available) / GB;
            Snapshot["disk.load"] = 100.0 * (size - available) / size;
            _diskDue = Environment.TickCount64 + 30_000;
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Snapshot["uptime.days"] = uptime.Days;
        Snapshot["uptime.hours"] = uptime.Hours;
        Snapshot["uptime.minutes"] = uptime.Minutes;
    }

    private IEnumerable<IHardware> AllHardware()
    {
        foreach (var hardware in _computer.Hardware)
        {
            yield return hardware;
            foreach (var sub in hardware.SubHardware) yield return sub;
        }
    }

    /// <summary>`SensorPanel sensors`: everything LHM can see, then the keys widgets can bind to.</summary>
    public void Dump(TextWriter output)
    {
        // The listing is where names for Maps are found, so it shows the GPU's LHM sensors even
        // while NVML feeds the keys. Opened after the keys were bound, it adds nothing to them.
        _computer.IsGpuEnabled = true;
        foreach (var hardware in AllHardware())
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            hardware.Update();
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            output.WriteLine($"{hardware.HardwareType}: {hardware.Name}   (poll {ms:F1} ms{(_polled.Any(p => p.Hardware == hardware) ? ", in use" : "")})");
            foreach (var s in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                output.WriteLine($"    {s.SensorType,-12} {s.Name,-32} {s.Value}");
        }

        _disk = true;   // the listing binds HardwareKeys, which has no disk.* key; list it like the other computed keys
        Update();
        output.WriteLine($"\nKeys available to widgets (gpu.* from {(_nvml is null ? "LibreHardwareMonitor" : "NVML")}):");
        foreach (var (key, value) in Snapshot.OrderBy(p => p.Key))
            output.WriteLine($"    {key,-16} {value:0.###}");
    }

    public void Dispose()
    {
        _computer.Close();
        _nvml?.Dispose();
        _fps?.Dispose();
    }
}

