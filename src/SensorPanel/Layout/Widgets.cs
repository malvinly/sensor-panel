using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace SensorPanel.Layout;

/// <summary>Rounded rectangle with optional border and an accent stripe along one edge.</summary>
public sealed class PanelWidget : Widget
{
    public float W { get; set; }
    public float H { get; set; }
    public float Radius { get; set; }
    public SKColor? Fill { get; set; }
    public BorderSpec? Border { get; set; }
    public AccentSpec? Accent { get; set; }

    public sealed class BorderSpec
    {
        public SKColor Color { get; set; } = SKColors.Gray;
        public float Width { get; set; } = 1;
    }

    public sealed class AccentSpec
    {
        public Edge Edge { get; set; } = Edge.Left;
        public SKColor Color { get; set; } = SKColors.White;
        public float Width { get; set; } = 4;
    }

    // Made on the first draw and kept: nothing about a panel changes between frames.
    private SKRoundRect? _shape;
    private SKPaint? _fillPaint, _accentPaint, _borderPaint;

    public override object? Sample(IReadOnlyDictionary<string, double> sensors) => null;

    public override void Draw(SKCanvas canvas)
    {
        Bounds = new SKRect(X, Y, X + W, Y + H);
        _shape ??= new SKRoundRect(Bounds, Radius);

        if (Fill is SKColor fill)
        {
            _fillPaint ??= new SKPaint { IsAntialias = true, Color = fill };
            canvas.DrawRoundRect(_shape, _fillPaint);
        }

        if (Accent is not null)
        {
            var stripe = Accent.Edge switch
            {
                Edge.Left => new SKRect(X, Y, X + Accent.Width, Y + H),
                Edge.Right => new SKRect(X + W - Accent.Width, Y, X + W, Y + H),
                Edge.Top => new SKRect(X, Y, X + W, Y + Accent.Width),
                _ => new SKRect(X, Y + H - Accent.Width, X + W, Y + H),
            };
            _accentPaint ??= new SKPaint { IsAntialias = true, Color = Accent.Color };
            canvas.Save();
            canvas.ClipRoundRect(_shape, antialias: true);   // the stripe follows the rounded corners
            canvas.DrawRect(stripe, _accentPaint);
            canvas.Restore();
        }

        if (Border is not null)
        {
            _borderPaint ??= new SKPaint { IsAntialias = true, Color = Border.Color, Style = SKPaintStyle.Stroke, StrokeWidth = Border.Width };
            float inset = Border.Width / 2;
            canvas.DrawRoundRect(new SKRect(X + inset, Y + inset, X + W - inset, Y + H - inset), Radius, Radius, _borderPaint);
        }
    }

    public override void Dispose()
    {
        _shape?.Dispose();
        _fillPaint?.Dispose();
        _accentPaint?.Dispose();
        _borderPaint?.Dispose();
    }
}

/// <summary>
/// A line of text: a literal ("value"), one or more sensors through a format string, or the
/// clock. (x, y) is the top of the capital letters; x is the left, centre or right per "align".
/// </summary>
public sealed class TextWidget : Widget
{
    public string? Value { get; set; }
    public string? Sensor { get; set; }
    /// <summary>Several keys for one string: "{0:F1} / {1:F0} GB".</summary>
    public string[]? Sensors { get; set; }
    /// <summary>.NET composite format applied to the sensor value(s).</summary>
    public string Format { get; set; } = "{0:F0}";
    /// <summary>.NET date/time format for the current local time, e.g. "HH:mm" or "ddd dd MMM yyyy".</summary>
    public string? Clock { get; set; }
    public bool Upper { get; set; }

    /// <summary>The time a clock shows. The Screenshots tool fixes it so its pictures do not change from run to run.</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public string Font { get; set; } = "Segoe UI";
    public float Size { get; set; } = 14;
    public int Weight { get; set; } = 400;
    public SKColor Color { get; set; } = SKColors.White;
    public Align Align { get; set; } = Align.Left;

    private string _text = "";
    private Label? _label;          // made on the first draw

    public override void Validate()
    {
        if (Clock is not null)
        {
            try { DateTime.Now.ToString(Clock, CultureInfo.InvariantCulture); }
            catch (FormatException) { throw new JsonException($"text: \"{Clock}\" is not a valid clock format."); }
        }
        else if ((Sensors ?? (Sensor is null ? null : [Sensor])) is { } keys) CheckFormat("text", Format, keys.Length);
    }

    /// <summary>A format that asks for more values than it is given would otherwise fail only once the sensor first reads.</summary>
    internal static void CheckFormat(string widget, string format, int values)
    {
        try { string.Format(CultureInfo.InvariantCulture, format, new object[values].Select(_ => (object)0.0).ToArray()); }
        catch (FormatException) { throw new JsonException($"{widget}: format \"{format}\" does not work with {values} sensor value(s)."); }
    }

    public override object? Sample(IReadOnlyDictionary<string, double> sensors)
    {
        _text = Clock is not null ? Now().ToString(Clock, CultureInfo.InvariantCulture)
            : (Sensors ?? (Sensor is null ? null : [Sensor])) is { } keys ? FormatSensors(Format, keys, sensors)
            : Value ?? "";
        if (Upper) _text = _text.ToUpperInvariant();
        return _text;
    }

    public override void Draw(SKCanvas canvas)
    {
        _label ??= new Label(Font, Weight, Size, Color);
        Bounds = _label.Draw(canvas, _text, X, Y, Align);
    }

    public override void Dispose() => _label?.Dispose();

    /// <summary>"--" when any key is missing (e.g. temperatures when not elevated).</summary>
    internal static string FormatSensors(string format, string[] keys, IReadOnlyDictionary<string, double> sensors)
    {
        var values = new object[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (!sensors.TryGetValue(keys[i], out var v)) return "--";
            values[i] = v;
        }
        return string.Format(CultureInfo.InvariantCulture, format, values);
    }
}

/// <summary>Horizontal meter, continuous or split into blocks with "segments".</summary>
public sealed class BarWidget : Widget
{
    public required string Sensor { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public SKColor Color { get; set; } = SKColors.White;
    public SKColor Background { get; set; } = new(0x26, 0x2A, 0x33);
    public float Radius { get; set; }
    /// <summary>0 = one continuous bar; N = N blocks that light up whole.</summary>
    public int Segments { get; set; }
    public float Gap { get; set; } = 2;

    private float _fraction;
    // Made on the first draw and kept.
    private SKPaint? _litPaint, _unlitPaint;
    private SKRoundRect? _shape;

    public override object? Sample(IReadOnlyDictionary<string, double> sensors)
    {
        _fraction = sensors.TryGetValue(Sensor, out var v) ? Fraction(v, Min, Max) : 0;
        // What is on screen is whole pixels or whole blocks; report that, not the raw value.
        return Segments > 0 ? MathF.Round(_fraction * Segments) : MathF.Round(_fraction * W);
    }

    public override void Draw(SKCanvas canvas)
    {
        Bounds = new SKRect(X, Y, X + W, Y + H);
        _litPaint ??= new SKPaint { IsAntialias = true, Color = Color };
        _unlitPaint ??= new SKPaint { IsAntialias = true, Color = Background };

        if (Segments > 0)
        {
            float blockWidth = (W - Gap * (Segments - 1)) / Segments;
            int lit = (int)MathF.Round(_fraction * Segments);
            for (int i = 0; i < Segments; i++)
            {
                float left = X + i * (blockWidth + Gap);
                canvas.DrawRoundRect(new SKRect(left, Y, left + blockWidth, Y + H), Radius, Radius, i < lit ? _litPaint : _unlitPaint);
            }
            return;
        }

        _shape ??= new SKRoundRect(Bounds, Radius);
        canvas.DrawRoundRect(_shape, _unlitPaint);
        canvas.Save();
        canvas.ClipRoundRect(_shape, antialias: true);
        canvas.DrawRoundRect(new SKRect(X, Y, X + MathF.Round(_fraction * W), Y + H), Radius, Radius, _litPaint);
        canvas.Restore();
    }

    public override void Dispose()
    {
        _litPaint?.Dispose();
        _unlitPaint?.Dispose();
        _shape?.Dispose();
    }
}

/// <summary>
/// Arc gauge centred on (x, y). Angles are degrees with 0 at 3 o'clock, increasing clockwise,
/// so startAngle 135 / endAngle 45 is the classic 270° dial open at the bottom.
/// </summary>
public sealed class GaugeWidget : Widget
{
    public required string Sensor { get; set; }
    /// <summary>Outer radius.</summary>
    public float Radius { get; set; } = 20;
    public float Thickness { get; set; } = 4;
    public float StartAngle { get; set; } = 135;
    public float EndAngle { get; set; } = 45;
    public bool Clockwise { get; set; } = true;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public SKColor Color { get; set; } = SKColors.White;
    public SKColor Background { get; set; } = new(0x26, 0x2A, 0x33);
    /// <summary>Optional readout in the middle of the dial.</summary>
    public CenterText? Text { get; set; }

    public sealed class CenterText
    {
        public string Format { get; set; } = "{0:F0}";
        public string Font { get; set; } = "Segoe UI";
        public float Size { get; set; } = 12;
        public int Weight { get; set; } = 400;
        public SKColor Color { get; set; } = SKColors.White;
    }

    private float _fraction;
    private string _text = "";
    // Made on the first draw and kept.
    private SKPaint? _trackPaint, _valuePaint;
    private Label? _label;

    public override void Validate()
    {
        if (Text is not null) TextWidget.CheckFormat("gauge", Text.Format, 1);
    }

    public override object? Sample(IReadOnlyDictionary<string, double> sensors)
    {
        _fraction = sensors.TryGetValue(Sensor, out var v) ? Fraction(v, Min, Max) : 0;
        if (Text is not null) _text = TextWidget.FormatSensors(Text.Format, [Sensor], sensors);
        return (MathF.Round(_fraction * 360), _text);   // whole degrees
    }

    public override void Draw(SKCanvas canvas)
    {
        Bounds = new SKRect(X - Radius, Y - Radius, X + Radius, Y + Radius);
        var arc = Bounds;
        arc.Inflate(-Thickness / 2, -Thickness / 2);

        float sweep = ((Clockwise ? EndAngle - StartAngle : StartAngle - EndAngle) % 360 + 360) % 360;
        if (sweep == 0) sweep = 360;
        if (!Clockwise) sweep = -sweep;

        _trackPaint ??= ArcPaint(Background);
        _valuePaint ??= ArcPaint(Color);
        canvas.DrawArc(arc, StartAngle, sweep, false, _trackPaint);
        if (_fraction > 0) canvas.DrawArc(arc, StartAngle, sweep * _fraction, false, _valuePaint);

        if (Text is not null)
        {
            _label ??= new Label(Text.Font, Text.Weight, Text.Size, Text.Color);
            _label.Draw(canvas, _text, X, Y - _label.Metrics.CapHeight / 2, Align.Center);
        }
    }

    private SKPaint ArcPaint(SKColor color) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = Thickness,
        StrokeCap = SKStrokeCap.Round,
        Color = color,
    };

    public override void Dispose()
    {
        _trackPaint?.Dispose();
        _valuePaint?.Dispose();
        _label?.Dispose();
    }
}

/// <summary>Sparkline of the last "history" samples, newest at the right edge.</summary>
public sealed class GraphWidget : Widget
{
    public required string Sensor { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public int History { get; set; } = 60;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public SKColor Color { get; set; } = SKColors.White;
    public float Thickness { get; set; } = 1.5f;
    /// <summary>Shade the area under the curve with a translucent tint of "color".</summary>
    public bool Fill { get; set; }

    private float[] _ring = [];
    private int _count, _next, _ticks;
    // Made on the first draw and kept.
    private SKPoint[]? _points;
    private SKPathBuilder? _builder;
    private SKPaint? _fillPaint, _linePaint;

    public override void Validate()
    {
        if (History < 2) throw new JsonException("graph: history must be at least 2.");
    }

    public override object? Sample(IReadOnlyDictionary<string, double> sensors)
    {
        if (_ring.Length != History) _ring = new float[History];
        if (!sensors.TryGetValue(Sensor, out var v)) return _ticks;

        _ring[_next] = Fraction(v, Min, Max);
        _next = (_next + 1) % History;
        _count = Math.Min(_count + 1, History);
        return ++_ticks;    // a new sample always moves the curve
    }

    public override void Draw(SKCanvas canvas)
    {
        Bounds = new SKRect(X, Y, X + W, Y + H);
        if (_count < 2) return;

        float step = W / (History - 1);
        float inset = Thickness / 2;    // keep the stroke inside the box at 0% and 100%
        // Curve points, then the two bottom corners that close the filled area.
        var points = _points ??= new SKPoint[History + 2];
        for (int i = 0; i < _count; i++)
        {
            float sample = _ring[(_next - _count + i + History) % History];
            points[i] = new SKPoint(X + W - (_count - 1 - i) * step, Y + H - inset - sample * (H - Thickness));
        }
        points[_count] = new SKPoint(X + W, Y + H);
        points[_count + 1] = new SKPoint(points[0].X, Y + H);

        var builder = _builder ??= new SKPathBuilder();
        if (Fill)
        {
            builder.AddPoly(points.AsSpan(0, _count + 2), true);
            using var area = builder.Detach();
            _fillPaint ??= new SKPaint { IsAntialias = true, Color = Color.WithAlpha((byte)(Color.Alpha * 0.22f)) };
            canvas.DrawPath(area, _fillPaint);
        }

        builder.AddPoly(points.AsSpan(0, _count), false);
        using var line = builder.Detach();
        _linePaint ??= new SKPaint
        {
            IsAntialias = true,
            Color = Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Thickness,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawPath(line, _linePaint);
    }

    public override void Dispose()
    {
        _builder?.Dispose();
        _fillPaint?.Dispose();
        _linePaint?.Dispose();
    }
}

/// <summary>
/// A picture at (x, y). Either "path": one PNG/JPEG, stretched to w x h if given. Or "folder":
/// a slideshow of every PNG/JPEG in it, in name order, a new one every "intervalSeconds", each
/// scaled to fill w x h with the overflow cropped. Both are relative to the config file.
/// </summary>
public sealed class ImageWidget : Widget
{
    public string? Path { get; set; }
    public string? Folder { get; set; }
    public double IntervalSeconds { get; set; } = 10;
    public float? W { get; set; }
    public float? H { get; set; }

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKPaint Paint = new() { IsAntialias = true };   // never changed, so shared by every image

    private SKImage? _image;        // slideshow: the current slide at w x h, or null if no file could be read
    private string[] _files = [];
    private readonly HashSet<string> _unreadable = [];
    private long _started;
    private int _slot = -1;

    public override void Validate()
    {
        if ((Path is null) == (Folder is null)) throw new JsonException("image: give either \"path\" or \"folder\", not both.");
        if (Folder is null) return;
        if (W is not > 0 || H is not > 0) throw new JsonException("image: a \"folder\" needs \"w\" and \"h\", the size every picture is scaled to.");
        if (IntervalSeconds < 1) throw new JsonException("image: intervalSeconds must be at least 1.");
    }

    public override void Load(string configDirectory)
    {
        if (Folder is null)
        {
            string full = System.IO.Path.Combine(configDirectory, Path!);
            _image = SKImage.FromEncodedData(full) ?? throw new IOException($"Cannot read image '{full}'.");
            return;
        }

        string directory = System.IO.Path.Combine(configDirectory, Folder);
        if (Directory.Exists(directory))
            _files = Directory.GetFiles(directory)
                .Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_files.Length == 0) throw new IOException($"No .png or .jpg pictures in folder '{directory}'.");
        _started = Environment.TickCount64;
    }

    public override object? Sample(IReadOnlyDictionary<string, double> sensors)
    {
        if (Folder is null) return null;

        int slot = (int)((Environment.TickCount64 - _started) / (long)(IntervalSeconds * 1000));
        if (slot != _slot)
        {
            _slot = slot;
            ShowSlide(slot % _files.Length);
        }
        return _slot;
    }

    /// <summary>
    /// Only the current slide is held, already at its final size. Scaling decodes the photo at
    /// full size, with mipmaps, into Skia's shared cache (~90 MB for 24 MP), so the cache is purged
    /// afterwards; that also drops a "path" picture's pixels, which decode again on its next draw.
    /// A file that cannot be read is reported once and the next one is shown.
    /// </summary>
    private void ShowSlide(int index)
    {
        _image?.Dispose();
        _image = null;
        for (int tried = 0; tried < _files.Length && _image is null; tried++)
        {
            string file = _files[(index + tried) % _files.Length];
            if (_unreadable.Contains(file)) continue;

            using var source = File.Exists(file) ? SKImage.FromEncodedData(file) : null;
            if (source is null)
            {
                _unreadable.Add(file);
                Log.Warn($"Slideshow: cannot read '{file}'; skipping it.");
                continue;
            }

            // Scale to cover the box and crop what overflows, centred, so nothing is distorted.
            float scale = Math.Max(W!.Value / source.Width, H!.Value / source.Height);
            float cropW = W.Value / scale, cropH = H.Value / scale;
            var crop = SKRect.Create((source.Width - cropW) / 2, (source.Height - cropH) / 2, cropW, cropH);

            using var surface = SKSurface.Create(new SKImageInfo((int)W.Value, (int)H.Value));
            surface.Canvas.DrawImage(source, crop, SKRect.Create(W.Value, H.Value), Sampling);
            _image = surface.Snapshot();
        }
        SKGraphics.PurgeResourceCache();
    }

    public override void Draw(SKCanvas canvas)
    {
        Bounds = new SKRect(X, Y, X + (W ?? _image!.Width), Y + (H ?? _image!.Height));
        if (_image is null) return;
        canvas.DrawImage(_image, Bounds, Sampling, Paint);
    }

    public override void Dispose() => _image?.Dispose();
}
