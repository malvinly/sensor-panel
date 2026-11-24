using System.Globalization;
using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary>
/// What each widget's Sample returns: the text it will show, or whatever decides its pixels.
/// The renderer skips a frame when every widget returns what it returned last time.
/// </summary>
public class WidgetSampleTests
{
    private static Dictionary<string, double> Sensors(params (string Key, double Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Text_formats_its_sensor()
    {
        var text = new TextWidget { Sensor = "cpu.temp", Format = "{0:F1} °C" };
        Assert.Equal("47.5 °C", text.Sample(Sensors(("cpu.temp", 47.53))));
    }

    [Fact]
    public void Text_formats_several_sensors()
    {
        var text = new TextWidget { Sensors = ["mem.used", "mem.total"], Format = "{0:F1} / {1:F0} GB" };
        Assert.Equal("14.6 / 31 GB", text.Sample(Sensors(("mem.used", 14.62), ("mem.total", 31.1))));
    }

    [Fact]
    public void Text_shows_dashes_when_any_of_its_sensors_is_missing()
    {
        var text = new TextWidget { Sensors = ["mem.used", "mem.total"], Format = "{0:F1} / {1:F0} GB" };
        Assert.Equal("--", text.Sample(Sensors(("mem.used", 14.62))));
        Assert.Equal("--", new TextWidget { Sensor = "cpu.temp" }.Sample(Sensors()));
    }

    [Fact]
    public void Text_numbers_do_not_follow_the_PCs_language()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");    // writes 1,5
            Assert.Equal("1.5", new TextWidget { Sensor = "a", Format = "{0:F1}" }.Sample(Sensors(("a", 1.5))));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Text_shows_its_fixed_value_uppercased_if_asked()
    {
        Assert.Equal("CPU Temp", new TextWidget { Value = "CPU Temp" }.Sample(Sensors()));
        Assert.Equal("CPU TEMP", new TextWidget { Value = "CPU Temp", Upper = true }.Sample(Sensors()));
        Assert.Equal("", new TextWidget().Sample(Sensors()));
    }

    [Fact]
    public void Text_with_a_sensor_ignores_its_value()
    {
        Assert.Equal("--", new TextWidget { Sensor = "a", Value = "fixed" }.Sample(Sensors()));
    }

    [Theory]
    [InlineData(50, 0, 100, 50)]
    [InlineData(0, 0, 100, 0)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(-20, 0, 100, 0)]       // clamped
    [InlineData(250, 0, 100, 100)]
    [InlineData(55, 20, 90, 50)]       // a range that does not start at 0
    [InlineData(50, 100, 100, 0)]      // an empty range shows nothing
    [InlineData(50, 100, 0, 0)]        // and so does a reversed one
    public void Bar_reports_the_pixels_it_fills(double value, double min, double max, float pixels)
    {
        var bar = new BarWidget { Sensor = "a", W = 100, Min = min, Max = max };
        Assert.Equal(pixels, bar.Sample(Sensors(("a", value))));
    }

    [Fact]
    public void Bar_reports_the_same_for_a_change_too_small_to_see()
    {
        var bar = new BarWidget { Sensor = "a", W = 100 };
        Assert.Equal(bar.Sample(Sensors(("a", 41.2))), bar.Sample(Sensors(("a", 41.4))));
        Assert.NotEqual(bar.Sample(Sensors(("a", 41.2))), bar.Sample(Sensors(("a", 42.2))));
    }

    [Fact]
    public void Segmented_bar_reports_lit_blocks()
    {
        var bar = new BarWidget { Sensor = "a", W = 100, Segments = 10 };
        Assert.Equal(4f, bar.Sample(Sensors(("a", 44))));
        Assert.Equal(5f, bar.Sample(Sensors(("a", 46))));
    }

    [Fact]
    public void Bar_with_a_missing_sensor_is_empty()
    {
        Assert.Equal(0f, new BarWidget { Sensor = "a", W = 100 }.Sample(Sensors()));
    }

    [Fact]
    public void Gauge_reports_whole_degrees_and_its_text()
    {
        var gauge = new GaugeWidget { Sensor = "a", Text = new GaugeWidget.CenterText { Format = "{0:F0}%" } };
        Assert.Equal((90f, "25%"), gauge.Sample(Sensors(("a", 25))));
        Assert.Equal((0f, "--"), gauge.Sample(Sensors()));
    }

    [Fact]
    public void Gauge_without_text_reports_an_empty_string()
    {
        Assert.Equal((180f, ""), new GaugeWidget { Sensor = "a" }.Sample(Sensors(("a", 50))));
    }

    [Fact]
    public void Graph_moves_on_every_reading_and_only_then()
    {
        var graph = new GraphWidget { Sensor = "a", History = 3 };
        var first = graph.Sample(Sensors(("a", 10)));
        var second = graph.Sample(Sensors(("a", 10)));    // the same value still scrolls the curve
        Assert.NotEqual(first, second);
        Assert.Equal(second, graph.Sample(Sensors()));    // no reading: nothing moves
    }

    [Fact]
    public void Panel_and_single_image_never_change()
    {
        Assert.Null(new PanelWidget().Sample(Sensors()));
        Assert.Null(new ImageWidget { Path = "a.png" }.Sample(Sensors()));
    }

    [Fact]
    public void SensorKeys_lists_every_key_a_widget_reads()
    {
        Assert.Equal(["a"], new TextWidget { Sensor = "a" }.SensorKeys());
        Assert.Equal(["a", "b"], new TextWidget { Sensor = "ignored", Sensors = ["a", "b"] }.SensorKeys());
        Assert.Empty(new TextWidget { Value = "x" }.SensorKeys());
        Assert.Equal(["a"], new BarWidget { Sensor = "a" }.SensorKeys());
        Assert.Equal(["a"], new GaugeWidget { Sensor = "a" }.SensorKeys());
        Assert.Equal(["a"], new GraphWidget { Sensor = "a" }.SensorKeys());
        Assert.Empty(new PanelWidget().SensorKeys());
        Assert.Empty(new ImageWidget { Path = "a.png" }.SensorKeys());
        Assert.Equal(["weather.code", "weather.day"], new TextWidget { Weather = WeatherText.Icon, Sensor = "ignored" }.SensorKeys());
        Assert.Empty(new TextWidget { Clock = "HH:mm", Weather = WeatherText.Icon, Sensor = "ignored" }.SensorKeys());   // the clock wins
    }

    [Fact]
    public void Weather_text_names_the_conditions_or_shows_their_icon()
    {
        var partlyCloudy = Sensors(("weather.code", 2), ("weather.day", 1));
        Assert.Equal("Partly cloudy", new TextWidget { Weather = WeatherText.Condition }.Sample(partlyCloudy));
        Assert.Equal("PARTLY CLOUDY", new TextWidget { Weather = WeatherText.Condition, Upper = true }.Sample(partlyCloudy));
        Assert.Equal("⛅", new TextWidget { Weather = WeatherText.Icon }.Sample(partlyCloudy));
    }

    [Fact]
    public void Weather_text_shows_dashes_without_a_reading()
    {
        Assert.Equal("--", new TextWidget { Weather = WeatherText.Icon }.Sample(Sensors()));
        Assert.Equal("--", new TextWidget { Weather = WeatherText.Condition }.Sample(Sensors()));
    }
}

/// <summary>Clocks read the static <see cref="TextWidget.Now"/>, so these run in the one-at-a-time collection.</summary>
[Collection(RenderingCollection.Name)]
public class ClockTests
{
    [Fact]
    public void Clock_shows_the_current_time_in_its_format()
    {
        var saved = TextWidget.Now;
        try
        {
            TextWidget.Now = () => new DateTime(2026, 11, 22, 9, 5, 0);
            Assert.Equal("09:05", new TextWidget { Clock = "HH:mm" }.Sample(new Dictionary<string, double>()));
            Assert.Equal("SUN 22 NOV", new TextWidget { Clock = "ddd dd MMM", Upper = true }.Sample(new Dictionary<string, double>()));
        }
        finally { TextWidget.Now = saved; }
    }
}
