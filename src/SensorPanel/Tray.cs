using System.Diagnostics;
using System.Runtime.InteropServices;
using SensorPanel.Layout;
using Forms = System.Windows.Forms;

namespace SensorPanel;

/// <summary>
/// The notification-area icon and its right-click menu, both WinForms rather than WPF: a WPF
/// menu's first right-click was measured adding 57 MB and 8 threads for the rest of the run.
/// Runs on the UI thread and only ever posts requests to the <see cref="Worker"/>.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly MessageWindow _window;
    private readonly Worker _worker;
    private readonly string _configsDirectory;
    private readonly string? _lastConfigFile;
    private string? _active, _picked;

    /// <param name="lastConfigFile">Where the menu choice is remembered, or null not to remember it.</param>
    public Tray(Worker worker, string configsDirectory, string? lastConfigFile)
    {
        _worker = worker;
        _configsDirectory = configsDirectory;
        _lastConfigFile = lastConfigFile;
        _window = new MessageWindow(this);

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "SensorPanel",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // WinForms opens the menu with Cancel already set when it has no items, as it has none the first time.
        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };

        // Both are raised on other threads.
        Log.Warned += message => Post(() =>
            _icon.ShowBalloonTip(10_000, "SensorPanel", message, Forms.ToolTipIcon.Warning));
        worker.ActiveChanged += path => Post(() =>
        {
            _active = path;
            if (path == _picked) Remember(path);    // the worker reports the path exactly as Pick gave it
            string text = "SensorPanel: " + Config.DisplayName(path);
            _icon.Text = text.Length > 127 ? text[..127] : text;    // the longest tooltip Windows takes
        });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, later. Callable from any thread.</summary>
    public void Post(Action action) => _window.BeginInvoke(action);

    /// <summary>
    /// Rebuilt every time, so a config dropped into the folder, or a renamed one, shows without a
    /// restart. Each config is listed by its "name", or its file name if it has none.
    /// </summary>
    private void BuildMenu()
    {
        _menu.Items.Clear();

        var configs = Directory.Exists(_configsDirectory) ? Directory.GetFiles(_configsDirectory, "*.json").ToList() : [];
        // A config started with --config from somewhere else is listed too, so something is ticked.
        if (_active is not null && !configs.Contains(_active, StringComparer.OrdinalIgnoreCase)) configs.Add(_active);

        foreach (var (path, name) in Config.ByName(configs))
        {
            var item = new Forms.ToolStripMenuItem
            {
                Text = name.Replace("&", "&&"),     // a lone & marks an access key
                Checked = path.Equals(_active, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => Pick(path);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var log = new Forms.ToolStripMenuItem { Text = "Show log", Enabled = Log.FilePath is not null };
        log.Click += (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath!) { UseShellExecute = true });
        _menu.Items.Add(log);
        var exit = new Forms.ToolStripMenuItem { Text = "Exit" };
        exit.Click += (_, _) => Exit();
        _menu.Items.Add(exit);
    }

    /// <summary>Picking the config that is already running reloads it, which is how an edit is applied.</summary>
    private void Pick(string path)
    {
        _picked = path;
        _worker.Switch(path);
    }

    /// <summary>
    /// Only a config that actually loaded is remembered: one that failed would be loaded again,
    /// and fail again, at every start.
    /// </summary>
    private void Remember(string path)
    {
        if (_lastConfigFile is null) return;
        if (!Path.GetDirectoryName(path)!.Equals(_configsDirectory, StringComparison.OrdinalIgnoreCase)) return;
        try { File.WriteAllText(_lastConfigFile, Path.GetFileName(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot remember the selection in {_lastConfigFile}: {e.Message}");
        }
    }

    /// <summary>
    /// The one way out: the Exit item, Ctrl+C (see Program.cs), and Windows shutting down or
    /// signing out (see <see cref="MessageWindow"/>) all call this. Blocks the UI thread until
    /// the worker has blanked the panel.
    /// </summary>
    public void Exit()
    {
        _worker.Stop();
        Forms.Application.ExitThread();
    }

    public void Dispose()
    {
        _icon.Dispose();    // otherwise the icon lingers until the mouse passes over it
        _window.Dispose();
    }

    /// <summary>
    /// A form that is never shown. Other threads reach the UI thread through its handle
    /// (<see cref="Post"/>), and being a top-level window it hears from Windows: when the
    /// monitors turn off and on, and when Windows is about to shut down or sign out.
    /// </summary>
    private sealed class MessageWindow : Forms.Form
    {
        private const int WM_QUERYENDSESSION = 0x0011, WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
        private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");     // GUID_CONSOLE_DISPLAY_STATE

        private readonly Tray _tray;
        private readonly IntPtr _registration;

        public MessageWindow(Tray tray)
        {
            _tray = tray;
            CreateHandle();
            // Windows replies at once with the current state, then sends every change.
            _registration = RegisterPowerSettingNotification(Handle, ConsoleDisplayState, 0);
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam == PBT_POWERSETTINGCHANGE)
            {
                // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then the data:
                // 0 off, 1 on, 2 dimmed (which is still on).
                _tray._worker.SetDisplaysOff(Marshal.ReadInt32(m.LParam, 20) == 0);
                m.Result = 1;
            }
            else if (m.Msg == WM_QUERYENDSESSION)
            {
                // Windows waits for the answer, so the panel is blank before it carries on. The
                // app quits even if the shutdown is then cancelled.
                _tray.Exit();
                m.Result = 1;
            }
            else base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) UnregisterPowerSettingNotification(_registration);
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, int flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterPowerSettingNotification(IntPtr registration);
    }
}
