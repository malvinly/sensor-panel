namespace SensorPanel;

/// <summary>
/// Which config the panel shows: the one picked from the tray menu ("yours"), or while a game runs,
/// the gaming layout from settings.json. The tray's bookkeeping apart from WinForms, so the tests
/// can drive it; used on the UI thread only, fed every load and failed load the worker reports.
///
/// Switching happens only when a game session starts or ends. A pick from the menu during a game
/// therefore stays on the panel until the game ends, and becomes yours, as any pick does. Picking
/// the gaming layout during a game is the exception: it is what the panel shows then anyway, so
/// it is not taken as a choice, and the panel still goes back to yours when the game ends.
///
/// The worker keeps only the latest request (<see cref="Worker.Switch"/>), so a switch for a game
/// would replace a pick it has not loaded yet. While a pick is pending, a game starting or ending
/// therefore asks for nothing: the pick is what the panel will show, and it becomes yours.
///
/// The tray starts looking for games only after the first config has loaded, so that
/// <see cref="Yours"/> is never the gaming layout merely because a game was running at startup.
/// </summary>
internal sealed class Selection
{
    private string? _pending;   // the latest pick, until the worker reports it loaded or failed

    /// <summary>The layout to show while a game runs, as a full path; null when switching for games is off.</summary>
    public string? GamingConfig { get; set; }

    /// <summary>The config the worker last loaded.</summary>
    public string? Active { get; private set; }

    /// <summary>What the panel shows when no game runs: the first config that loaded, then each one picked from the menu that loaded.</summary>
    public string? Yours { get; private set; }

    public bool GameRunning { get; private set; }

    /// <summary>A pick from the menu, before the worker has loaded it.</summary>
    public void Pick(string path)
    {
        if (GameRunning && Same(path, GamingConfig)) return;
        _pending = path;
    }

    /// <summary>The worker has loaded <paramref name="path"/>. Returns whether it was picked from the menu, and so is to be remembered.</summary>
    public bool Loaded(string path)
    {
        Active = path;
        bool picked = Same(path, _pending);
        if (picked) _pending = null;
        if (picked || Yours is null) Yours = path;
        return picked;
    }

    /// <summary>The worker could not load <paramref name="path"/>, and kept what it had.</summary>
    public void Failed(string path)
    {
        if (Same(path, _pending)) _pending = null;
    }

    /// <summary>A game session has started. Returns the config to switch to, or null when there is nothing to do.</summary>
    public string? GameStarted()
    {
        GameRunning = true;
        return _pending is not null || Same(Active, GamingConfig) ? null : GamingConfig;
    }

    /// <summary>The game session has ended. Returns the config to switch back to, or null when there is nothing to do.</summary>
    public string? GameEnded()
    {
        GameRunning = false;
        return _pending is not null || Same(Active, Yours) ? null : Yours;
    }

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
