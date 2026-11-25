using System.Diagnostics;
using System.Text.Json;
using SensorPanel.Display;
using SensorPanel.Layout;
using SensorPanel.Sensors;

namespace SensorPanel;

/// <summary>
/// The one thread that touches the sensors, the renderer and the presenter: poll, render,
/// present, once per refreshMs. Writes to the panel block for up to 2.4 s, so the tray's UI
/// thread never does any of this itself; it only leaves requests here (<see cref="Switch"/>,
/// <see cref="SetBrightness"/>, <see cref="Stop"/>, and the pauses for sleep and for the monitors being off), which are
/// picked up between frames, never in the middle of one.
/// </summary>
public sealed class Worker(IPresenter presenter, bool verbose)
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private string? _requested, _fallback;
    private bool _lhmGpu, _upsideDown;
    private int _fallbackBrightness;
    private int _chosenBrightness = NoBrightness;   // from the tray; set on the UI thread, read on this one
    private int _appliedChoice = NoBrightness;      // _chosenBrightness as the presenter was last configured with; worker thread only
    private const int NoBrightness = -1;
    private WeatherSettings? _weather;
    private WallPowerSettings? _wallPower;
    private readonly ManualResetEventSlim _dark = new(false);   // set once the panel has been blanked for sleep
    private volatile bool _stop, _suspended, _displaysOff;

    // Owned by the worker thread. _config and _renderer are null when not even the default config could be loaded.
    private Config? _config;
    private SensorHub? _sensors;
    private Renderer? _renderer;
    private long _next;
    private bool _loggedAsleep, _loggedDisplaysOff;     // the reasons for a dark panel, as last logged

    /// <summary>
    /// Raised on the worker thread when a config has loaded and is now running, with the path
    /// exactly as it was given to <see cref="Start"/> or <see cref="Switch"/>.
    /// </summary>
    public event Action<string>? ActiveChanged;

    /// <summary>Raised on the worker thread when a config could not be loaded, with the path as it was given; what was running keeps running.</summary>
    public event Action<string>? LoadFailed;

    /// <param name="fallbackPath">Loaded instead if <paramref name="configPath"/> cannot be.</param>
    /// <param name="lhmGpu">"lhmGpu" from settings.json, for the SensorHub.</param>
    /// <param name="weather">"weather" from settings.json, for the SensorHub.</param>
    /// <param name="wallPower">"wallPower" from settings.json, for the SensorHub.</param>
    /// <param name="upsideDown">"upsideDown" from settings.json: every config's orientation is turned 180°.</param>
    /// <param name="fallbackBrightness">"brightness" from settings.json, for configs that set none of their own.</param>
    /// <param name="chosenBrightness">Picked from the tray, over every config's and settings.json's; null for none.</param>
    public void Start(string configPath, string fallbackPath, bool lhmGpu, WeatherSettings? weather, WallPowerSettings? wallPower, bool upsideDown, int fallbackBrightness, int? chosenBrightness)
    {
        _requested = configPath;
        _fallback = fallbackPath;
        _lhmGpu = lhmGpu;
        _weather = weather;
        _wallPower = wallPower;
        _upsideDown = upsideDown;
        _fallbackBrightness = fallbackBrightness;
        _chosenBrightness = chosenBrightness ?? NoBrightness;
        _thread = new Thread(Run) { Name = "Panel", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Asks for another config. If it cannot be loaded, the current one keeps running.</summary>
    public void Switch(string configPath)
    {
        Interlocked.Exchange(ref _requested, configPath);
        _wake.Set();
    }

    /// <summary>
    /// A brightness picked from the tray, over every config's and settings.json's, or null to
    /// leave it to them again. Sent with the next frame, which comes at once, or once the panel is
    /// lit again if it is dark for sleep or the monitors.
    /// </summary>
    public void SetBrightness(int? percent)
    {
        Volatile.Write(ref _chosenBrightness, percent ?? NoBrightness);
        _wake.Set();
    }

    /// <summary>
    /// The PC is about to sleep: blanks the panel and pauses until <see cref="Resume"/>. Windows
    /// allows about two seconds before it suspends, so this waits that long for the blank to go
    /// out; if a full frame is being sent it may not make it, and the panel stays lit.
    /// </summary>
    public void Suspend()
    {
        _dark.Reset();
        _suspended = true;
        _wake.Set();
        _dark.Wait(2000);
    }

    /// <summary>The PC has woken: lights the panel again and carries on.</summary>
    public void Resume()
    {
        _suspended = false;
        _wake.Set();
    }

    /// <summary>
    /// Windows has turned the monitors off, or on again. Unless the layout opts out, the panel
    /// goes dark with them and nothing is polled. Unlike <see cref="Suspend"/> this does not
    /// wait for the blank: nothing is about to stop the worker from sending it.
    /// </summary>
    public void SetDisplaysOff(bool off)
    {
        if (_displaysOff == off) return;    // Windows repeats itself (dimmed, then on); an extra frame is all a wake would do
        _displaysOff = off;
        _wake.Set();
    }

    /// <summary>Blanks the panel and ends the thread. Returns when that is done: up to 2.4 s if a full frame is being sent.</summary>
    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join();
    }

    private void Run()
    {
        try
        {
            while (!_stop)
            {
                // With nothing on the panel (startup), a config that will not load gives way to the default.
                if (Interlocked.Exchange(ref _requested, null) is { } path && !Load(path) && _config is null && path != _fallback)
                    Load(_fallback!);
                if (_config is not null && Volatile.Read(ref _chosenBrightness) != _appliedChoice) ConfigurePresenter();
                if (Paused())
                {
                    presenter.Blank();
                    _dark.Set();
                    while (!_stop)
                    {
                        _wake.WaitOne();
                        if (!Paused()) break;
                        _dark.Set();    // still dark, so a Suspend that came in meanwhile need not wait for a blank
                    }
                    if (_stop) break;
                    if (_renderer is not null) presenter.Present(_renderer.Frame);  // nothing has changed, so this only turns the screen on
                    _next = _clock.ElapsedMilliseconds;
                }

                if (_config is null) { _wake.WaitOne(); continue; }

                long t0 = _clock.ElapsedMilliseconds;
                _sensors!.Update();
                long t1 = _clock.ElapsedMilliseconds;
                bool changed = _renderer!.Render(_sensors.Snapshot);
                long t2 = _clock.ElapsedMilliseconds;
                if (changed || presenter.NeedsFrame) presenter.Present(_renderer.Frame);

                if (verbose)
                {
                    var sp = presenter as SerialPresenter;
                    Console.WriteLine($"sensors {t1 - t0,3} ms  render {t2 - t1,2} ms  present {_clock.ElapsedMilliseconds - t2,4} ms  " +
                                      $"{sp?.LastRects,3} rects {sp?.LastBytes,6} B  cpu {Environment.CpuUsage.TotalTime.TotalMilliseconds:F0} ms total");
                }

                // Fixed cadence; if a frame overran (a full paint takes 2.4 s), start fresh from now.
                _next += _config.RefreshMs;
                long now = _clock.ElapsedMilliseconds;
                if (_next < now) _next = now;
                _wake.WaitOne((int)(_next - now));
            }
            presenter.Blank();
        }
        catch (Exception e)
        {
            // Nothing is expected to land here, but a tray app that stopped updating must say so.
            Log.Warn($"SensorPanel has stopped updating the panel: {e}");
        }
        finally
        {
            Unload();
            presenter.Dispose();
        }
    }

    /// <summary>
    /// Whether the panel is to be dark: the PC is going to sleep, or the monitors are off and the
    /// layout has not opted out. The opt-out is read here because only this thread may touch
    /// _config. Either reason can come or go while the other holds, so each change is logged
    /// on its own line, with what it does to the panel.
    /// </summary>
    private bool Paused()
    {
        bool asleep = _suspended, displaysOff = _displaysOff && _config is { PauseWhenDisplayOff: true };
        if (asleep != _loggedAsleep)
            Log.Info(asleep ? $"The PC is going to sleep: {Panel(true, _loggedDisplaysOff)}." : $"The PC is awake: {Panel(false, _loggedDisplaysOff)}.");
        if (displaysOff != _loggedDisplaysOff)
            Log.Info(displaysOff ? $"The monitors are off: {Panel(true, asleep)}." : $"The monitors are on: {Panel(false, asleep)}.");
        (_loggedAsleep, _loggedDisplaysOff) = (asleep, displaysOff);
        return asleep || displaysOff;

        static string Panel(bool off, bool otherReason) => otherReason ? "panel stays off" : off ? "panel off" : "panel on";
    }

    /// <summary>
    /// Everything that can fail happens before anything of the running config is touched, so a
    /// config that cannot be loaded never costs the one on the panel. The SensorHub is opened
    /// once and re-configured after that; that step comes last because it cannot be undone.
    /// </summary>
    private bool Load(string path)
    {
        Config config;
        Renderer? renderer = null;
        try
        {
            config = Config.Load(path);
            renderer = new Renderer(config, Path.GetDirectoryName(Path.GetFullPath(path))!);
            var keys = config.Widgets.SelectMany(w => w.SensorKeys());
            if (_sensors is null) _sensors = new SensorHub(config.RefreshMs, config.SensorIntervals, keys, _lhmGpu, _weather, _wallPower);
            else _sensors.Configure(config.RefreshMs, config.SensorIntervals, keys);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            renderer?.Dispose();
            Log.Warn($"Cannot load {path}:\n  {e.Message}");
            LoadFailed?.Invoke(path);
            return false;
        }

        _renderer?.Dispose();
        _config = config;
        _renderer = renderer;
        _next = _clock.ElapsedMilliseconds;
        ConfigurePresenter();
        Log.Info($"Running {Path.GetFileName(path)} ({config.Widgets.Count} widgets, every {config.RefreshMs} ms).");
        ActiveChanged?.Invoke(path);
        return true;
    }

    private void ConfigurePresenter()
    {
        _appliedChoice = Volatile.Read(ref _chosenBrightness);
        var (orientation, brightness) = _config!.ForPanel(_upsideDown, _fallbackBrightness, _appliedChoice == NoBrightness ? null : _appliedChoice);
        presenter.Configure(orientation, brightness);
    }

    private void Unload()
    {
        _renderer?.Dispose();
        _sensors?.Dispose();
        (_config, _renderer, _sensors) = (null, null, null);
    }
}
