using System.Text.Json;
using SkiaSharp;
using SensorPanel.Display;
using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary>
/// <see cref="Config.Load"/>: every mistake that can be seen without hardware is a JsonException,
/// which the tray reports and then keeps the running config.
/// </summary>
public sealed class ConfigLoadTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private Config Load(string json) => Config.Load(_dir.Write("config.json", json));

    /// <summary>A config holding just these widgets (JSON objects, comma-separated).</summary>
    private Config LoadWidgets(string widgets) => Load($$"""{ "widgets": [ {{widgets}} ] }""");

    private void Rejects(string json, string? messagePart = null)
    {
        var e = Assert.Throws<JsonException>(() => Load(json));
        if (messagePart is not null) Assert.Contains(messagePart, e.Message);
    }

    private void RejectsWidgets(string widgets, string? messagePart = null) =>
        Rejects($$"""{ "widgets": [ {{widgets}} ] }""", messagePart);

    [Fact]
    public void An_empty_object_gives_the_defaults()
    {
        var config = Load("{}");
        Assert.Null(config.Name);
        Assert.Equal(1000, config.RefreshMs);
        Assert.Equal(Orientation.Landscape, config.Orientation);
        Assert.Equal(30, config.Brightness);
        Assert.Equal(SKColors.Black, config.Background);
        Assert.True(config.PauseWhenDisplayOff);
        Assert.Empty(config.Widgets);
    }

    [Fact]
    public void Comments_trailing_commas_and_camel_case_enums_are_accepted()
    {
        var config = Load("""
            {
              // a comment
              "orientation": "reversePortrait",
              "background": "#80102030",
              "widgets": [ { "type": "text", "value": "hi", }, ],
            }
            """);
        Assert.Equal(Orientation.ReversePortrait, config.Orientation);
        Assert.Equal(new SKColor(0x10, 0x20, 0x30, 0x80), config.Background);
        Assert.IsType<TextWidget>(Assert.Single(config.Widgets));
    }

    [Fact]
    public void Type_need_not_come_first()
    {
        var bar = Assert.IsType<BarWidget>(Assert.Single(LoadWidgets("""{ "sensor": "cpu.load", "w": 10, "type": "bar" }""").Widgets));
        Assert.Equal("cpu.load", bar.Sensor);
    }

    [Fact] public void Null_is_an_empty_config() => Rejects("null", "empty");
    [Fact] public void Malformed_json_is_rejected() => Rejects("{ \"refreshMs\": ");
    [Fact] public void A_misspelled_property_is_an_error() => Rejects("""{ "refreshMS": 500 }""");
    [Fact] public void A_misspelled_widget_property_is_an_error() => RejectsWidgets("""{ "type": "text", "colour": "#FFFFFF" }""");
    [Fact] public void An_unknown_widget_type_is_an_error() => RejectsWidgets("""{ "type": "dial", "sensor": "cpu.load" }""");
    [Fact] public void A_bar_without_a_sensor_is_an_error() => RejectsWidgets("""{ "type": "bar" }""");
    [Fact] public void An_unknown_orientation_is_an_error() => Rejects("""{ "orientation": "sideways" }""");

    [Theory]
    [InlineData("red")]
    [InlineData("#GG0000")]
    [InlineData("#12345")]
    public void A_bad_colour_is_an_error(string colour) => Rejects($$"""{ "background": "{{colour}}" }""", "colour");

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    public void RefreshMs_has_a_floor_of_100(int ms, bool ok)
    {
        string json = $$"""{ "refreshMs": {{ms}} }""";
        if (ok) Assert.Equal(ms, Load(json).RefreshMs);
        else Rejects(json, "refreshMs");
    }

    [Theory]
    [InlineData("gpu", 3000)]
    [InlineData("cpu", 1)]
    [InlineData("net", 3_600_000)]
    public void Hardware_groups_can_have_intervals(string group, int ms) =>
        Assert.Equal(ms, Load($$"""{ "sensorIntervals": { "{{group}}": {{ms}} } }""").SensorIntervals[group]);

    [Theory]
    [InlineData("disk")]        // computed, not read from hardware
    [InlineData("uptime")]
    [InlineData("fps")]
    [InlineData("GPU")]         // group names are exact
    public void Only_hardware_groups_can_have_intervals(string group) =>
        Rejects($$"""{ "sensorIntervals": { "{{group}}": 3000 } }""", "is not a hardware sensor group");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void Intervals_are_between_1_ms_and_an_hour(int ms) =>
        Rejects($$"""{ "sensorIntervals": { "gpu": {{ms}} } }""", "between 1 and 3600000");

    [Fact] public void A_graph_needs_two_samples_of_history() => RejectsWidgets("""{ "type": "graph", "sensor": "cpu.load", "history": 1 }""", "history");
    [Fact] public void A_graph_with_two_samples_is_fine() => LoadWidgets("""{ "type": "graph", "sensor": "cpu.load", "history": 2 }""");

    [Theory]
    [InlineData("""{ "type": "image" }""", "either")]
    [InlineData("""{ "type": "image", "path": "a.png", "folder": "slides" }""", "either")]
    [InlineData("""{ "type": "image", "folder": "slides" }""", "\"w\" and \"h\"")]
    [InlineData("""{ "type": "image", "folder": "slides", "w": 100 }""", "\"w\" and \"h\"")]
    [InlineData("""{ "type": "image", "folder": "slides", "w": 100, "h": 0 }""", "\"w\" and \"h\"")]
    [InlineData("""{ "type": "image", "folder": "slides", "w": 100, "h": 50, "intervalSeconds": 0.5 }""", "intervalSeconds")]
    public void Image_settings_are_checked(string widget, string messagePart) => RejectsWidgets(widget, messagePart);

    [Fact]
    public void An_image_path_alone_passes_the_check()
    {
        // The file itself is only read when the renderer is made.
        LoadWidgets("""{ "type": "image", "path": "missing.png" }""");
    }

    [Theory]
    [InlineData("""{ "type": "text", "sensor": "cpu.load", "format": "{0} {1}" }""")]
    [InlineData("""{ "type": "text", "sensors": ["a", "b"], "format": "{2}" }""")]
    [InlineData("""{ "type": "text", "sensor": "cpu.load", "format": "{0" }""")]
    [InlineData("""{ "type": "gauge", "sensor": "cpu.load", "text": { "format": "{1}" } }""")]
    public void A_format_needing_more_values_than_it_has_is_an_error(string widget) =>
        RejectsWidgets(widget, "does not work with");

    [Theory]
    [InlineData("""{ "type": "text", "sensor": "cpu.load", "format": "{0:F1} %" }""")]
    [InlineData("""{ "type": "text", "sensors": ["a", "b"], "format": "{0:F1} / {1:F0} GB" }""")]
    [InlineData("""{ "type": "text", "sensors": ["a", "b"], "format": "{1} only" }""")]     // a value may go unused
    [InlineData("""{ "type": "text", "value": "{9}" }""")]     // a fixed value is not a format
    [InlineData("""{ "type": "gauge", "sensor": "cpu.load", "text": { "format": "{0:F0}°" } }""")]
    public void Formats_that_fit_their_values_are_fine(string widget) => LoadWidgets(widget);

    [Fact]
    public void A_bad_clock_format_is_an_error() =>
        RejectsWidgets("""{ "type": "text", "clock": "%" }""", "clock format");

    [Fact]
    public void A_clock_ignores_format() =>
        LoadWidgets("""{ "type": "text", "clock": "HH:mm", "format": "{5}" }""");
}
