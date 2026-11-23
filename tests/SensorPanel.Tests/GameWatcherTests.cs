using System.Collections.Concurrent;
using System.Diagnostics;
using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary>
/// <see cref="Program.GamingSettings.Check"/>: when settings.json turns switching for games on,
/// and the gaming layout's path it then hands the tray.
/// </summary>
public class GamingSettingsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _configs;

    public GamingSettingsTests()
    {
        _configs = Path.Combine(_dir.Path, "configs");
        _dir.Write("configs/gaming/gaming.json", "{}");
        _dir.Write("outside.json", "{}");
    }

    public void Dispose() => _dir.Dispose();

    private static Program.GamingSettings Gaming(string? config = "gaming/gaming.json", int scanMs = 2000, int endAfterMs = 30_000, params string?[] games) =>
        new() { Config = config, Games = games.Length > 0 ? [.. games] : ["*/steamapps/common/*"], ScanMs = scanMs, EndAfterMs = endAfterMs };

    [Fact]
    public void A_valid_section_gives_the_layout_exactly_as_the_tray_menu_lists_it()
    {
        // Selection compares this with the menu's paths, so a pick of the gaming layout is recognised.
        var check = Gaming().Check(_configs);
        Assert.NotNull(check);
        string menu = Assert.Single(Config.Files(_configs), p => p.EndsWith("gaming.json"));
        Assert.Equal(menu, check.Value.Config, StringComparer.OrdinalIgnoreCase);
        Assert.True(check.Value.Patterns.Matches(@"C:\Steam\steamapps\common\Game\game.exe"));
    }

    [Fact]
    public void No_game_pattern_is_off()
    {
        Assert.Null(new Program.GamingSettings { Config = "gaming/gaming.json", Games = [] }.Check(_configs));
        Assert.Null(new Program.GamingSettings { Config = "gaming/gaming.json", Games = null }.Check(_configs));
        Assert.Null(new Program.GamingSettings { Config = "gaming/gaming.json", Games = ["!*/steamapps/common/wallpaper_engine/*"] }.Check(_configs));
    }

    [Theory]
    [InlineData(499, 30_000)]
    [InlineData(60_001, 30_000)]
    [InlineData(2000, -1)]
    [InlineData(2000, 3_600_001)]
    public void A_timing_out_of_range_is_off(int scanMs, int endAfterMs) =>
        Assert.Null(Gaming(scanMs: scanMs, endAfterMs: endAfterMs).Check(_configs));

    [Theory]
    [InlineData(500, 0)]
    [InlineData(60_000, 3_600_000)]
    public void The_timing_limits_themselves_are_allowed(int scanMs, int endAfterMs) =>
        Assert.NotNull(Gaming(scanMs: scanMs, endAfterMs: endAfterMs).Check(_configs));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("gaming/missing.json")]     // renamed or deleted
    [InlineData("../outside.json")]         // the elevated app reads layouts only from the configs folder
    [InlineData("a\0b.json")]               // not a path: must not end the app at startup
    public void A_config_that_is_not_a_layout_in_the_configs_folder_is_off(string? config) =>
        Assert.Null(Gaming(config).Check(_configs));

    [Fact]
    public void An_absolute_config_outside_the_folder_is_off() =>
        Assert.Null(Gaming(Path.Combine(_dir.Path, "outside.json")).Check(_configs));

    [Fact]
    public void An_absolute_config_inside_the_folder_is_allowed() =>
        Assert.NotNull(Gaming(Path.Combine(_configs, "gaming", "gaming.json")).Check(_configs));
}

/// <summary>
/// <see cref="GameWatcher"/> against real processes: a copy of ping.exe under a steamapps\common
/// folder of its own plays the game, so the path patterns need the real exe path.
/// </summary>
public class GameWatcherTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _exe;
    private readonly BlockingCollection<string?> _changes = [];

    public GameWatcherTests()
    {
        _exe = Path.Combine(_dir.Path, "steamapps", "common", "FakeGame", "fakegame.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), _exe);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>A watcher that scans every 50 ms, with no start or end delay, for this test's folder only.</summary>
    private GameWatcher Watcher(params string[] patterns) =>
        new(new GamePatterns(patterns), scanMs: 50, endAfterMs: 0, game => _changes.Add(game), startMs: 0);

    private Process StartGame() =>
        Process.Start(new ProcessStartInfo(_exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    private string? Next() => _changes.TryTake(out var change, 5000) ? change : throw new TimeoutException("no game session change within 5 s");

    private static void Stop(Process game)
    {
        game.Kill();
        game.WaitForExit();
        game.Dispose();
    }

    [Fact]
    public void A_game_matched_by_its_path_starts_a_session_with_its_full_path_and_its_exit_ends_it()
    {
        string folder = Path.GetFileName(_dir.Path);
        using var watcher = Watcher($"*/{folder}/steamapps/common/*");
        watcher.Start();
        Assert.False(_changes.TryTake(out _, 300));     // nothing running yet

        var game = StartGame();
        try { Assert.Equal(_exe, Next(), StringComparer.OrdinalIgnoreCase); }
        finally { Stop(game); }
        Assert.Null(Next());
    }

    [Fact]
    public void An_excluded_path_starts_nothing()
    {
        string folder = Path.GetFileName(_dir.Path);
        using var watcher = Watcher($"*/{folder}/steamapps/common/*", $"!*/{folder}/steamapps/common/FakeGame/*");
        watcher.Start();
        var game = StartGame();
        try { Assert.False(_changes.TryTake(out _, 500)); }
        finally { Stop(game); }
    }

    [Fact]
    public void Nothing_is_raised_once_disposed()
    {
        string folder = Path.GetFileName(_dir.Path);
        var watcher = Watcher($"*/{folder}/steamapps/common/*");
        watcher.Start();
        watcher.Dispose();
        var game = StartGame();
        try { Assert.False(_changes.TryTake(out _, 500)); }
        finally { Stop(game); }
    }
}
