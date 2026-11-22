using System.Text.Json.Serialization;
using SkiaSharp;

namespace SensorPanel.Layout;

/// <summary>
/// Base of the six widget types. A widget is deserialized straight from its JSON object,
/// so its public properties are the config schema. Adding a type = a new subclass and one
/// more [JsonDerivedType] line here (and one in <see cref="SensorKeys"/> if it reads sensors).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PanelWidget), "panel")]
[JsonDerivedType(typeof(TextWidget), "text")]
[JsonDerivedType(typeof(BarWidget), "bar")]
[JsonDerivedType(typeof(GaugeWidget), "gauge")]
[JsonDerivedType(typeof(GraphWidget), "graph")]
[JsonDerivedType(typeof(ImageWidget), "image")]
public abstract class Widget : IDisposable
{
    public float X { get; set; }
    public float Y { get; set; }

    /// <summary>Pixels this widget last covered.</summary>
    [JsonIgnore] public SKRect Bounds { get; protected set; }

    /// <summary>The value this widget showed in the last rendered frame.</summary>
    [JsonIgnore] public object? LastDrawn { get; set; }

    /// <summary>
    /// The sensor keys this widget reads. Only these are polled, so a key-bearing property
    /// missing from this list would read "--".
    /// </summary>
    public IEnumerable<string> SensorKeys() => this switch
    {
        TextWidget t => t.Sensors ?? (t.Sensor is null ? [] : [t.Sensor]),
        BarWidget b => [b.Sensor],
        GaugeWidget g => [g.Sensor],
        GraphWidget g => [g.Sensor],
        _ => [],
    };

    /// <summary>Checks this widget's settings when the config is loaded; throws JsonException on a bad one.</summary>
    public virtual void Validate() { }

    /// <summary>One-time setup after loading (image files are resolved relative to the config).</summary>
    public virtual void Load(string configDirectory) { }

    /// <summary>
    /// Called once per tick. Reads the sensors and returns whatever determines this widget's
    /// pixels; if that equals <see cref="LastDrawn"/> for every widget, the frame is skipped.
    /// </summary>
    public abstract object? Sample(IReadOnlyDictionary<string, double> sensors);

    /// <summary>
    /// Draws the most recent sample over whatever is already on the canvas. Widgets never
    /// clear their own background, so they compose over panels and images.
    /// </summary>
    public abstract void Draw(SKCanvas canvas);

    /// <summary>
    /// Frees the Skia objects this widget keeps between frames. They are native memory, so
    /// leaving them to a finalizer can take a long time with the few collections this app makes.
    /// </summary>
    public virtual void Dispose() { }

    protected static float Fraction(double value, double min, double max) =>
        max > min ? (float)Math.Clamp((value - min) / (max - min), 0, 1) : 0;
}

public enum Align { Left, Center, Right }
public enum Edge { Left, Top, Right, Bottom }

internal static class Fonts
{
    private static readonly Dictionary<(string, int), SKTypeface> Cache = [];

    /// <summary>A font installed on this PC, by family name. Weight: 300 light, 400 regular, 600 semibold, 700 bold.</summary>
    public static SKTypeface Get(string family, int weight)
    {
        if (Cache.TryGetValue((family, weight), out var typeface)) return typeface;

        typeface = SKTypeface.FromFamilyName(family, weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        if (!typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
            Log.Warn($"Font '{family}' is not installed; using '{typeface.FamilyName}' instead.");
        return Cache[(family, weight)] = typeface;
    }
}

/// <summary>
/// One widget's text in a fixed font and colour. The font, the paint and the metrics are made
/// once, because each is a native object or a call into Skia, and a string is measured again
/// only when it differs from the last one drawn.
/// </summary>
internal sealed class Label : IDisposable
{
    private readonly SKFont _font;
    private readonly SKPaint _paint;
    private string? _measured;
    private float _width;

    public SKFontMetrics Metrics { get; }

    public Label(string family, int weight, float size, SKColor color)
    {
        _font = new SKFont(Fonts.Get(family, weight), size) { Edging = SKFontEdging.Antialias };
        _paint = new SKPaint { Color = color, IsAntialias = true };
        Metrics = _font.Metrics;
    }

    /// <summary>
    /// Draws text with (x, y) at the top of the capital letters, on the side given by align.
    /// Returns the area covered.
    /// </summary>
    public SKRect Draw(SKCanvas canvas, string text, float x, float y, Align align)
    {
        if (text != _measured) (_measured, _width) = (text, _font.MeasureText(text));
        float capHeight = Metrics.CapHeight > 0 ? Metrics.CapHeight : -Metrics.Ascent;
        float left = align switch { Align.Center => x - _width / 2, Align.Right => x - _width, _ => x };

        canvas.DrawText(text, left, y + capHeight, SKTextAlign.Left, _font, _paint);
        return new SKRect(left, y + capHeight + Metrics.Ascent, left + _width, y + capHeight + Metrics.Descent);
    }

    public void Dispose()
    {
        _font.Dispose();
        _paint.Dispose();
    }
}
