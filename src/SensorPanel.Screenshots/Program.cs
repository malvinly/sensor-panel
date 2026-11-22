using SensorPanel.Display;
using SensorPanel.Layout;

// Renders configs to PNG with made-up sensor values, through the app's own renderer.
//   (no arguments)            regenerate the README screenshots in docs/images
//   <config.json> <out.png>   render one config
// Clocks show a fixed time, so the same config always gives the same picture.

TextWidget.Now = () => new DateTime(2026, 11, 22, 10, 21, 0);

if (args.Length == 2)
{
    Render(args[0], args[1]);
    return 0;
}
if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: SensorPanel.Screenshots [<config.json> <out.png>]");
    return 1;
}

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "sensor-panel.slnx"))) root = root.Parent;
if (root is null)
{
    Console.Error.WriteLine("Cannot find the repository root (sensor-panel.slnx).");
    return 1;
}
var configs = Path.Combine(root.FullName, "src", "SensorPanel", "Configs");
var images = Path.Combine(root.FullName, "docs", "images");
Render(Path.Combine(configs, "default-sample.json"), Path.Combine(images, "default-sample.png"));
Render(Path.Combine(configs, "simple-columns.json"), Path.Combine(images, "simple-columns.png"));
return 0;

static void Render(string configPath, string outPath)
{
    configPath = Path.GetFullPath(configPath);
    using var renderer = new Renderer(Config.Load(configPath), Path.GetDirectoryName(configPath)!);

    // A fixed seed, so the same config gives the same picture.
    var rng = new Random(7);
    var s = new Dictionary<string, double>();
    double cpu = 18, gpu = 35, net = 8;

    // Enough ticks to fill the longest graph's history.
    for (int tick = 0; tick < 100; tick++)
    {
        // Smooth random walks for the graphed values, with a burst of GPU activity mid-way.
        cpu = Math.Clamp(cpu + rng.NextDouble() * 8 - 4, 6, 45);
        gpu = Math.Clamp(gpu + rng.NextDouble() * 10 - 5 + (tick is > 40 and < 70 ? 3 : -1), 8, 92);
        net = Math.Clamp(net + rng.NextDouble() * 6 - 3, 0.5, 26);

        s["cpu.load"] = cpu;
        s["cpu.temp"] = 48 + cpu * 0.4;
        s["cpu.freq"] = 4.62 + cpu / 200;
        s["cpu.power"] = 42 + cpu * 1.5;
        s["gpu.load"] = gpu;
        s["gpu.temp"] = 41 + gpu * 0.3;
        s["gpu.power"] = 38 + gpu * 2.4;
        s["gpu.fan"] = 900 + gpu * 12;
        s["gpu.mem.used"] = 5.6 + gpu / 40;
        s["gpu.mem.total"] = 16;
        s["mem.used"] = 14.6;
        s["mem.free"] = 16.5;
        s["mem.total"] = 31.1;
        s["mem.load"] = 47;
        s["disk.used"] = 658;
        s["disk.free"] = 1204;
        s["disk.total"] = 1862;
        s["disk.load"] = 35;
        s["net.down"] = net;
        s["net.up"] = net / 7;
        s["uptime.days"] = 2;
        s["uptime.hours"] = 7;
        s["uptime.minutes"] = 41;

        renderer.Render(s);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    new PngPresenter(Path.GetFullPath(outPath)).Present(renderer.Frame);
    Console.WriteLine($"Wrote {outPath}");
}
