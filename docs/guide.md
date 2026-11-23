# Setup and configuration

The details behind the [README](../README.md): running the app, the config format, and adding sensors and widgets.

## The tray icon

Right-click the icon for the menu:

- **The configs.** Every `.json` file in the `configs` folder beside the exe, listed by its `name` (or its file name if it has none) in alphabetical order, with a tick on the one that is running. Picking one switches the panel to it without a restart, and it is remembered (in `last-config.txt` beside the exe) for the next start. The folder is read each time the menu opens, so a file you add appears straight away. Picking the ticked config again reloads it, which is how an edit is applied.
- **Show log.** Opens `SensorPanel.log`, also beside the exe and started afresh on every run. Every message the app logs goes there. Warnings (a missing PawnIO driver, panel unavailable, a config that will not load) also pop up as a notification.
- **Exit.** Turns the panel's screen off, then quits. The next start turns it back on.

## Which config runs

On first start the exe creates the `configs` folder and writes the five shipped layouts and their pictures into it. Delete the folder to get the originals back.

At each start the app runs, in order:

1. the `--config` file, if one was given;
2. the config last picked from the menu;
3. `default-sample.json`;
4. if that has been deleted, the first config in the menu.

If the `configs` folder has no `.json` files left at all, `default-sample.json` (and `media/background.png`, which it draws, if that is missing too) is written back first. The other samples are not restored.

A config that fails to load is reported, whatever was running carries on, and the failed one is not remembered. If the config to start with will not load, the default (3 or 4 above) is tried instead; if that fails too (for example it has been broken by an edit), the panel shows nothing until you pick a config that loads.

A second copy of the app refuses to start, because only one program can hold the panel's port.

## Shutdown, sleep and monitors off

- **Exit, shutdown, sign-out.** The panel is turned off when Windows shuts down or you sign out, and on Ctrl+C when the app was started from a terminal. It cannot be when the app is killed (Task Manager, closing that terminal) or crashes: the last frame stays until the next start. The app quits as soon as Windows asks, so if you then cancel the shutdown (another program had unsaved work), start it again.
- **Sleep and hibernate.** The panel's screen is turned off, and on waking it is turned back on with the same picture. Windows gives a program about two seconds before it sleeps, so if the panel is in the middle of a full repaint at that moment the command may not get out in time and the panel stays lit for that sleep.
- **Monitors off.** When Windows turns the monitors off the panel goes dark too and nothing is polled until they come back; locking the PC alone does not do it. A layout can opt out with `"pauseWhenDisplayOff": false`.

## Getting every sensor

CPU temperature, clock speed, package power and motherboard fans are read through a kernel driver. They need **both** of these:

1. **Administrator rights.** The exe requires them: Windows shows a UAC prompt on every start, in every mode, and the app does not start if you decline. Use an elevated terminal for the command-line modes and for `dotnet run`.
2. **The PawnIO driver**, installed once: `winget install namazso.PawnIO`. LibreHardwareMonitor 0.9.5+ uses PawnIO in place of the old bundled WinRing0 driver. The app warns at startup when it is missing; those sensors then have no value (see [Widgets](#widgets)) and everything else keeps working.

Load, memory, GPU (NVIDIA), disk and network need neither.

To start it elevated at logon without a UAC prompt, create a Task Scheduler task with "Run with highest privileges" and "Run only when user is logged on" (the icon needs your desktop) that launches the exe. If you do, keep the exe and its `configs` folder in a folder only administrators can write to, such as one under `C:\Program Files`: anything able to replace a file that an elevated task runs gains administrator rights. Also untick the task's "Stop the task if it runs longer than" setting.

To run it without administrator rights and without the UAC prompt, for testing or automation, set this in the terminal before starting the exe:

```powershell
$env:__COMPAT_LAYER = 'RunAsInvoker'
```

It applies only to programs started from that terminal. The sensors above then have no value, with no warning; everything else works, and output reaches the terminal as usual.

## Command line

| | |
|---|---|
| `SensorPanel` | Start in the tray (see [Which config runs](#which-config-runs)). |
| `SensorPanel --config path.json` | Start with this layout instead, for this run only. Image paths are relative to the config file. |
| `SensorPanel --png out.png` | Draw to a PNG (rewritten whenever the picture changes) instead of the panel, for designing layouts: `SensorPanel --config my.json --png out.png`. The PNG path is relative to the current folder. It may run beside the tray app; it has its own tray icon, but writes no log file and does not remember what is picked from its menu. |
| `SensorPanel --verbose` | Print per-frame timings and bytes sent to the terminal. |
| `SensorPanel sensors` | List every hardware sensor LibreHardwareMonitor sees, and every key widgets can use. No tray icon. |
| `SensorPanel bench` | Run the transport measurements against the panel (164 s). No tray icon. Exit the tray app first: it holds the port. |

The exe is a windowed program with no console of its own. Started from a terminal:

- It prints to that terminal, but the shell does not wait for it: the prompt comes back at once and the output appears after it.
- To make PowerShell wait and show each line as it is printed, pipe the output: `.\SensorPanel.exe sensors | Out-Host`.
- Do not use `| more`: it shows nothing until the program has finished, and `bench` runs for nearly three minutes.
- Redirecting to a file (`> sensors.txt`) works too.
- Ctrl+C in that terminal stops the app the same way Exit does.

During development, from an elevated terminal at the repository root: `dotnet run --project src/SensorPanel -c Release -- --config src/SensorPanel/Configs/default-sample.json`. `sensor-panel.slnx` opens the projects in Visual Studio.

`src/SensorPanel.Screenshots` renders configs with made-up sensor values and a fixed clock, through the app's own renderer, so it needs no panel, sensors or administrator rights. `dotnet run --project src/SensorPanel.Screenshots` regenerates the README screenshots in `docs/images`; `dotnet run --project src/SensorPanel.Screenshots -- path\to\config.json out.png` renders any config.

The COM port is found automatically (USB VID 1A86 / PID 5722), so it does not matter if Windows renumbers it. If the panel is unplugged or the PC sleeps, the app keeps retrying and repaints when it comes back. Close any other program that drives the panel (such as turing-smart-screen-python) before starting this one.

## settings.json

An optional file beside the exe for choices about this PC rather than about a layout. It is read once at start (also by `--png` and `sensors`), with the same rules as a config: comments and trailing commas allowed, unknown names an error. A file that cannot be read is reported and ignored. It has one setting:

```json
{ "lhmGpu": true }
```

`lhmGpu` (default `false`) reads the GPU entirely through LibreHardwareMonitor. Normally the `gpu.*` keys of an NVIDIA card come from NVML, the monitoring library that ships with NVIDIA's driver, where a poll takes under half a millisecond. Any other GPU (AMD, Intel), or an NVIDIA card whose driver lacks NVML, is read through LibreHardwareMonitor anyway.

Use `lhmGpu` when:

- you want a GPU sensor that NVML does not give, such as one you added to `Maps` (see [Adding a key](#adding-a-key));
- you want fan RPM on a driver too old for NVML's fan call (`gpu.fan` has no value there);
- an NVML reading looks wrong on your card.

It costs: on my machine LibreHardwareMonitor's GPU poll takes 15-20 ms of CPU and ~77 ms in all, every tick, so also add `"sensorIntervals": { "gpu": 3000 }` to each layout you use.

## Config schema

JSON with comments and trailing commas allowed. Unknown property names are an error, so a typo is reported rather than ignored. There is no hot reload: after editing, pick the config again from the tray menu.

Switching configs re-applies everything in the file. If the orientation changes the whole screen is repainted (2.4 s, top to bottom); otherwise only the pixels that differ are sent. Graph history starts again.

### Top level

| Property | Default | |
|---|---|---|
| `name` | file name | Shown in the tray menu and tooltip. |
| `refreshMs` | `1000` | Sensor poll and redraw interval. At least 100. |
| `orientation` | `"landscape"` | `landscape` (480x320), `portrait` (320x480), `reverseLandscape`, `reversePortrait`. |
| `brightness` | `20` | 0-100. |
| `background` | `"#000000"` | Colour behind all widgets. |
| `sensorIntervals` | `{}` | Poll expensive hardware less often. See below. |
| `pauseWhenDisplayOff` | `true` | Turn the panel off, and stop polling, while Windows has the monitors off. |
| `widgets` | `[]` | Drawn in order; later widgets are on top. |

#### `sensorIntervals`

By default every device the config's widgets read from is polled every `refreshMs`. `sensorIntervals` slows chosen devices down: each key is a key group (the part of a sensor key before the first dot), each value is milliseconds, from 1 to 3600000.

```json
"sensorIntervals": { "gpu": 3000, "fan": 5000 }
```

- The groups are `cpu`, `gpu`, `mem`, `net` and `fan`. `"gpu"` covers every `gpu.*` key. A single key (`"gpu.temp"`) cannot be listed, because hardware is polled a whole device at a time.
- An interval can only slow a device down. Anything shorter than `refreshMs` is treated as `refreshMs`, and other values are rounded up to a whole number of refreshes: `2500` at a 1000 ms refresh means every 3000 ms. The app logs the interval it ended up with.
- Between polls a key keeps its last value. Graphs still add a point every refresh, so a graph of a slowed sensor shows flat steps.
- `disk.*` and `uptime.*` are computed, not read from hardware, so they cannot be listed: `uptime.*` is refreshed every `refreshMs`, and `disk.*` every 30 s because free space moves slowly. `mem.total` is computed from `mem.used` and `mem.free`, so it follows `"mem"`.

With the GPU read through NVML, no device on my machine is expensive enough to need this. With `"lhmGpu": true` the GPU is: polling it every 3 s instead of every 1 s cuts the app's CPU use by a third to a half. `SensorPanel sensors` shows the poll time of each device LibreHardwareMonitor reads.

### Widgets

Every widget has `type`, `x` and `y`. Coordinates are pixels from the top-left corner.

Colours are `"#RRGGBB"` or `"#AARRGGBB"` (alpha first). Translucent colours blend with whatever is underneath, including a background image. Fonts are family names of fonts installed in Windows; `weight` is 300 light, 400 regular, 600 semibold, 700 bold, where the family has that weight.

When a sensor key has no value (the sensor was not found, or needs administrator rights), text shows `--`, bars and gauges stay empty, and a graph adds no point. There is no warning.

### `panel`: rounded card

| Property | Default | |
|---|---|---|
| `w`, `h` | | Size. |
| `radius` | `0` | Corner radius. |
| `fill` | none | Fill colour. |
| `border` | none | `{ "color": "#1E2636", "width": 1 }`. Inside it, `color` defaults to grey and `width` to `1`. |
| `accent` | none | Stripe along one edge, clipped to the rounded corners: `{ "edge": "left", "color": "#00E5FF", "width": 4 }`. `edge` is `left`, `top`, `right` or `bottom`. Inside it, the defaults are `left`, white and `4`. |

### `text`: label, readout or clock

`x`,`y` is the **top of the capital letters**, at the left, centre or right of the text according to `align`. Give one source:

| Source | |
|---|---|
| `value` | Literal text. Icons work too: use `"font": "Segoe Fluent Icons"` and a glyph, written as a JSON escape such as `"\uE950"`. |
| `sensor` + `format` | One key through a [.NET format string](https://learn.microsoft.com/dotnet/standard/base-types/standard-numeric-format-strings), e.g. `"{0:F0}%"`. Default `"{0:F0}"`. |
| `sensors` + `format` | Several keys in one string: `"sensors": ["mem.used", "mem.total"], "format": "{0:F1} / {1:F0} GB"`. |
| `clock` | Current time through a [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings): `"HH:mm"`, `"ddd dd MMM yyyy"`. |

Extra sources are not reported: `clock` wins over `sensors`, `sensors` over `sensor`, and any of them over `value`. Numbers and dates are always formatted the English way (`.` decimal point, English day and month names), whatever the Windows language.

| Property | Default | |
|---|---|---|
| `font` | `"Segoe UI"` | |
| `size` | `14` | Pixels. |
| `weight` | `400` | |
| `color` | `"#FFFFFF"` | |
| `align` | `"left"` | `left`, `center`, `right`. |
| `upper` | `false` | Upper-case the result (`SUN 22 NOV`). |

### `bar`: horizontal meter

| Property | Default | |
|---|---|---|
| `sensor` | required | |
| `w`, `h` | | Size. |
| `min`, `max` | `0`, `100` | Value range. |
| `color`, `background` | white, `#262A33` | Lit and unlit colour. |
| `radius` | `0` | Corner radius (of each block when segmented). |
| `segments` | `0` | `0` = continuous. `N` = N blocks that light up whole. |
| `gap` | `2` | Space between blocks. |

### `gauge`: arc

`x`,`y` is the **centre**. Angles are degrees, 0 at 3 o'clock, increasing clockwise: `135` to `45` is a 270° dial open at the bottom; equal start and end is a full ring.

| Property | Default | |
|---|---|---|
| `sensor` | required | |
| `radius` | `20` | Outer radius. |
| `thickness` | `4` | |
| `startAngle`, `endAngle` | `135`, `45` | |
| `clockwise` | `true` | Direction from start to end. |
| `min`, `max` | `0`, `100` | |
| `color`, `background` | white, `#262A33` | Value arc and track. |
| `text` | none | Centred readout: `{ "format": "{0:F0}%", "font": "Segoe UI", "size": 12, "weight": 400, "color": "#FFFFFF" }`. Those are the defaults, except `format`, which defaults to `"{0:F0}"`. |

### `graph`: sparkline

| Property | Default | |
|---|---|---|
| `sensor` | required | |
| `w`, `h` | | Size. |
| `history` | `60` | Samples kept; one per refresh. At least 2. The newest is at the right edge. |
| `min`, `max` | `0`, `100` | Fixed vertical range. |
| `color` | white | |
| `thickness` | `1.5` | |
| `fill` | `false` | Tint the area under the curve. |

### `image`: picture or slideshow

Give exactly one of `path` and `folder`. The tray menu lists only the `.json` files directly in `configs`, so keep pictures in a subfolder; the shipped configs use `configs/media` (`"path": "media/background.png"`).

| Property | Default | |
|---|---|---|
| `path` | | One PNG or JPEG, relative to the config file. |
| `folder` | | A slideshow: every `.png`, `.jpg` and `.jpeg` in this folder (relative to the config file), in name order. |
| `w`, `h` | image size | With `path`: optional; the picture is stretched to this size. With `folder`: required; each picture is scaled to cover the box, keeping its shape, and what overflows is cropped equally from both sides. |
| `intervalSeconds` | `10` | Time each picture is shown. At least 1. |

Put a 480x320 image first in the list for a full-screen background.

Slideshow notes:

- A change costs the box's area x 2 bytes at 128,000 bytes per second, painted top to bottom: 1.2 s for the 320x240 frame in `slideshow-sample.json`, 2.4 s for the full screen. Nothing else on the panel updates during that time.
- The folder is listed when the config is loaded. After adding pictures, pick the config again from the tray menu.
- A missing folder, or one with no pictures, is a load error. A file that cannot be decoded is reported once and skipped.
- Only the current picture is kept in memory, already scaled, so large photos are fine.

### Designing for this panel

The panel accepts 128,000 bytes per second, about two fifths of the screen, no matter how it is driven. The first frame takes 2.4 s; after that only changed pixels are sent, so a layout stays responsive as long as the area that changes each second is modest. Big elements that change every tick (a seconds counter in huge type, a full-width graph) are what to avoid. `--verbose` shows the bytes sent per frame. Why it works this way: [Optimizations](optimizations.md#sending-pixels-to-the-panel).

## Sensor keys

Run `SensorPanel sensors` for the live list on your machine. Built in:

| Key | Unit | | Key | Unit |
|---|---|---|---|---|
| `cpu.load` | % | | `mem.used`, `mem.free`, `mem.total` | GB |
| `cpu.temp` * | °C | | `mem.load` | % |
| `cpu.freq` * | GHz | | `disk.used`, `disk.free`, `disk.total` | GB (drive C:) |
| `cpu.power` * | W | | `disk.load` | % |
| `gpu.load` | % | | `net.up`, `net.down` | MB/s (adapter "Ethernet") |
| `gpu.temp` | °C (whole degrees from NVML) | | `uptime.days`, `uptime.hours`, `uptime.minutes` | |
| `gpu.power` | W | | `gpu.mem.used`, `gpu.mem.total` | GB |
| `gpu.fan` | RPM | | `fan.cpu` *, `fan.sys1` *, `fan.sys2` * | RPM (motherboard headers) |

\* needs administrator + PawnIO.

### Adding a key

Everything lives in [Sensors/SensorHub.cs](../src/SensorPanel/Sensors/SensorHub.cs). There is no expression language in the config on purpose: a new value is a line or two of C# and a rebuild.

**A hardware sensor.** Run `SensorPanel sensors`, find the line you want:

```
GpuNvidia: NVIDIA GeForce RTX 5070 Ti   (poll 74.0 ms)
    Temperature  GPU Memory Junction              44
```

and add one line to the `Maps` table: key, where to look, sensor type, exact sensor name.

```csharp
new("gpu.vram.temp", Gpu, SensorType.Temperature, "GPU Memory Junction"),
```

On an NVIDIA card a GPU line in `Maps` only reads with `"lhmGpu": true` in [settings.json](#settingsjson); otherwise LibreHardwareMonitor does not open the GPU and the key has no value. (`sensors` lists the GPU's sensors either way.) The six built-in `gpu.*` keys come from [Sensors/Nvml.cs](../src/SensorPanel/Sensors/Nvml.cs) instead.

Name the key so that everything sharing its first segment comes from the same device (`gpu.*` from the GPU, `fan.*` from the board's fan chip): `sensorIntervals` is set per key group, and two key groups read from one device with different intervals is a load error.

Optional extras are a scale factor and an exact hardware name (`Device: "Ethernet"`) when several devices of the same kind exist. Listing a key twice gives fallbacks: the first sensor that exists wins.

**A computed value.** Add it in `AddDerived()`, which runs after every poll:

```csharp
if (Snapshot.TryGetValue("gpu.mem.used", out var vramUsed) && Snapshot.TryGetValue("gpu.mem.total", out var vramTotal))
    Snapshot["gpu.mem.load"] = 100 * vramUsed / vramTotal;
```

Then use `"sensor": "gpu.mem.load"` in the config. Only the keys that widgets read are polled, so also list the keys it is computed from in `DerivedFrom`, just above `AddDerived()`:

```csharp
["gpu.mem.load"] = ["gpu.mem.used", "gpu.mem.total"],
```

### Adding a widget type

Subclass `Widget` in [Layout/Widgets.cs](../src/SensorPanel/Layout/Widgets.cs) (public properties become the JSON schema; implement `Sample` and `Draw`) and register it with one `[JsonDerivedType(typeof(MyWidget), "my")]` line in [Layout/Widget.cs](../src/SensorPanel/Layout/Widget.cs). If it reads sensor keys, add it to `SensorKeys` there too: only the keys listed there are polled. If it keeps Skia objects between frames, override `Dispose` to free them.
