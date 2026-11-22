# Transport and refresh strategy: what was measured, what was chosen

Measured on 2026-11-22 against the real panel (Turing Smart Screen 3.5" rev A, `USB\VID_1A86&PID_5722\USB35INCHIPSV2`, Microsoft `usbser.sys`, COM3), .NET 10.0.404, Ryzen 7 9850X3D (16 logical cores). Reproduce with `SensorPanel bench` (source: [Bench.cs](../src/SensorPanel/Bench.cs)). Times are wall-clock around the blocking writes.

## Result in one paragraph

**The panel accepts exactly 128 bytes per millisecond and nothing on the PC side changes that.** A full 480x320 frame (307,200 B) takes 2,400 ms through every host-side variant tried, to within 1 ms. The host spends ~0 ms of CPU doing it. So the transport is not worth optimising; the only lever is *sending fewer bytes*. The app therefore renders the whole frame offscreen every tick, diffs it against the last frame sent, and transmits only the 8x8 tiles that changed. On the default dashboard that is 3.5-11 KB per second instead of 307 KB: **31-97 ms per update instead of 2,400 ms**, a ~30x improvement with no driver change.

## 1. Full frame, 307,200 bytes: host-side tuning

| Variant | Mean (ms) | Min | Max |
|---|---:|---:|---:|
| `SerialPort`, RTS/CTS, chunk 3,840 (the Python projects: width x 8) | 2400.6 | 2400.4 | 2400.7 |
| chunk 4,096 (other reference) | 2400.5 | 2400.4 | 2400.6 |
| chunk 16,384 | 2400.0 | 2400.0 | 2400.0 |
| chunk 65,536 | 2400.0 | 2399.9 | 2400.0 |
| chunk 307,200 (one write) | 2400.1 | 2400.0 | 2400.2 |
| `WriteBufferSize` = 1 MB, chunk 4,096 | 2400.4 | 2400.3 | 2400.6 |
| `WriteBufferSize` = 1 MB, one write | 2400.5 | 2400.5 | 2400.6 |
| `Handshake.None` (no RTS/CTS), chunk 4,096 | 2400.4 | 2400.2 | 2400.6 |
| `Handshake.None`, one write | 2400.5 | 2400.4 | 2400.6 |
| 9,600 baud instead of 115,200 | 2400.6 | 2400.5 | 2400.8 |
| `BaseStream.WriteAsync` | 2401.2 | 2400.7 | 2402.0 |
| Raw `FileStream` on `\\.\COM3` (no `System.IO.Ports`) | 2400.3 | 2400.0 | 2400.6 |
| turing-smart-screen-python via pyserial, for reference | ~2400 | | |

Sustained, 10 frames back to back: 2,400 ms/frame, 0.42 fps, 125 KiB/s (= 128,000 B/s), **process CPU time 0 ms** over the 24 seconds.

What this says about the four suspected causes:

- **pyserial / worker-queue threading: not guilty.** C# with no queue hits the identical number.
- **Chunk size, write buffer, sync vs async: irrelevant.** All within 1 ms of each other.
- **RTS/CTS, baud rate: irrelevant.** It is a virtual COM port; line settings are ignored.
- **The device: guilty.** 128 B/ms is exactly two 64-byte packets per 1 ms USB frame, which is the signature of a full-speed USB bulk endpoint whose firmware takes two packets per frame and NAKs the rest. (That is an inference from the timing; I did not dump the USB descriptors.)

## 2. Partial rectangles

One `DISPLAY_BITMAP` header plus pixels, mean of 20:

| Rectangle | Bytes | Mean (ms) | Predicted at 128 B/ms |
|---|---:|---:|---:|
| 1x1 | 2 | 0.9 | 0.0 |
| 8x8 | 128 | 1.4 | 1.0 |
| 40x16 | 1,280 | 10.5 | 10.0 |
| 80x24 | 3,840 | 30.5 | 30.0 |
| 150x24 | 7,200 | 57.0 | 56.3 |
| 232x64 | 29,696 | 232.5 | 232.0 |
| 480x24 | 23,040 | 180.5 | 180.0 |
| 480x80 | 76,800 | 600.5 | 600.0 |
| 240x320 | 153,600 | 1200.5 | 1200.0 |
| 20 different 80x24 rects in a burst | 76,800 | 609.7 | 600.0 |

Cost is linear in bytes with a fixed overhead of roughly 0.5 ms per rectangle (the header is its own write and waits for a USB frame). Rectangle shape and position do not matter. An 80x24 readout costs 30 ms. That makes dirty-rect updates the whole game, and it sets the tile size: an 8x8 tile is 128 B = 1 ms, about the same as one rectangle's overhead, so finer tiles would not pay for themselves.

## 3. Our own pixel pipeline

No I/O, 480x320, mean of 200 runs:

| Step | Time |
|---|---:|
| RGBA8888 -> RGB565, managed loop | 0.136 ms |
| RGBA8888 -> RGB565, Skia `ReadPixels` | 0.102 ms |
| Render straight into an RGB565 Skia bitmap, then copy | 0.003 ms |
| Diff two frames (`SequenceEqual`) | 0.007 ms |
| Render the default dashboard (52 widgets) | 1-2 ms (13 ms first frame) |

Rendering is 1,000x cheaper than transmission. The app draws directly into an `SKColorType.Rgb565` bitmap, so there is no conversion step and no per-frame buffer; the bitmap's memory *is* the wire format. Because a full re-render costs ~1 ms, there is no need for widgets to track their own damage: redraw everything, let the presenter diff.

## 4. The implemented path, measured on the default dashboard

`SensorPanel --verbose`, 1 s refresh, 45 s run:

| | First frame | Steady state |
|---|---:|---:|
| Bytes sent | 307,200 | 3,500 - 11,400 |
| Rectangles | 1 | 8 - 19 |
| Present time | 2,461 ms | 31 - 97 ms |
| Render time | 13 ms | 1 - 2 ms |

CPU while running: **~30 ms of CPU time per second = about 3% of one core, 0.19% of the whole machine** as Task Manager counts it. Memory: 43 MB private. Where the CPU goes:

| Component | CPU per 1 s tick |
|---|---:|
| Serial transmission | ~0 ms |
| Render + diff | ~1-2 ms |
| LibreHardwareMonitor, NVIDIA GPU poll | ~16-28 ms (74 ms wall) |
| LibreHardwareMonitor, CPU / memory / network | < 0.5 ms |

The display side is as cheap as it can get. What remained was LibreHardwareMonitor's NVIDIA update, and the fix was on the sensor side, not the display side.

**Since these figures were taken**, an NVIDIA GPU is read through NVML (~0.4 ms of CPU per poll instead of 15-20 ms), which with other trimming brought an un-elevated `--png` run to ~7.5 ms of CPU per second and 37.5 MB private. The `sensorIntervals` setting, which polls the GPU less often, now only matters with `"lhmGpu": true` ([settings.json](guide.md#settingsjson)).

## 5. Direct USB (WinUSB / LibUsbDotNet): not measured, not recommended

I did not test this. `usbser.sys` owns the device, so it needs the driver replaced with Zadig, which removes COM3 and breaks every other program that uses the panel until it is switched back, and the numbers above say it is not worth it:

- **What it would buy:** it talks to the same bulk OUT endpoint, and the 128 B/ms ceiling is on the device's side of that endpoint. Every host-side variation above, including bypassing `System.IO.Ports` entirely, lands within 0.05% of the same figure with zero CPU, which is what a pass-through driver in front of a slow device looks like. Expected gain on throughput: none. The most it could plausibly recover is part of the ~0.5 ms per-rectangle overhead: under 10% of a typical 80 ms update.
- **What it would cost:** a Zadig driver swap on every machine, losing the COM port for everything else, a LibUsbDotNet dependency, and hand-written device discovery and reconnect in place of `SerialPort`.
- **Caveat:** this is a prediction from the measurements, not a measurement. If you ever want it settled, the swap is reversible (Device Manager -> Uninstall device, delete driver, replug) and `IPresenter` means a `WinUsbPresenter` would be one new file.

A 0-10% speedup does not justify a driver swap. The diff approach gave 30x for free.

## 6. Things considered and left out

- **Header and pixels in one write** (or several rectangles per write): tested afterwards while watching the panel, and it does not work. Merged rectangles came out jumbled, so each header stays its own write.
- **Finer or smarter rectangle merging.** Runs of dirty tiles are merged horizontally, and vertically when consecutive rows have the same extent. A better packer would save a few rectangles per frame, i.e. a few milliseconds.
- **The panel's other commands.** Rev A has no fill-rect, scroll or compressed-bitmap command (`DISPLAY_PIXELS` exists only on the newer generation), so there is no protocol shortcut to sending pixels.

## Recommendation (implemented)

1. Plain `System.IO.Ports.SerialPort`, default buffers, RTS/CTS as in the Python projects, one blocking write per rectangle, on the loop's own thread. No queue. The loop runs on one worker thread so the tray menu stays responsive; it is the only thread that writes to the port.
2. Render the full frame into an RGB565 bitmap every tick; skip the tick entirely if no widget's value changed, unless the panel's contents are unknown (never reached, or replugged), when the unchanged frame is sent too.
3. [SerialPresenter](../src/SensorPanel/Display/SerialPresenter.cs) diffs against the last frame sent on an 8x8 tile grid and sends only dirty rectangles.
4. Design layouts with the budget in mind: **128,000 bytes = 64,000 pixels per second**, about two fifths of the screen. A widget that changes every tick costs its area x 2 bytes. A digit in the 74 px clock is roughly 40x56 px, so about 35 ms each time it changes; that is why the default layout shows minutes, not seconds.
