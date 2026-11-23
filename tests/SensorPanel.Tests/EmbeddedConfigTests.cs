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
    public void The_exe_carries_the_default_layout_and_its_background()
    {
        // The csproj's LogicalNames; Program writes out every resource under "configs/".
        var names = typeof(Config).Assembly.GetManifestResourceNames().Order().ToArray();
        Assert.Equal(["configs/default.json", "configs/media/background.png"], names);
    }

    [Fact]
    public void The_first_run_layout_loads_and_renders()
    {
        using var dir = new TempDirectory();
        string configs = Path.Combine(dir.Path, "configs");
        Program.WriteDefaultConfigs(configs);

        var config = Config.Load(Path.Combine(configs, "default.json"));
        using var renderer = new Renderer(config, configs);     // throws if background.png is not where the layout looks
        Assert.True(renderer.Render(new Dictionary<string, double>()));
    }

    [Fact]
    public void The_restored_default_layout_loads_and_renders()
    {
        using var dir = new TempDirectory();
        string configs = Directory.CreateDirectory(Path.Combine(dir.Path, "configs")).FullName;
        string path = Program.DefaultConfig(configs);     // an empty folder: default.json is written back

        using var renderer = new Renderer(Config.Load(path), configs);
        Assert.True(renderer.Render(new Dictionary<string, double>()));
    }
}
