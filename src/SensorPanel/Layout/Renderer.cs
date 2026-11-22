using SkiaSharp;
using SensorPanel.Display;

namespace SensorPanel.Layout;

/// <summary>
/// Draws the widget list into an offscreen bitmap. The bitmap is RGB565, the panel's native
/// format, so a finished frame is already the bytes that go on the wire.
/// </summary>
public sealed class Renderer : IDisposable
{
    private readonly Config _config;
    private readonly SKCanvas _canvas;
    private bool _drawnOnce;

    public SKBitmap Frame { get; }

    public Renderer(Config config, string configDirectory)
    {
        _config = config;
        bool landscape = config.Orientation is Orientation.Landscape or Orientation.ReverseLandscape;
        Frame = new SKBitmap(new SKImageInfo(landscape ? 480 : 320, landscape ? 320 : 480, SKColorType.Rgb565, SKAlphaType.Opaque));
        _canvas = new SKCanvas(Frame);
        try { foreach (var widget in config.Widgets) widget.Load(configDirectory); }
        catch { Dispose(); throw; }     // the caller never gets this renderer to dispose
    }

    /// <summary>Samples every widget and redraws the frame. Returns false if nothing changed.</summary>
    public bool Render(IReadOnlyDictionary<string, double> sensors)
    {
        bool changed = !_drawnOnce;
        var samples = new object?[_config.Widgets.Count];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = _config.Widgets[i].Sample(sensors);
            changed |= !Equals(samples[i], _config.Widgets[i].LastDrawn);
        }
        if (!changed) return false;

        // Widgets composite over each other, so the whole frame is redrawn (about 0.7 ms);
        // the presenter works out which pixels actually differ.
        _canvas.Clear(_config.Background);
        for (int i = 0; i < samples.Length; i++)
        {
            _config.Widgets[i].Draw(_canvas);
            _config.Widgets[i].LastDrawn = samples[i];
        }
        _drawnOnce = true;
        return true;
    }

    public void Dispose()
    {
        foreach (var widget in _config.Widgets) widget.Dispose();
        _canvas.Dispose();
        Frame.Dispose();
    }
}
