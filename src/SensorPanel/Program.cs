using System.Runtime.InteropServices;
using System.Text.Json;
using SensorPanel.Display;
using SensorPanel.Layout;
using SensorPanel.Sensors;

namespace SensorPanel;

// Usage: SensorPanel [--config path] [--png out.png] [--verbose]     tray icon
//        SensorPanel sensors      list every hardware sensor and every bindable key
//        SensorPanel bench        transport measurements against the panel
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A windowed exe has no console of its own. Started from a terminal, borrow that one so
        // the command-line modes, warnings and --verbose still print somewhere.
        AttachConsole(-1);

        string? configPath = null, pngPath = null;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "bench": return Bench.Run();
                case "sensors":
                    WarnIfNoDriver();
                    var defaults = new Config();
                    using (var hub = new SensorHub(defaults.RefreshMs, defaults.SensorIntervals, SensorHub.HardwareKeys, ReadSettings().LhmGpu)) hub.Dump(Console.Out);
                    return 0;
                case "--config" when i + 1 < args.Length: configPath = Path.GetFullPath(args[++i]); break;
                case "--png" when i + 1 < args.Length: pngPath = args[++i]; break;
                case "--verbose": verbose = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.\nUsage: SensorPanel [--config path] [--png out.png] [--verbose] | sensors | bench");
                    return 2;
            }
        }

        // One instance owns the panel's COM port, the log file and the remembered selection.
        // A --png run is allowed beside it, and for that reason writes neither file (below).
        Mutex? single = null;
        if (pngPath is null)
        {
            bool first;
            try { single = new Mutex(true, @"Global\SensorPanel", out first); }
            catch (UnauthorizedAccessException) { first = false; }     // held by an elevated instance
            if (!first)
            {
                Console.Error.WriteLine("SensorPanel is already running.");
                System.Windows.Forms.MessageBox.Show("SensorPanel is already running. Look for its icon in the notification area.", "SensorPanel");
                return 1;
            }
        }

        string configs = Path.Combine(AppContext.BaseDirectory, "configs");
        string lastConfigFile = Path.Combine(AppContext.BaseDirectory, "last-config.txt");

        // Without this WinForms leaves the process DPI-unaware, and Windows blurs the menu.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();     // the menu in Windows' own colours
        var worker = new Worker(pngPath is not null ? new PngPresenter(pngPath) : new SerialPresenter(), verbose);
        // The tray comes before the first warning, so that it gets a balloon.
        using var tray = new Tray(worker, configs, pngPath is null ? lastConfigFile : null);

        if (pngPath is null) Log.Start(Path.Combine(AppContext.BaseDirectory, "SensorPanel.log"));
        WriteDefaultConfigs(configs);
        WarnIfNoDriver();

        // --config wins for this run; otherwise the last config picked from the menu, or the default.
        string fallback = DefaultConfig(configs);
        string? last = File.Exists(lastConfigFile) ? Path.Combine(configs, File.ReadAllText(lastConfigFile).Trim()) : null;
        worker.Start(configPath ?? (File.Exists(last) ? last : fallback), fallback, ReadSettings().LhmGpu);

        // Ctrl+C blanks the panel on the way out, through the same Exit as the menu item and as
        // Windows shutting down or signing out (which the tray hears itself). Being killed (Task
        // Manager, a closed terminal) cannot.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tray.Post(tray.Exit); };

        // The panel stays powered while the PC sleeps, so turn its screen off for the duration.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) worker.Suspend();
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) worker.Resume();
        };
        System.Windows.Forms.Application.Run();

        GC.KeepAlive(single);
        return 0;
    }

    /// <summary>settings.json beside the exe: choices about this PC rather than a layout. Optional; see docs/guide.md.</summary>
    private sealed class Settings
    {
        /// <summary>The GPU entirely through LibreHardwareMonitor, not NVML.</summary>
        public bool LhmGpu { get; set; }
    }

    /// <summary>Read once at start, with the layout config's rules. A file that will not parse is reported and ignored.</summary>
    private static Settings ReadSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Config.Options) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot read {path}, so it is ignored:\n  {e.Message}");
            return new();
        }
    }

    /// <summary>The csproj embeds every shipped file under a name starting with this.</summary>
    private const string ShippedPrefix = "configs/";

    /// <summary>The config to start with when none has been picked from the menu.</summary>
    private const string DefaultConfigName = "default-sample.json";

    /// <summary>
    /// The config to start with when none was picked, and to fall back on when the one to start
    /// with will not load: default-sample.json, or if that has been deleted, the first config in
    /// the menu. When the folder has no configs left at all, default-sample.json is written back.
    /// </summary>
    private static string DefaultConfig(string configs)
    {
        string path = Path.Combine(configs, DefaultConfigName);
        if (File.Exists(path)) return path;
        // No folder means the first-run write failed. Writing only the default here would make the
        // folder exist, and the complete write would then never be tried again.
        if (!Directory.Exists(configs)) return path;
        string[] others;
        try { others = Directory.GetFiles(configs, "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot list the configs in {configs}: {e.Message}");
            return path;
        }
        if (others.Length > 0) return Config.ByName(others)[0].Path;
        RestoreDefaultConfig(configs);
        return path;
    }

    /// <summary>
    /// Writes default-sample.json back from the exe, with media/background.png, the picture it
    /// draws, if that is missing too. The other shipped configs are not restored.
    /// </summary>
    private static void RestoreDefaultConfig(string configs)
    {
        try
        {
            foreach (var file in new[] { DefaultConfigName, "media/background.png" })
            {
                string path = Path.Combine(configs, file);
                if (File.Exists(path)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var resource = typeof(Program).Assembly.GetManifestResourceStream(ShippedPrefix + file)!;
                using var output = File.Create(path);
                resource.CopyTo(output);
            }
            Log.Info($"There were no configs in {configs}, so {DefaultConfigName} was written back.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot write {DefaultConfigName} to {configs}: {e.Message}");
        }
    }

    /// <summary>
    /// First run from a fresh copy of the exe: write the built-in layouts into the configs folder
    /// so they can be edited. They are written to a staging folder that is renamed at the end,
    /// so the configs folder either does not exist or is complete.
    /// </summary>
    private static void WriteDefaultConfigs(string configs)
    {
        if (Directory.Exists(configs)) return;
        string staging = configs + ".new";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);     // an earlier attempt that failed
            var assembly = typeof(Program).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ShippedPrefix)))
            {
                string path = Path.Combine(staging, name[ShippedPrefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(path);
                resource.CopyTo(file);
            }
            Directory.Move(staging, configs);
            Log.Info($"Created the default configs in {configs}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot write the default configs to {configs}: {e.Message}");
        }
    }

    // CPU temperature, clock, power and motherboard fans come from LibreHardwareMonitor's kernel
    // driver. That needs administrator rights, which app.manifest makes Windows require, and the
    // PawnIO driver; say so when that is missing instead of showing blanks.
    private static void WarnIfNoDriver()
    {
        if (LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled) return;

        Log.Warn("WARNING: CPU temperature, clock speed, power and fan sensors are unavailable; their widgets will show \"--\"." +
                 "\n  The PawnIO driver is not installed. Install it once with: winget install namazso.PawnIO" +
                 "\n  Load, memory, GPU, disk and network sensors work regardless.");
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
