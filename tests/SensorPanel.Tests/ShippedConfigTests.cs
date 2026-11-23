using SensorPanel.Layout;
using SensorPanel.Sensors;

namespace SensorPanel.Tests;

/// <summary>The layouts in src/SensorPanel/Configs: each must load, render, and read only keys that exist.</summary>
[Collection(RenderingCollection.Name)]
public class ShippedConfigTests
{
    private static string ConfigsFolder
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "sensor-panel.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            return Path.Combine(root.FullName, "src", "SensorPanel", "Configs");
        }
    }

    private static string[] ConfigFiles() =>
        Directory.GetFiles(ConfigsFolder, "*.json").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    public static TheoryData<string> Configs() => new(ConfigFiles());

    private static readonly HashSet<string> KnownKeys = [.. SensorHub.HardwareKeys, .. SensorHub.ComputedKeys];

    [Fact]
    public void The_folder_has_the_six_known_layouts()
    {
        // A layout added, renamed or removed fails here on purpose: the README lists them by name.
        Assert.Equal(["default.json", "gaming.json", "portrait.json", "simple-columns.json", "simple-rows.json", "slideshow.json"], ConfigFiles());
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Loads_and_has_a_name(string file)
    {
        string path = Path.Combine(ConfigsFolder, file);
        var config = Config.Load(path);
        Assert.NotEmpty(config.Widgets);
        Assert.False(string.IsNullOrWhiteSpace(config.Name), $"{file} has no \"name\", so the tray lists it by its file name");
        Assert.Equal(config.Name, Config.DisplayName(path));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Reads_only_keys_that_exist(string file)
    {
        var config = Config.Load(Path.Combine(ConfigsFolder, file));
        var unknown = config.Widgets.SelectMany(w => w.SensorKeys()).Where(k => !KnownKeys.Contains(k)).Distinct().ToList();
        Assert.True(unknown.Count == 0,
            $"{file} reads keys that are neither in SensorHub.Maps nor in SensorHub.ComputedKeys, so they would show \"--\": " +
            string.Join(", ", unknown));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Renders_with_no_sensors_and_with_every_sensor(string file)
    {
        // Also finds each layout's pictures: Renderer throws IOException when one is missing.
        string path = Path.Combine(ConfigsFolder, file);
        var config = Config.Load(path);
        using var renderer = new Renderer(config, ConfigsFolder);

        Assert.True(renderer.Render(new Dictionary<string, double>()), "the first frame is always drawn");

        var all = KnownKeys.ToDictionary(k => k, _ => 50.0);
        for (int tick = 0; tick < 3; tick++) renderer.Render(all);   // graphs need two samples to draw a line
    }
}
