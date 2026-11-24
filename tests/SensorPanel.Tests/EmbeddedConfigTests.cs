using System.Text.Json;
using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary>
/// The layout that travels inside the exe and is written out on first run. A wrong LogicalName
/// in the csproj, or a first-run write that goes wrong, would otherwise only show up on a fresh
/// copy of the published exe.
/// </summary>
[Collection(RenderingCollection.Name)]
public class EmbeddedConfigTests
{
    [Fact]
    public void The_exe_carries_the_default_and_gaming_layouts_and_settings()
    {
        // The csproj's LogicalNames; Program writes out every resource under "configs/", and settings.json beside them.
        var names = typeof(Config).Assembly.GetManifestResourceNames().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["configs/default/background.png", "configs/default/default.json", "configs/gaming/gaming.json", Program.ShippedSettings], names);
    }

    [Fact]
    public void The_first_run_gaming_layout_loads_renders_and_is_the_one_the_settings_name()
    {
        using var dir = new TempDirectory();
        string configs = Path.Combine(dir.Path, "configs");
        Program.WriteDefaultConfigs(configs);
        Program.WriteDefaultSettings(Path.Combine(dir.Path, "settings.json"));

        var settings = JsonSerializer.Deserialize<Program.Settings>(File.ReadAllText(Path.Combine(dir.Path, "settings.json")), Config.Options)!;
        settings.Gaming!.Games = ["*/steamapps/common/*"];      // as someone turning it on would
        var check = settings.Gaming.Check(configs);
        Assert.NotNull(check);                                  // the layout it names is there, with nothing to copy

        using var renderer = new Renderer(Config.Load(check.Value.Config), Path.GetDirectoryName(check.Value.Config)!);
        Assert.True(renderer.Render(new Dictionary<string, double>()));
    }

    [Fact]
    public void The_first_run_settings_parse_and_leave_switching_for_games_off()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "settings.json");
        Program.WriteDefaultSettings(path);

        // With the reader's own options: a typo in the shipped file would make the app ignore all of it.
        var settings = JsonSerializer.Deserialize<Program.Settings>(File.ReadAllText(path), Config.Options)!;
        Assert.False(settings.LhmGpu);
        Assert.False(settings.UpsideDown);
        Assert.Equal(30, settings.Brightness);
        Assert.Equal(30, JsonSerializer.Deserialize<Program.Settings>("{}", Config.Options)!.Brightness);   // a settings.json from before it gets the same
        Assert.Equal("gaming/gaming.json", settings.Gaming!.Config);
        Assert.Empty(settings.Gaming.Games!);
        var defaults = new Program.GamingSettings();     // what a "gaming" without the two timings gets
        Assert.Equal((defaults.ScanMs, defaults.EndAfterMs), (settings.Gaming.ScanMs, settings.Gaming.EndAfterMs));
        Assert.Null(settings.Weather!.Latitude);      // no location until the user gives one: nothing is sent anywhere
        Assert.Null(settings.Weather.Longitude);
        Assert.False(settings.Weather.Fahrenheit);
        Assert.False(File.Exists(path + ".new"));
    }

    [Fact]
    public void Settings_that_are_there_are_never_overwritten()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "lhmGpu": true }""");
        Program.WriteDefaultSettings(path);
        Assert.Equal("""{ "lhmGpu": true }""", File.ReadAllText(path));
    }

    [Fact]
    public void The_first_run_layout_loads_and_renders()
    {
        using var dir = new TempDirectory();
        string configs = Path.Combine(dir.Path, "configs");
        Program.WriteDefaultConfigs(configs);

        string folder = Path.Combine(configs, "default");
        var config = Config.Load(Path.Combine(folder, "default.json"));
        using var renderer = new Renderer(config, folder);     // throws if background.png is not where the layout looks
        Assert.True(renderer.Render(new Dictionary<string, double>()));
    }

    [Fact]
    public void The_restored_default_layout_loads_and_renders()
    {
        using var dir = new TempDirectory();
        string configs = Directory.CreateDirectory(Path.Combine(dir.Path, "configs")).FullName;
        string path = Program.DefaultConfig(configs);     // an empty folder: default/default.json is written back

        using var renderer = new Renderer(Config.Load(path), Path.GetDirectoryName(path)!);
        Assert.True(renderer.Render(new Dictionary<string, double>()));
    }
}
