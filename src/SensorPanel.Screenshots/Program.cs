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
Render(Path.Combine(configs, "default", "default.json"), Path.Combine(images, "default.png"));
Render(Path.Combine(configs, "gaming", "gaming.json"), Path.Combine(images, "gaming.png"), gaming: true);
return 0;

/// <param name="gaming">Overlay the values of a game in progress: see <see cref="Game"/>.</param>
static void Render(string configPath, string outPath, bool gaming = false)
{
    configPath = Path.GetFullPath(configPath);
    using var renderer = new Renderer(Config.Load(configPath), Path.GetDirectoryName(configPath)!);

    // Fixed seeds, so the same config gives the same picture. The game has its own, so that
    // overlaying it leaves the other values, and the other screenshots, as they were.
    var rng = new Random(7);
    var game = new GameState(new Random(11));
    var disk = new Random(13);  // its own, so adding the disk values left the other walks as they were
    var s = new Dictionary<string, double>();
    var wall = new SensorPanel.Sensors.WallPowerSettings();     // the shipped estimate
    double cpu = 18, gpu = 35, net = 8, read = 20, write = 6;

    // Enough ticks to fill the longest graph's history.
    for (int tick = 0; tick < 100; tick++)
    {
        // Smooth random walks for the graphed values, with a burst of GPU activity mid-way.
        cpu = Math.Clamp(cpu + rng.NextDouble() * 8 - 4, 6, 45);
        gpu = Math.Clamp(gpu + rng.NextDouble() * 10 - 5 + (tick is > 40 and < 70 ? 3 : -1), 8, 92);
        net = Math.Clamp(net + rng.NextDouble() * 6 - 3, 0.5, 26);
        // Disk activity: a trickle, with a burst of reading and writing near the end.
        bool burst = tick is > 70 and < 82;
        read = Math.Clamp(read + disk.NextDouble() * 40 - 20 + (burst ? 30 : -4), 0, 190);
        write = Math.Clamp(write + disk.NextDouble() * 16 - 8 + (burst ? 6 : -1.5), 0, 80);

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
        s["disk.read"] = read;
        s["disk.write"] = write;
        s["net.down"] = net;
        s["net.up"] = net / 4;
        s["power.total"] = s["cpu.power"] + s["gpu.power"];
        s["power.wall"] = wall.At(s["power.total"]);
        s["weather.temp"] = 12;
        s["weather.high"] = 15;
        s["weather.low"] = 7;
        s["weather.precip"] = 20;
        s["weather.code"] = 2;      // partly cloudy
        s["weather.day"] = 1;
        s["uptime.days"] = 2;
        s["uptime.hours"] = 7;
        s["uptime.minutes"] = 41;
        if (gaming)
        {
            Game(game, tick, s);
            s["power.total"] = s["cpu.power"] + s["gpu.power"];     // again: the game replaced both
            s["power.wall"] = wall.At(s["power.total"]);
        }

        renderer.Render(s);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    new PngPresenter(Path.GetFullPath(outPath)).Present(renderer.Frame);
    Console.WriteLine($"Wrote {outPath}");
}

// A GPU-bound game near a 120 fps cap: the GPU in the 80s and 90s, a few seconds where it hits 99%
// and the frame rate dips, one stuttered frame, and the CPU, power and memory to go with it.
static void Game(GameState game, int tick, Dictionary<string, double> s)
{
    bool dip = tick is >= 52 and < 58;
    game.Cpu = Math.Clamp(game.Cpu + game.Rng.NextDouble() * 8 - 4, 24, 58);
    game.Gpu = dip ? 99 : Math.Clamp(game.Gpu + game.Rng.NextDouble() * 6 - 3, 82, 98);

    s["fps"] = dip ? 74 + game.Rng.NextDouble() * 8 : tick == 80 ? 41 : 116 + game.Rng.NextDouble() * 4;
    s["cpu.load"] = game.Cpu;
    s["cpu.temp"] = 58 + game.Cpu * 0.35;
    s["cpu.power"] = 62 + game.Cpu * 1.1;
    s["gpu.load"] = game.Gpu;
    s["gpu.temp"] = 52 + game.Gpu * 0.17;
    s["gpu.power"] = 120 + game.Gpu * 1.6;
    s["gpu.mem.used"] = 9.4 + game.Gpu / 100;
    s["mem.used"] = 19.6;
    s["mem.free"] = 11.5;
    s["mem.total"] = 31.1;
    s["mem.load"] = 63;
}

sealed class GameState(Random rng)
{
    public Random Rng { get; } = rng;
    public double Cpu { get; set; } = 38;
    public double Gpu { get; set; } = 93;
}
