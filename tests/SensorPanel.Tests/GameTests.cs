namespace SensorPanel.Tests;

/// <summary><see cref="GamePatterns"/>: which exes count as games.</summary>
public class GamePatternsTests
{
    private static GamePatterns Patterns(params string?[] patterns) => new(patterns);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\KingdomComeDeliverance2\Bin\Win64\KingdomCome.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe")]
    [InlineData(@"E:\SteamLibrary\STEAMAPPS\Common\Game\game.exe")]
    public void A_path_pattern_matches_any_library_on_any_drive_ignoring_case(string exe) =>
        Assert.True(Patterns("*/steamapps/common/*").Matches(exe));

    [Fact]
    public void Backslashes_in_a_pattern_count_as_slashes()
    {
        // .NET's matcher reads a backslash as an escape; a pattern copied from Explorer must still work.
        Assert.True(Patterns(@"*\steamapps\common\*").Matches(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe"));
    }

    [Fact]
    public void A_path_pattern_does_not_match_outside_its_folder()
    {
        var patterns = Patterns("*/steamapps/common/*");
        Assert.False(patterns.Matches(@"C:\Program Files (x86)\Steam\steam.exe"));
        Assert.False(patterns.Matches(@"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void A_name_pattern_matches_the_file_name_only()
    {
        var patterns = Patterns("eldenring.exe", "dosbox*.exe");
        Assert.True(patterns.Matches(@"C:\Games\Elden Ring\ELDENRING.EXE"));
        Assert.True(patterns.Matches(@"C:\Emulators\dosbox-x.exe"));
        Assert.False(patterns.Matches(@"C:\eldenring.exe\notes.exe"));     // a folder of that name is not the exe
    }

    [Fact]
    public void A_process_known_only_by_name_matches_name_patterns_but_not_path_patterns()
    {
        // When the path cannot be read the watcher has only "name.exe".
        Assert.True(Patterns("eldenring.exe").Matches("eldenring.exe"));
        Assert.False(Patterns("*/steamapps/common/*").Matches("eldenring.exe"));
    }

    [Fact]
    public void An_exclusion_wins_over_games()
    {
        var patterns = Patterns("*/steamapps/common/*", "!*/steamapps/common/wallpaper_engine/*", "!vrserver.exe");
        Assert.False(patterns.Matches(@"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe"));
        Assert.False(patterns.Matches(@"C:\Steam\steamapps\common\SteamVR\bin\win64\vrserver.exe"));
        Assert.True(patterns.Matches(@"C:\Steam\steamapps\common\Elden Ring\Game\eldenring.exe"));
    }

    [Fact]
    public void Empty_lists_are_off_and_match_nothing()
    {
        var patterns = Patterns();
        Assert.True(patterns.IsEmpty);
        Assert.False(patterns.Matches(@"C:\Steam\steamapps\common\Game\game.exe"));
    }

    [Fact]
    public void Only_exclusions_are_off()
    {
        var patterns = Patterns("!*/steamapps/common/wallpaper_engine/*", "!vrserver.exe");
        Assert.True(patterns.IsEmpty);
        Assert.False(patterns.Matches(@"C:\Steam\steamapps\common\Game\game.exe"));
    }

    [Fact]
    public void Blank_and_null_patterns_are_ignored()
    {
        var patterns = Patterns("", "  ", null);
        Assert.True(patterns.IsEmpty);
        Assert.False(patterns.Matches("anything.exe"));
        Assert.False(Patterns("*.exe", "!", "! ").IsEmpty);
        Assert.True(Patterns("*.exe", "!", "! ").Matches("game.exe"));     // a bare "!" excludes nothing
    }
}

/// <summary><see cref="GameSession"/>: when scans that find a game start and end a session. Times in ms.</summary>
public class GameSessionTests
{
    private readonly GameSession _session = new(30_000);

    /// <summary>Scans every 2 s from <paramref name="from"/> up to and including <paramref name="to"/>; returns the changes they reported.</summary>
    private List<(long At, bool Started)> Scans(bool game, long from, long to)
    {
        var changes = new List<(long, bool)>();
        for (long t = from; t <= to; t += 2000)
            if (_session.Update(game, t) is { } started) changes.Add((t, started));
        return changes;
    }

    [Fact]
    public void A_game_seen_for_5_seconds_starts_a_session()
    {
        Assert.Empty(Scans(true, 10_000, 14_000));                  // seen for 4 s
        Assert.Equal([(16_000L, true)], Scans(true, 16_000, 30_000));
        Assert.True(_session.Active);
    }

    [Fact]
    public void A_match_gone_within_5_seconds_starts_nothing()
    {
        Assert.Empty(Scans(true, 10_000, 14_000));
        Assert.Empty(Scans(false, 16_000, 20_000));
        Assert.Empty(Scans(true, 22_000, 26_000));                  // counted afresh: 4 s again
        Assert.False(_session.Active);
    }

    [Fact]
    public void A_session_ends_30_seconds_after_a_game_was_last_seen()
    {
        Scans(true, 0, 10_000);
        Assert.Empty(Scans(false, 12_000, 38_000));                 // last seen at 10 s: 28 s ago
        Assert.Equal([(40_000L, false)], Scans(false, 40_000, 60_000));
        Assert.False(_session.Active);
    }

    [Fact]
    public void A_gap_shorter_than_30_seconds_is_the_same_session()
    {
        // The game restarting, or going back to its launcher and coming again.
        Assert.Equal([(6_000L, true)], Scans(true, 0, 10_000));
        Assert.Empty(Scans(false, 12_000, 38_000));
        Assert.Empty(Scans(true, 40_000, 50_000));
        Assert.True(_session.Active);
    }

    [Fact]
    public void After_a_session_ends_the_next_one_waits_5_seconds_again()
    {
        Scans(true, 0, 10_000);
        Scans(false, 12_000, 40_000);
        Assert.Equal([(106_000L, true)], Scans(true, 100_000, 110_000));
    }

    [Fact]
    public void The_start_comes_at_exactly_5_seconds()
    {
        var session = new GameSession(30_000);
        Assert.Null(session.Update(true, 1_000));
        Assert.Null(session.Update(true, 5_999));
        Assert.True(session.Update(true, 6_000));
    }

    [Theory]
    [InlineData(0, 12_000)]         // endAfterMs 0: the first scan that finds no game ends it
    [InlineData(10_000, 20_000)]
    [InlineData(120_000, 130_000)]
    public void The_end_follows_endAfterMs(int endAfterMs, long endsAt)
    {
        var session = new GameSession(endAfterMs);
        for (long t = 0; t <= 10_000; t += 2000) session.Update(true, t);      // last seen at 10 s
        long? ended = null;
        for (long t = 12_000; t <= 200_000 && ended is null; t += 2000)
            if (session.Update(false, t) == false) ended = t;
        Assert.Equal(endsAt, ended);
    }
}

/// <summary><see cref="Selection"/>: what the tray switches to as games start and end, and what it remembers.</summary>
public class SelectionTests
{
    private const string Default = @"C:\app\configs\default\default.json", Rows = @"C:\app\configs\simple-rows\simple-rows.json";
    private const string Gaming = @"C:\app\configs\gaming\gaming.json";
    private readonly Selection _selection = new() { GamingConfig = Gaming };

    public SelectionTests() => Assert.False(_selection.Loaded(Default));     // the config the app started with

    [Fact]
    public void A_game_switches_to_the_gaming_layout_and_back_to_yours()
    {
        Assert.Equal(Gaming, _selection.GameStarted());
        Assert.False(_selection.Loaded(Gaming));        // not a pick, so not remembered
        Assert.Equal(Default, _selection.Yours);
        Assert.Equal(Default, _selection.GameEnded());
    }

    [Fact]
    public void A_pick_during_a_game_stays_and_becomes_yours()
    {
        _selection.GameStarted();
        _selection.Loaded(Gaming);
        _selection.Pick(Rows);
        Assert.True(_selection.Loaded(Rows));
        Assert.Null(_selection.GameEnded());            // already on the panel
        Assert.Equal(Rows, _selection.Yours);
        Assert.Equal(Gaming, _selection.GameStarted()); // the next game switches again
    }

    [Fact]
    public void Picking_the_gaming_layout_during_a_game_is_not_a_choice()
    {
        _selection.GameStarted();
        _selection.Loaded(Gaming);
        _selection.Pick(Rows);
        _selection.Loaded(Rows);
        _selection.Pick(Gaming.ToUpperInvariant());     // paths compare ignoring case
        Assert.False(_selection.Loaded(Gaming.ToUpperInvariant()));
        Assert.Equal(Rows, _selection.GameEnded());
    }

    [Fact]
    public void Picking_the_gaming_layout_with_no_game_makes_it_yours()
    {
        _selection.Pick(Gaming);
        Assert.True(_selection.Loaded(Gaming));
        Assert.Null(_selection.GameStarted());          // already on the panel: no reload, graphs keep their history
        Assert.Null(_selection.GameEnded());
    }

    [Fact]
    public void A_pick_that_does_not_load_leaves_yours_as_it_was()
    {
        _selection.GameStarted();
        _selection.Loaded(Gaming);
        _selection.Pick(Rows);
        _selection.Failed(Rows);
        Assert.Equal(Default, _selection.GameEnded());  // so the game's end still leaves the gaming layout
    }

    [Fact]
    public void An_earlier_pick_that_failed_does_not_make_a_game_switch_yours()
    {
        // The gaming layout picked with no game running, but broken; fixed later without picking it again.
        _selection.Pick(Gaming);
        _selection.Failed(Gaming);
        Assert.Equal(Gaming, _selection.GameStarted());
        Assert.False(_selection.Loaded(Gaming));        // the game's switch, not that old pick: not remembered
        Assert.Equal(Default, _selection.GameEnded());
    }

    [Fact]
    public void A_pick_still_loading_when_a_game_starts_is_not_replaced()
    {
        // The worker keeps only the latest request, so switching now would drop the pick.
        _selection.Pick(Rows);
        Assert.Null(_selection.GameStarted());
        Assert.True(_selection.Loaded(Rows));
        Assert.Equal(Rows, _selection.Yours);
        Assert.Null(_selection.GameEnded());            // the pick is on the panel and is yours
    }

    [Fact]
    public void A_pick_still_loading_when_a_game_ends_is_not_replaced()
    {
        _selection.GameStarted();
        _selection.Loaded(Gaming);
        _selection.Pick(Rows);
        Assert.Null(_selection.GameEnded());
        Assert.True(_selection.Loaded(Rows));
    }

    [Fact]
    public void A_pick_that_loaded_is_pending_no_more()
    {
        _selection.Pick(Rows);
        _selection.Loaded(Rows);
        Assert.Equal(Gaming, _selection.GameStarted());
        Assert.False(_selection.Loaded(Gaming));
        Assert.Equal(Rows, _selection.GameEnded());
    }

    [Fact]
    public void A_gaming_layout_that_does_not_load_needs_no_switch_back()
    {
        _selection.GameStarted();                       // the worker cannot load it: Default stays on the panel
        Assert.Null(_selection.GameEnded());
    }
}
