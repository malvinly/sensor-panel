using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace SensorPanel;

/// <summary>
/// "games" from settings.json's "gaming": which running exes are games. A pattern with a slash is
/// matched against the exe's whole path, one without against its file name, in both cases ignoring
/// case, with * for any characters (slashes too) and ? for one. Backslashes count as slashes, so
/// "*\\steamapps\\common\\*" and "*/steamapps/common/*" are the same. A pattern starting with !
/// excludes: an exe it matches is never a game, whatever else matches it. Spaces around a pattern,
/// or after the !, are dropped: no exe path starts or ends with one.
/// </summary>
internal sealed class GamePatterns
{
    private readonly string[] _games, _exclude;

    public GamePatterns(IEnumerable<string?> patterns)
    {
        var all = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim().Replace('\\', '/')).ToArray();
        _games = all.Where(p => !p.StartsWith('!')).ToArray();
        _exclude = all.Where(p => p.StartsWith('!')).Select(p => p[1..].Trim()).Where(p => p.Length > 0).ToArray();
    }

    /// <summary>No game patterns, only exclusions or none at all: switching for games is off.</summary>
    public bool IsEmpty => _games.Length == 0;

    /// <param name="exe">The exe's full path, or only its file name when the path cannot be read.</param>
    public bool Matches(string exe)
    {
        string path = exe.Replace('\\', '/');
        string name = path[(path.LastIndexOf('/') + 1)..];
        return _games.Any(p => Match(p, path, name)) && !_exclude.Any(p => Match(p, path, name));
    }

    // MatchesSimpleExpression reads a backslash as an escape, which is why both sides use slashes.
    private static bool Match(string pattern, string path, string name) =>
        FileSystemName.MatchesSimpleExpression(pattern, pattern.Contains('/') ? path : name);
}

/// <summary>
/// When a game session starts and ends, from scans that each say whether a game is running. It
/// starts once games have been seen for <paramref name="startMs"/> without a break, so that a match
/// that lasts only a moment does not switch the panel there and back, which costs a reload each
/// way and the graphs' history. It ends once none has been seen for <paramref name="endAfterMs"/>
/// ("endAfterMs" in settings.json), so a game restarting, or handing over to its launcher, is one session.
/// </summary>
internal sealed class GameSession(long endAfterMs, long startMs = GameSession.StartMs)
{
    public const long StartMs = 5_000;
    private long? _seenSince;   // when the current unbroken run of scans that found a game began
    private long _lastSeen;

    public bool Active { get; private set; }

    /// <summary>One scan at <paramref name="now"/> (ms). True when this starts a session, false when it ends one, null otherwise.</summary>
    public bool? Update(bool gameRunning, long now)
    {
        if (gameRunning)
        {
            _lastSeen = now;
            _seenSince ??= now;
            if (!Active && now - _seenSince >= startMs) return Active = true;
        }
        else
        {
            _seenSince = null;
            if (Active && now - _lastSeen >= endAfterMs) return Active = false;
        }
        return null;
    }
}

/// <summary>
/// Scans the running processes every <paramref name="scanMs"/> ("scanMs" in settings.json) for a
/// game (<see cref="GamePatterns"/>) and reports when a session starts or ends (<see cref="GameSession"/>).
/// A scan reads every process's exe path, which measured ~3 ms of CPU for ~270 processes. A process
/// whose path cannot be read (protected, or gone) is matched by its file name only.
/// </summary>
internal sealed class GameWatcher : IDisposable
{
    private readonly GamePatterns _patterns;
    private readonly int _scanMs;
    private readonly GameSession _session;
    private readonly Action<string?> _changed;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    // The longest path Windows has (32,767 characters with long paths on). A shorter buffer would make
    // the query fail for longer paths, and the fallback to the bare file name would then quietly stop
    // path patterns, "!" ones too, from applying to that exe.
    private readonly char[] _path = new char[32_768];
    private bool _started, _disposed, _warned;

    /// <param name="changed">
    /// Raised on a pool thread: with the game's exe when a session starts, with null when it ends.
    /// Never raised once <see cref="Dispose"/> has returned.
    /// </param>
    /// <param name="startMs">How long a game must be seen before a session starts; only the tests shorten it.</param>
    public GameWatcher(GamePatterns patterns, int scanMs, int endAfterMs, Action<string?> changed, long startMs = GameSession.StartMs)
    {
        (_patterns, _scanMs, _session, _changed) = (patterns, scanMs, new GameSession(endAfterMs, startMs), changed);
        _timer = new Timer(_ => Scan());
    }

    /// <summary>Starts scanning. Calling it again does nothing.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
            _timer.Change(0, Timeout.Infinite);
        }
    }

    private void Scan()
    {
        // Each scan arms the next one, so two never overlap; the lock keeps Dispose from returning mid-scan.
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                string? game = FindGame();
                // A session only starts on a scan that found a game, so game is not null then.
                if (_session.Update(game is not null, Environment.TickCount64) is { } started)
                    _changed(started ? game : null);
            }
            catch (Exception e)
            {
                // Nothing is expected here, but an exception on a pool thread would end the app.
                if (!_warned) Log.Warn($"Looking for running games failed, and is tried again every {_scanMs} ms: {e.Message}");
                _warned = true;
            }
            _timer.Change(_scanMs, Timeout.Infinite);
        }
    }

    /// <summary>The exe of the first running game, or null.</summary>
    private string? FindGame()
    {
        string? found = null;
        foreach (var process in Process.GetProcesses())
        {
            using (process)     // every Process holds a handle: keep going after a match so each is disposed, not break
            {
                if (found is not null) continue;
                string exe = ImagePath(process.Id) ?? process.ProcessName + ".exe";
                if (_patterns.Matches(exe)) found = exe;
            }
        }
        return found;
    }

    private string? ImagePath(int pid)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            int size = _path.Length;
            return QueryFullProcessImageNameW(process, 0, _path, ref size) ? new string(_path, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _timer.Dispose();
    }

    // System32 only, as in Nvml: the app runs elevated, and the default search would look beside the exe first.
    private const string Kernel32 = "kernel32.dll";
    [DllImport(Kernel32), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport(Kernel32, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);
    [DllImport(Kernel32), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool CloseHandle(IntPtr handle);
}
