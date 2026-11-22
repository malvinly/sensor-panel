using System.Diagnostics;
using System.IO.Ports;
using SkiaSharp;
using SensorPanel.Display;

namespace SensorPanel;

/// <summary>
/// `SensorPanel bench` — transport measurements against the real panel.
/// This is the harness behind docs/transport-comparison.md; it is not used by the dashboard.
/// </summary>
public static class Bench
{
    private const int W = 480, H = 320;

    public static int Run()
    {
        var portName = PortFinder.Find();
        if (portName is null) { Console.Error.WriteLine("Panel not found."); return 1; }
        Console.WriteLine($"Panel on {portName}");

        byte[] a = Pattern("A", new SKColor(0x10, 0x30, 0x60)), b = Pattern("B", new SKColor(0x60, 0x20, 0x10));
        var hdr = new byte[Protocol.OrientationSize];
        var proc = Process.GetCurrentProcess();

        // ---- 1. SerialPort, RTS/CTS, default buffers: chunk size sweep ----
        using (var sp = Open(portName, Handshake.RequestToSend, null))
        {
            Protocol.WriteOrientation(hdr, Orientation.Landscape, W, H);
            sp.Write(hdr, 0, Protocol.OrientationSize);
            Protocol.WriteBrightness(hdr, 20);
            sp.Write(hdr, 0, Protocol.HeaderSize);

            Console.WriteLine("\n[1] Full frame 480x320 (307,200 B), SerialPort RTS/CTS, default WriteBufferSize");
            foreach (int chunk in new[] { 3840, 4096, 16384, 65536, 307200 })
                Report($"chunk {chunk,6}", 3, i => Frame(sp.Write, i % 2 == 0 ? a : b, 0, 0, W, H, chunk));

            Console.WriteLine("\n[2] Partial rectangles, one write each (mean of 20)");
            foreach (var (w, h) in new[] { (1, 1), (8, 8), (40, 16), (80, 24), (150, 24), (232, 64), (480, 24), (480, 80), (240, 320) })
            {
                int n = 0;
                Report($"{w,3}x{h,-3} {w * h * 2,6} B", 20, i =>
                    Frame(sp.Write, (n++ % 2 == 0) ? a : b, (W - w) / 2 / 2 * 2, (H - h) / 2, w, h, int.MaxValue));
            }

            Console.WriteLine("\n[3] Burst: 20 different 80x24 rects back to back");
            Report("20 x 80x24", 3, i =>
            {
                for (int k = 0; k < 20; k++)
                    Frame(sp.Write, i % 2 == 0 ? a : b, (k % 5) * 96, (k / 5) * 80, 80, 24, int.MaxValue);
            });

            Console.WriteLine("\n[4] Async BaseStream.WriteAsync, whole frame");
            Report("WriteAsync", 3, i =>
                Frame((buf, o, c) => sp.BaseStream.WriteAsync(buf, o, c).GetAwaiter().GetResult(), i % 2 == 0 ? a : b, 0, 0, W, H, int.MaxValue));

            Console.WriteLine("\n[5] Sustained: 10 full frames back to back, CPU cost");
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) Frame(sp.Write, i % 2 == 0 ? a : b, 0, 0, W, H, 65536);
            sw.Stop(); proc.Refresh();
            var cpu = proc.TotalProcessorTime - cpu0;
            Console.WriteLine($"  {sw.ElapsedMilliseconds / 10.0:F0} ms/frame, {10000.0 / sw.ElapsedMilliseconds:F2} fps, " +
                              $"{W * H * 2 * 10 / 1024.0 / sw.Elapsed.TotalSeconds:F0} KB/s, " +
                              $"process CPU {cpu.TotalMilliseconds:F0} ms = {cpu.TotalMilliseconds / sw.ElapsedMilliseconds * 100 / Environment.ProcessorCount:F2}% of machine");
        }

        // ---- 2. Buffer size and flow control ----
        Console.WriteLine("\n[6] WriteBufferSize = 1 MB, RTS/CTS");
        using (var sp = Open(portName, Handshake.RequestToSend, 1 << 20))
            foreach (int chunk in new[] { 4096, 307200 })
                Report($"chunk {chunk,6}", 3, i => Frame(sp.Write, i % 2 == 0 ? a : b, 0, 0, W, H, chunk));

        Console.WriteLine("\n[7] No flow control (Handshake.None)");
        using (var sp = Open(portName, Handshake.None, null))
            foreach (int chunk in new[] { 4096, 307200 })
                Report($"chunk {chunk,6}", 3, i => Frame(sp.Write, i % 2 == 0 ? a : b, 0, 0, W, H, chunk));

        Console.WriteLine("\n[8] Baud 9600 (does the virtual port care?)");
        using (var sp = Open(portName, Handshake.RequestToSend, null, 9600))
            Report("chunk 307200", 2, i => Frame(sp.Write, i % 2 == 0 ? a : b, 0, 0, W, H, int.MaxValue));

        // ---- 3. Bypass System.IO.Ports entirely: plain file handle on the device ----
        Console.WriteLine("\n[9] Raw FileStream on \\\\.\\" + portName + " (no SerialPort class)");
        try
        {
            using var fs = new FileStream(@"\\.\" + portName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 0);
            foreach (int chunk in new[] { 4096, 307200 })
                Report($"chunk {chunk,6}", 3, i => Frame(fs.Write, i % 2 == 0 ? a : b, 0, 0, W, H, chunk));
            Report(" 80x24    3840 B", 20, i => Frame(fs.Write, i % 2 == 0 ? a : b, 200, 148, 80, 24, int.MaxValue));
        }
        catch (Exception e) { Console.WriteLine("  failed: " + e.Message); }

        // ---- 4. Our own pixel pipeline ----
        Console.WriteLine("\n[10] Pixel pipeline (no I/O), mean of 200");
        using var rgba = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var rgb565 = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgb565, SKAlphaType.Opaque));
        var managed = new byte[W * H * 2];
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        Report("RGBA8888 -> RGB565, managed loop", 200, _ => ConvertManaged(rgba.GetPixelSpan(), managed));
        Report("RGBA8888 -> RGB565, Skia ReadPixels", 200, _ => { using var pm = rgba.PeekPixels(); pm.ReadPixels(rgb565.Info, rgb565.GetPixels(), rgb565.RowBytes); });
        Report("copy 307,200 B (render straight to 565)", 200, _ => rgb565.GetPixelSpan().CopyTo(managed));
        Report("diff two frames (SequenceEqual)", 200, _ => managed.AsSpan().SequenceEqual(rgb565.GetPixelSpan()));
        Console.WriteLine($"  allocated during [10]: {GC.GetAllocatedBytesForCurrentThread() - alloc0} B");
        return 0;
    }

    private static SerialPort Open(string name, Handshake handshake, int? writeBuffer, int baud = 115200)
    {
        var sp = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        { Handshake = handshake, WriteTimeout = 10000, ReadTimeout = 1000 };
        if (writeBuffer is int size) sp.WriteBufferSize = size;
        sp.Open();
        return sp;
    }

    private delegate void Writer(byte[] buffer, int offset, int count);
    private static readonly byte[] Header = new byte[Protocol.HeaderSize];
    private static readonly byte[] Scratch = new byte[W * H * 2];

    /// <summary>Sends the (x,y,w,h) region of a full-frame RGB565 buffer.</summary>
    private static void Frame(Writer write, byte[] frame, int x, int y, int w, int h, int chunk)
    {
        for (int row = 0; row < h; row++)
            frame.AsSpan(((y + row) * W + x) * 2, w * 2).CopyTo(Scratch.AsSpan(row * w * 2));
        Protocol.WriteHeader(Header, Protocol.DisplayBitmap, x, y, x + w - 1, y + h - 1);
        write(Header, 0, Header.Length);
        int total = w * h * 2;
        for (int o = 0; o < total; o += chunk)
            write(Scratch, o, Math.Min(chunk, total - o));
    }

    private static void Report(string label, int reps, Action<int> action)
    {
        var times = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            long t = Stopwatch.GetTimestamp();
            action(i);
            times[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Console.WriteLine($"  {label,-40} mean {times.Average(),9:F3} ms   min {times.Min(),9:F3}   max {times.Max(),9:F3}");
    }

    private static byte[] Pattern(string label, SKColor background)
    {
        using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgb565, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(background);
        using var paint = new SKPaint { IsAntialias = true };
        SKColor[] bars = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Cyan, SKColors.Magenta, SKColors.White, SKColors.Gray];
        for (int i = 0; i < bars.Length; i++)
        {
            paint.Color = bars[i];
            canvas.DrawRect(i * 60, 0, 60, 60, paint);
        }
        paint.Color = SKColors.White;
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 160);
        canvas.DrawText("BENCH " + label, 240, 250, SKTextAlign.Center, new SKFont(font.Typeface, 60), paint);
        canvas.Flush();
        return bmp.GetPixelSpan().ToArray();
    }

    private static void ConvertManaged(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 2)
        {
            int v = ((rgba[i] & 0xF8) << 8) | ((rgba[i + 1] & 0xFC) << 3) | (rgba[i + 2] >> 3);
            dst[o] = (byte)v;
            dst[o + 1] = (byte)(v >> 8);
        }
    }
}
