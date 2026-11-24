# Optimizations

A sensor panel runs all day, so it should cost next to nothing. This page explains the main decisions that keep it that way. The figures were measured on my PC (see the [README](../README.md)); yours will differ.

## Where it ended up

| | First version | Now |
|---|---|---|
| Private memory | 61 MB, climbing | 37.5 MB, flat |
| Working set | 120 MB | 89 MB |
| CPU | ~23 ms per second | ~7.5 ms per second |
| Exe size | 183 MB | 129 MB |

Measured without administrator rights, drawing to a PNG instead of the panel, over 5 minutes.

## Sending pixels to the panel

The panel is the bottleneck. It accepts 128,000 bytes per second however it is driven: bigger or smaller writes, bigger buffers, async writes, bypassing .NET's serial port class, a different baud rate all gave the same 2.4 s for a full 480x320 frame. The limit is in the panel's USB firmware, so the only lever is sending fewer bytes.

So the app redraws the whole layout every tick, straight into the panel's 16-bit pixel format, compares it with the last frame sent, and sends only the 8x8 tiles that changed. Redrawing everything takes under a millisecond and comparing two frames a few microseconds, so tracking which widget changed is not worth the complexity. On the default dashboard an update is 3.5-11 KB, which takes 31-97 ms instead of 2.4 s. A tick where nothing changed sends nothing.

Not done: talking to the panel over raw USB (WinUSB). It means replacing Windows' driver, and it is the same endpoint with the same limit, so no gain is expected.

## Reading sensors

- **The NVIDIA GPU is read through NVML**, the monitoring library in NVIDIA's driver. Through LibreHardwareMonitor a GPU poll took 77 ms, mostly spent sampling PCIe traffic that no layout shows; NVML gives the same readings in 0.4 ms. The tradeoffs: only the first NVIDIA card, and temperature in whole degrees. `"lhmGpu": true` in [settings.json](guide.md#settingsjson) switches back.
- **Only what is on screen is polled.** The motherboard's fan chip, for example, is left alone unless the layout shows a fan.
- **LibreHardwareMonitor's sensor history is off.** By default it keeps a day of readings for every sensor, which nothing here uses and which grows all day.
- **Disk space is read every 30 s**, not every tick: it changes slowly.
- **Disk activity comes straight from Windows.** `disk.read` and `disk.write` are one `IOCTL_DISK_PERFORMANCE` call on drive C: per tick, about 8 µs, with no memory and no elevation. LibreHardwareMonitor's storage support would open every drive, read their SMART data, need administrator rights, and took 134 ms just to open.
- **The weather has no timer.** The panel's own tick starts a fetch when one is due, so nothing runs while the monitors are off, and nothing at all while the layout shows no weather. A fetch is about 600 bytes, every 15 minutes.

## Memory

- **The garbage collector starts small.** .NET sizes its first-generation budget from the CPU's cache, which on this CPU is about 48 MB, so memory climbed for half an hour before the first collection. Capped at 2 MB, memory stays flat for a sub-millisecond collection about once a minute.
- **Tiered compilation is off.** Each method is compiled once, fully optimized: about 2 MB less, and no swings as methods are recompiled.
- **Drawing objects are reused.** Fonts and paints are made once per widget instead of every frame. This halved allocations per frame, though drawing got only 4% faster: the background image dominates.
- **Decoded photos are freed.** After a slide is scaled to its frame, Skia's copy of the full-size photo is dropped. With large photos that is 46 MB instead of 138 MB.

## The tray menu

The menu is WinForms rather than WPF. WPF's first right-click loaded its rendering stack, 57 MB and 8 threads that stayed for the rest of the run; WinForms adds about 2 MB, and the exe shrank from 183 to 129 MB.

## Pausing

When Windows turns the monitors off, the panel goes dark and nothing is polled until they come back.

## Looked at, not done

- **Precompiling (ReadyToRun):** more memory, not less, in a self-contained single-file exe.
- **Native AOT:** it works, and would save about 15 MB of memory with no change in CPU use, but only by running Windows Forms under Native AOT, which Microsoft does not support. Not worth that risk for now.
- **Pre-rendering widgets that never change:** about 0.1% of one core saved, for code whose failure mode is a wrong picture.
- **A bare Win32 tray menu:** a few MB less than WinForms, for about 150 lines of interop.
- **Avoiding the CPU poll's per-core wake-ups:** the whole app adds only 4-5% to the CPU's idle wake-ups; not worth AMD-only special code.

## Checking it yourself

- `--verbose` prints the time and bytes of every frame.
- `SensorPanel sensors` shows how long each device takes to poll.
- `SensorPanel bench` reruns the panel throughput measurements (164 s).

See the [guide](guide.md#command-line) for how to run them.
