using System.IO.Ports;
using SkiaSharp;

namespace SensorPanel.Display;

/// <summary>
/// Sends frames to the panel over its virtual COM port, transmitting only what changed.
///
/// The panel accepts exactly 128 bytes per millisecond no matter how it is fed (see
/// docs/transport-comparison.md), so the only lever is sending fewer bytes. Each frame is
/// diffed against the last one sent on an 8x8 tile grid and only dirty tiles go out, merged
/// into rectangles.
/// </summary>
public sealed class SerialPresenter : IPresenter
{
    private const int Tile = 8;

    private SerialPort? _port;
    private Orientation _orientation;
    private int _brightness;
    private bool _configured;               // orientation and brightness have been sent to the open port
    private bool _off;                      // Blank() turned the screen off; the next Present turns it on
    private byte[]? _shown;                 // what the panel currently displays; null = unknown
    private byte[] _send = [];
    private readonly byte[] _header = new byte[Protocol.OrientationSize];
    private readonly List<SKRectI> _rects = [];
    private readonly List<SKRectI> _open = [];
    private string? _lastError;

    /// <summary>Pixel bytes sent by the most recent <see cref="Present"/> (for --verbose).</summary>
    public int LastBytes { get; private set; }
    public int LastRects { get; private set; }

    public bool NeedsFrame => _shown is null;

    public void Configure(Orientation orientation, int brightness)
    {
        // What the panel shows means nothing in another orientation, so the next frame goes out
        // whole. In the same orientation the usual diff is still right, and sends less.
        if (orientation != _orientation) _shown = null;
        _orientation = orientation;
        _brightness = brightness;
        _configured = false;
    }

    public void Present(SKBitmap frame)
    {
        LastBytes = LastRects = 0;
        try
        {
            EnsureOpen();
            if (_off)
            {
                Protocol.WriteHeader(_header, Protocol.ScreenOn, 0, 0, 0, 0);   // the picture is still there
                _port!.Write(_header, 0, Protocol.HeaderSize);
                _off = false;
            }
            if (!_configured)
            {
                Protocol.WriteOrientation(_header, _orientation, frame.Width, frame.Height);
                _port!.Write(_header, 0, Protocol.OrientationSize);
                Protocol.WriteBrightness(_header, _brightness);
                _port.Write(_header, 0, Protocol.HeaderSize);
                _configured = true;
            }
            var pixels = frame.GetPixelSpan();
            FindDirtyRects(pixels, frame.Width, frame.Height);
            foreach (var r in _rects) Send(pixels, frame.Width, r);

            _shown ??= new byte[pixels.Length];
            pixels.CopyTo(_shown);
            LastRects = _rects.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            // Unplugged, asleep, or held by another program. Drop the port and retry next frame.
            if (e.Message != _lastError) Log.Warn($"Panel unavailable: {e.Message} (will keep retrying)");
            _lastError = e.Message;
            Close();
        }
    }

    /// <summary>SCREEN_OFF: the panel goes black at once and keeps its picture until the next SCREEN_ON, sent by the next Present or at the next connect.</summary>
    public void Blank()
    {
        if (_port is null) return;
        try
        {
            Protocol.WriteHeader(_header, Protocol.ScreenOff, 0, 0, 0, 0);
            _port.Write(_header, 0, Protocol.HeaderSize);
            _off = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Log.Warn($"Could not blank the panel: {e.Message}");
        }
    }

    private void EnsureOpen()
    {
        if (_port is not null) return;
        var name = PortFinder.Find() ?? throw new IOException("Turing Smart Screen 3.5\" not found on any COM port");

        var port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend,
            ReadTimeout = 1000,
            WriteTimeout = 5000,    // a full frame is one 2.4 s write
        };
        port.Open();
        _port = port;

        Protocol.WriteHeader(_header, Protocol.ScreenOn, 0, 0, 0, 0);   // our own exit, or another program, left it off
        port.Write(_header, 0, Protocol.HeaderSize);

        _off = false;
        _configured = false;
        _shown = null;              // panel contents unknown: first frame goes out whole
        _lastError = null;
        Log.Info($"Panel connected on {name}");
    }

    /// <summary>Fills <see cref="_rects"/> with tile-aligned rectangles covering every changed pixel.</summary>
    private void FindDirtyRects(ReadOnlySpan<byte> pixels, int width, int height)
    {
        _rects.Clear();
        _open.Clear();
        if (_shown is null) { _rects.Add(new SKRectI(0, 0, width, height)); return; }

        ReadOnlySpan<byte> shown = _shown;
        int stride = width * 2;
        for (int y = 0; y < height; y += Tile)
        {
            int bottom = Math.Min(y + Tile, height);
            int firstOpen = _open.Count;    // rects opened by this band start here

            if (!pixels[(y * stride)..(bottom * stride)].SequenceEqual(shown[(y * stride)..(bottom * stride)]))
            {
                // Horizontal runs of dirty tiles in this band.
                int runStart = -1;
                for (int x = 0; x <= width; x += Tile)
                {
                    bool dirty = x < width && TileDiffers(pixels, shown, stride, x, Math.Min(x + Tile, width), y, bottom);
                    if (dirty && runStart < 0) runStart = x;
                    else if (!dirty && runStart >= 0)
                    {
                        _open.Add(new SKRectI(runStart, y, Math.Min(x, width), bottom));
                        runStart = -1;
                    }
                }
            }

            // Grow rects from the band above when a run has the same horizontal extent; close the rest.
            for (int i = firstOpen - 1; i >= 0; i--)
            {
                var above = _open[i];
                int match = -1;
                for (int j = firstOpen; j < _open.Count; j++)
                    if (_open[j].Left == above.Left && _open[j].Right == above.Right) { match = j; break; }

                if (match >= 0)
                {
                    _open[i] = new SKRectI(above.Left, above.Top, above.Right, bottom);
                    _open.RemoveAt(match);
                }
                else
                {
                    _rects.Add(above);
                    _open.RemoveAt(i);
                    firstOpen--;
                }
            }
        }
        _rects.AddRange(_open);
    }

    private static bool TileDiffers(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, int x0, int x1, int y0, int y1)
    {
        int len = (x1 - x0) * 2;
        for (int y = y0; y < y1; y++)
        {
            int o = y * stride + x0 * 2;
            if (!a.Slice(o, len).SequenceEqual(b.Slice(o, len))) return true;
        }
        return false;
    }

    private void Send(ReadOnlySpan<byte> pixels, int width, SKRectI r)
    {
        int rowBytes = r.Width * 2, total = rowBytes * r.Height;
        if (_send.Length < total) _send = new byte[pixels.Length];
        for (int row = 0; row < r.Height; row++)
            pixels.Slice(((r.Top + row) * width + r.Left) * 2, rowBytes).CopyTo(_send.AsSpan(row * rowBytes));

        Protocol.WriteHeader(_header, Protocol.DisplayBitmap, r.Left, r.Top, r.Right - 1, r.Bottom - 1);
        _port!.Write(_header, 0, Protocol.HeaderSize);
        _port.Write(_send, 0, total);
        LastBytes += total;
    }

    private void Close()
    {
        // Closing a port whose device has gone can fail in the same ways a write can.
        try { _port?.Dispose(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _port = null;
        _shown = null;
    }

    public void Dispose() => Close();
}
