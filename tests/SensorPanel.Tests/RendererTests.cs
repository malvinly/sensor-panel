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
