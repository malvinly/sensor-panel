using SkiaSharp;
using SensorPanel.Display;
using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary><see cref="Renderer"/>: the frame's size, and skipping frames in which nothing visible changed.</summary>
[Collection(RenderingCollection.Name)]
public class RendererTests
{
    private static Renderer Bar(Orientation orientation = Orientation.Landscape) =>
        new(new Config
        {
            Orientation = orientation,
            Widgets = [new BarWidget { Sensor = "a", X = 10, Y = 10, W = 100, H = 10, Color = SKColors.White }],
        }, ".");

    private static Dictionary<string, double> A(double value) => new() { ["a"] = value };

    [Theory]
    [InlineData(Orientation.Landscape, 480, 320)]
    [InlineData(Orientation.ReverseLandscape, 480, 320)]
    [InlineData(Orientation.Portrait, 320, 480)]
    [InlineData(Orientation.ReversePortrait, 320, 480)]
    public void The_frame_is_the_panels_size_in_its_orientation(Orientation orientation, int width, int height)
    {
        using var renderer = Bar(orientation);
        Assert.Equal((width, height), (renderer.Frame.Width, renderer.Frame.Height));
        Assert.Equal(SKColorType.Rgb565, renderer.Frame.ColorType);    // the panel's own pixel format
    }

    [Fact]
    public void The_first_frame_is_always_drawn()
    {
        using var renderer = new Renderer(new Config(), ".");
        Assert.True(renderer.Render(A(0)));
        Assert.False(renderer.Render(A(0)));
    }

    [Fact]
    public void A_frame_is_skipped_unless_something_visible_changed()
    {
        using var renderer = Bar();
        Assert.True(renderer.Render(A(40)));
        Assert.False(renderer.Render(A(40)));
        Assert.False(renderer.Render(A(40.3)));     // less than a pixel of a 100 px bar
        Assert.True(renderer.Render(A(60)));
        Assert.True(renderer.Render(new Dictionary<string, double>()));   // the sensor went away: the bar empties
    }

    // A graph in the box y = 10-50, 2 px thick: its line runs from y = 49 (min) to y = 11 (max)
    // upright, and from y = 11 (min) to y = 49 (max) flipped.
    [Theory]
    [InlineData(false, 0, 49, 30)]
    [InlineData(true, 0, 11, 30)]
    [InlineData(false, 75, 20, 40)]     // 3/4 of the way up from the bottom edge
    [InlineData(true, 75, 39, 20)]      // 3/4 of the way down from the top edge
    public void A_flipped_graph_hangs_from_the_top_edge(bool flip, double value, int lineY, int emptyY)
    {
        using var renderer = Graph(flip, fill: false);
        renderer.Render(A(value));
        renderer.Render(A(value));      // two points, with a history of two: a line across the whole width
        Assert.Equal(SKColors.White, renderer.Frame.GetPixel(60, lineY));
        Assert.Equal(SKColors.Black, renderer.Frame.GetPixel(60, emptyY));
    }

    [Theory]
    [InlineData(false, 40, 20)]         // upright: tinted below the line, between it and the bottom edge
    [InlineData(true, 20, 40)]          // flipped: tinted above it, between it and the top edge
    public void A_graph_fills_between_its_line_and_its_base_edge(bool flip, int tintedY, int emptyY)
    {
        using var renderer = Graph(flip, fill: true);
        renderer.Render(A(50));         // the line at y = 30 either way
        renderer.Render(A(50));
        var tint = renderer.Frame.GetPixel(60, tintedY);
        Assert.NotEqual(SKColors.Black, tint);
        Assert.NotEqual(SKColors.White, tint);
        Assert.Equal(SKColors.Black, renderer.Frame.GetPixel(60, emptyY));
    }

    private static Renderer Graph(bool flip, bool fill) => new(new Config
    {
        Widgets = [new GraphWidget { Sensor = "a", X = 10, Y = 10, W = 100, H = 40, History = 2, Thickness = 2, Color = SKColors.White, Flip = flip, Fill = fill }],
    }, ".");

    [Fact]
    public void The_bar_is_drawn_where_and_as_long_as_its_value_says()
    {
        using var renderer = Bar();
        renderer.Render(A(50));
        Assert.Equal(SKColors.White, renderer.Frame.GetPixel(30, 15));    // inside the lit half
        var track = renderer.Frame.GetPixel(100, 15);                     // past it: the unlit track
        Assert.NotEqual(SKColors.White, track);
        Assert.NotEqual(SKColors.Black, track);
        Assert.Equal(SKColors.Black, renderer.Frame.GetPixel(200, 15));   // past the bar: the background
    }
}
