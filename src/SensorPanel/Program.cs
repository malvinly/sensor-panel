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

        // One instance owns the panel's COM port, the log file and state.json.
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

        // Without this WinForms leaves the process DPI-unaware, and Windows blurs the menu.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();     // the menu in Windows' own colours
        var worker = new Worker(pngPath is not null ? new PngPresenter(pngPath) : new SerialPresenter(), verbose);
        // The tray comes before the first warning, so that it gets a balloon.
        using var tray = new Tray(worker, configs);

        if (pngPath is null) Log.Start(Path.Combine(AppContext.BaseDirectory, "SensorPanel.log"));
        WriteDefaultConfigs(configs);
        WriteDefaultSettings(SettingsPath);
        WarnIfNoDriver();
        var settings = ReadSettings();
        // A --png run is a preview beside the tray instance: it shows the config it was given.
        if (pngPath is null) tray.WatchGames(settings.Gaming);

        // What was picked from the menu. Read only now, after Log.Start, so a problem with the file
        // reaches the log; a --png run reads it too, for the same config, but never writes it.
        tray.State = TrayState.Load(Path.Combine(AppContext.BaseDirectory, "state.json"), writable: pngPath is null);

        // --config wins for this run; otherwise the last config picked from the menu, or the default.
        string fallback = DefaultConfig(configs);
        string? last = tray.State.Config is { } picked ? Path.Combine(configs, picked) : null;
        worker.Start(configPath ?? (File.Exists(last) ? last : fallback), fallback, settings.LhmGpu, settings.Weather, settings.UpsideDown, settings.Brightness, tray.State.Brightness);

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

    /// <summary>
    /// settings.json beside the exe: choices about this PC rather than a layout. Written on first
    /// start from the copy inside the exe (settings.json in the project), and optional; see docs/guide.md.
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>The GPU entirely through LibreHardwareMonitor, not NVML.</summary>
        public bool LhmGpu { get; set; }
        public GamingSettings? Gaming { get; set; }
        /// <summary>Where the weather.* keys are for. Sent to Open-Meteo, rounded, while a layout shows the weather.</summary>
        public WeatherSettings? Weather { get; set; }
        /// <summary>The panel is mounted the other way up: every config's orientation is turned 180°.</summary>
        public bool UpsideDown { get; set; }
        /// <summary>0-100, for every config that does not set its own.</summary>
        public int Brightness { get; set; } = 30;
    }

    /// <summary>"gaming": the layout to show while a game runs (see <see cref="Tray.WatchGames"/>).</summary>
    internal sealed class GamingSettings
    {
        /// <summary>Relative to the configs folder, like "gaming/gaming.json".</summary>
        public string? Config { get; set; }
        /// <summary>Patterns for the exes that are games, "!" first for those that never are (<see cref="GamePatterns"/>). None but "!" ones: switching is off.</summary>
        public List<string?>? Games { get; set; }
        /// <summary>How often the running processes are looked through, in ms.</summary>
        public int ScanMs { get; set; } = 2000;
        /// <summary>How long after the last game was seen the panel goes back, in ms.</summary>
        public int EndAfterMs { get; set; } = 30_000;

        internal const int MinScanMs = 500, MaxScanMs = 60_000, MaxEndAfterMs = 3_600_000;

        /// <summary>
        /// Whether switching for games is on, and if so the gaming layout's full path and the patterns.
        /// Off without a word when no pattern names a game (the shipped file). Anything else that is
        /// wrong is reported, and turns it off: the timings out of range, a config that is missing,
        /// not a path, or outside <paramref name="configs"/> (the elevated app reads only layouts from there).
        /// </summary>
        internal (string Config, GamePatterns Patterns)? Check(string configs)
        {
            var patterns = new GamePatterns(Games ?? []);
            if (patterns.IsEmpty) return null;

            string? problem = null, path = null;
            if (ScanMs is < MinScanMs or > MaxScanMs)
                problem = $"scanMs must be from {MinScanMs} to {MaxScanMs}";
            else if (EndAfterMs is < 0 or > MaxEndAfterMs)
                problem = $"endAfterMs must be from 0 to {MaxEndAfterMs}";
            else if (string.IsNullOrWhiteSpace(Config))
                problem = "config is missing";
            else
            {
                string root = Path.GetFullPath(configs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                try { path = Path.GetFullPath(Path.Combine(root, Config)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { problem = $"config \"{Config}\" is not a path"; }
                if (path is not null && !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    problem = $"config \"{Config}\" is not inside the configs folder";
                else if (path is not null && !File.Exists(path))
                    problem = $"config \"{Config}\" is not in the configs folder";
            }

            if (problem is null) return (path!, patterns);
            Log.Warn($"settings.json: gaming.{problem}, so the panel does not switch for games.");
            return null;
        }
    }

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>The csproj embeds the shipped settings.json under this name.</summary>
    internal const string ShippedSettings = "settings.json";

    /// <summary>
    /// Writes the shipped settings.json, where switching for games is off, if there is none: on
    /// first start, and after it is deleted. One that is there is never touched. It is written under
    /// another name and renamed, so a write cut short never leaves a file that will not parse.
    /// </summary>
    internal static void WriteDefaultSettings(string path)
    {
        if (File.Exists(path)) return;
        string staging = path + ".new";
        try
        {
            using (var resource = typeof(Program).Assembly.GetManifestResourceStream(ShippedSettings)!)
            using (var file = File.Create(staging))
                resource.CopyTo(file);
            File.Move(staging, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot write {path}: {e.Message}");
        }
    }

    /// <summary>Read once at start, with the layout config's rules. A file that will not parse is reported and ignored.</summary>
    private static Settings ReadSettings()
    {
        string path = SettingsPath;
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
    private const string DefaultConfigName = "default/default.json";

    /// <summary>
    /// The config to start with when none was picked, and to fall back on when the one to start
    /// with will not load: default/default.json, or if that has been deleted, the first config in
    /// the menu. When the folder has no configs left at all, default/default.json is written back.
    /// </summary>
    internal static string DefaultConfig(string configs)
    {
        // GetFullPath turns the "/" into "\", so the path matches the menu's when it is ticked.
        string path = Path.GetFullPath(Path.Combine(configs, DefaultConfigName));
        if (File.Exists(path)) return path;
        // No folder means the first-run write failed. Writing only the default here would make the
        // folder exist, and the complete write would then never be tried again.
        if (!Directory.Exists(configs)) return path;
        string[] others;
        try { others = Config.Files(configs); }
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
    /// Writes default/default.json back from the exe, with default/background.png, the picture it
    /// draws, if that is missing too.
    /// </summary>
    private static void RestoreDefaultConfig(string configs)
    {
        try
        {
            foreach (var file in new[] { DefaultConfigName, "default/background.png" })
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
    /// First run from a fresh copy of the exe: write the built-in layout (the default folder:
    /// default.json and its background) into the configs folder so it can be edited. They are written to a staging folder that is renamed at the end,
    /// so the configs folder either does not exist or is complete.
    /// </summary>
    internal static void WriteDefaultConfigs(string configs)
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
