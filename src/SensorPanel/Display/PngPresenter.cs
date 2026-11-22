using SkiaSharp;

namespace SensorPanel.Display;

/// <summary>Writes each frame to a PNG instead of the panel (`--png out.png`), for designing layouts.</summary>
public sealed class PngPresenter(string path) : IPresenter
{
    public void Present(SKBitmap frame)
    {
        using var data = frame.Encode(SKEncodedImageFormat.Png, 100);
        try { File.WriteAllBytes(path, data.ToArray()); }
        catch (IOException) { }     // an image viewer has the file open; the next frame will try again
    }

    public bool NeedsFrame => false;
    public void Configure(Orientation orientation, int brightness) { }
    public void Blank() { }
    public void Dispose() { }
}
