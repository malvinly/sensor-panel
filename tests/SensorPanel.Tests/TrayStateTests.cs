namespace SensorPanel.Tests;

/// <summary><see cref="TrayState"/>: state.json, what the tray menu remembers for the next start.</summary>
public sealed class TrayStateTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StatePath => Path.Combine(_dir.Path, "state.json");

    [Fact]
    public void What_is_saved_is_loaded_at_the_next_start()
    {
        var state = TrayState.Load(StatePath, writable: true);
        state.Config = @"default\default.json";
        state.Brightness = 60;
        state.Save();

        var loaded = TrayState.Load(StatePath, writable: true);
        Assert.Equal(@"default\default.json", loaded.Config);
        Assert.Equal(60, loaded.Brightness);
        Assert.False(File.Exists(StatePath + ".new"));
    }

    [Fact]
    public void Layout_default_is_saved_as_no_brightness()
    {
        var state = TrayState.Load(StatePath, writable: true);
        state.Brightness = 60;
        state.Save();
        state.Brightness = null;
        state.Save();

        Assert.Null(TrayState.Load(StatePath, writable: true).Brightness);
    }

    [Fact]
    public void No_file_remembers_nothing()
    {
        var state = TrayState.Load(StatePath, writable: true);
        Assert.Null(state.Config);
        Assert.Null(state.Brightness);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "brightness": "bright" }""")]
    public void A_file_that_will_not_parse_is_ignored(string json)
    {
        _dir.Write("state.json", json);
        var state = TrayState.Load(StatePath, writable: true);
        Assert.Null(state.Config);
        Assert.Null(state.Brightness);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_brightness_outside_0_to_100_is_ignored_but_the_config_is_kept(int brightness)
    {
        _dir.Write("state.json", $$"""{ "config": "gaming\\gaming.json", "brightness": {{brightness}} }""");
        var state = TrayState.Load(StatePath, writable: true);
        Assert.Equal(@"gaming\gaming.json", state.Config);
        Assert.Null(state.Brightness);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void A_brightness_at_either_end_of_0_to_100_is_kept(int brightness)
    {
        var state = TrayState.Load(StatePath, writable: true);
        state.Brightness = brightness;
        state.Save();
        Assert.Equal(brightness, TrayState.Load(StatePath, writable: true).Brightness);
    }

    [Fact]
    public void A_png_run_reads_the_file_but_never_writes_it()
    {
        string written = _dir.Write("state.json", """{ "config": "gaming\\gaming.json" }""");
        var state = TrayState.Load(StatePath, writable: false);
        Assert.Equal(@"gaming\gaming.json", state.Config);

        state.Brightness = 60;
        state.Save();
        Assert.Equal("""{ "config": "gaming\\gaming.json" }""", File.ReadAllText(written));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void A_file_held_by_another_program_is_not_overwritten_by_the_next_pick()
    {
        string written = _dir.Write("state.json", """{ "config": "gaming\\gaming.json", "brightness": 40 }""");
        TrayState state;
        using (new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.None))
            state = TrayState.Load(StatePath, writable: true);

        state.Brightness = 60;
        state.Save();
        var reloaded = TrayState.Load(StatePath, writable: true);
        Assert.Equal(@"gaming\gaming.json", reloaded.Config);
        Assert.Equal(40, reloaded.Brightness);
    }
}
