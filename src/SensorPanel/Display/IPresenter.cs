using SkiaSharp;

namespace SensorPanel.Display;

/// <summary>
/// The one seam between rendering and the outside world. The widget layer hands over a
/// finished frame and neither knows nor cares how (or whether) it reaches a panel.
/// </summary>
public interface IPresenter : IDisposable
{
    /// <summary>Panel settings for the frames that follow. Called when a config is loaded.</summary>
    void Configure(Orientation orientation, int brightness);

    /// <param name="frame">Panel-sized bitmap in <see cref="SKColorType.Rgb565"/>.</param>
    void Present(SKBitmap frame);

    /// <summary>
    /// True while what the panel shows is unknown: it was never reached, or it was lost. The
    /// next frame is then presented even if nothing in it changed, which is also what retries
    /// the connection; otherwise a replugged panel would stay blank until a value moved.
    /// </summary>
    bool NeedsFrame { get; }

    /// <summary>
    /// Darkens the panel: before the app exits, so it does not sit on a stale frame, and while
    /// the PC sleeps or the monitors are off. The next <see cref="Present"/> lights it again.
    /// </summary>
    void Blank();
}
