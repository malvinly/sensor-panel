using SkiaSharp;
using SensorPanel.Display;

namespace SensorPanel.Tests;

/// <summary>
/// <see cref="SerialPresenter.FindDirtyRects"/>: which parts of a frame are sent to the panel.
/// A mistake here leaves stale pixels on the screen without any error, so the main test checks
/// the result tile by tile against a straightforward recount, on many random changes.
/// </summary>
public class DirtyRectsTests
{
    private const int Tile = 8;

    private static List<SKRectI> Find(byte[] pixels, byte[]? shown, int width, int height)
    {
        var rects = new List<SKRectI>();
        SerialPresenter.FindDirtyRects(pixels, shown, width, height, rects, []);
        return rects;
    }

    private static void Change(byte[] frame, int width, int x, int y) => frame[(y * width + x) * 2 + 1] ^= 0xFF;

    [Fact]
    public void Unknown_panel_contents_send_the_whole_frame()
    {
        Assert.Equal([new SKRectI(0, 0, 480, 320)], Find(new byte[480 * 320 * 2], null, 480, 320));
    }

    [Fact]
    public void A_column_of_tiles_merges_into_one_rect()
    {
        var shown = new byte[480 * 320 * 2];
        var frame = (byte[])shown.Clone();
        for (int y = 40; y < 64; y++) Change(frame, 480, 100, y);
        Assert.Equal([new SKRectI(96, 40, 104, 64)], Find(frame, shown, 480, 320));
    }

    [Fact]
    public void Runs_of_different_widths_stay_separate_rects()
    {
        // An L: two tiles wide in the first band, one tile wide in the next.
        var shown = new byte[480 * 320 * 2];
        var frame = (byte[])shown.Clone();
        Change(frame, 480, 0, 0);
        Change(frame, 480, 8, 0);
        Change(frame, 480, 0, 8);
        var rects = Find(frame, shown, 480, 320);
        Assert.Equal(2, rects.Count);
        Assert.Contains(new SKRectI(0, 0, 16, 8), rects);
        Assert.Contains(new SKRectI(0, 8, 8, 16), rects);
    }

    [Fact]
    public void Lists_reused_from_frame_to_frame_give_the_same_result_as_fresh_ones()
    {
        // The presenter passes the same two lists every frame, and "open" still holds the last
        // band's runs when a call returns.
        var random = new Random(99);
        var rects = new List<SKRectI>();
        var open = new List<SKRectI>();
        var shown = new byte[64 * 48 * 2];
        for (int i = 0; i < 50; i++)
        {
            var frame = (byte[])shown.Clone();
            for (int c = random.Next(0, 40); c > 0; c--) Change(frame, 64, random.Next(64), random.Next(48));

            SerialPresenter.FindDirtyRects(frame, shown, 64, 48, rects, open);
            Assert.Equal(Find(frame, shown, 64, 48), rects);
            shown = frame;
        }
    }

    [Theory]
    [InlineData(480, 320, 50)]
    [InlineData(320, 480, 50)]
    [InlineData(64, 48, 500)]
    [InlineData(40, 37, 500)]       // a height that is not a whole number of tiles
    [InlineData(44, 32, 500)]       // a width that is not: a run reaching the right edge was once never closed, and lost
    [InlineData(45, 31, 500)]
    public void Random_changes_are_covered_exactly_once(int width, int height, int iterations)
    {
        var random = new Random(1234);
        for (int i = 0; i < iterations; i++)
        {
            var shown = new byte[width * height * 2];
            random.NextBytes(shown);
            var frame = (byte[])shown.Clone();

            // A few rectangles of change, of any size, sometimes none at all.
            int blobs = random.Next(0, 6);
            for (int b = 0; b < blobs; b++)
            {
                int x0 = random.Next(width), y0 = random.Next(height);
                int x1 = Math.Min(width, x0 + random.Next(1, width / 2)), y1 = Math.Min(height, y0 + random.Next(1, height / 2));
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if (random.Next(3) == 0) Change(frame, width, x, y);
            }

            AssertCoversExactlyTheDirtyTiles(frame, shown, width, height, Find(frame, shown, width, height));
        }
    }

    private static void AssertCoversExactlyTheDirtyTiles(byte[] frame, byte[] shown, int width, int height, List<SKRectI> rects)
    {
        int columns = (width + Tile - 1) / Tile, rows = (height + Tile - 1) / Tile;

        var dirty = new bool[columns, rows];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 2;
                if (frame[o] != shown[o] || frame[o + 1] != shown[o + 1]) dirty[x / Tile, y / Tile] = true;
            }

        var covered = new int[columns, rows];
        foreach (var r in rects)
        {
            Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= width && r.Bottom <= height, $"{r} is outside the frame");
            Assert.True(r.Width > 0 && r.Height > 0, $"{r} is empty");
            Assert.True(r.Left % Tile == 0 && r.Top % Tile == 0, $"{r} does not start on a tile");
            Assert.True(r.Right % Tile == 0 || r.Right == width, $"{r} does not end on a tile");
            Assert.True(r.Bottom % Tile == 0 || r.Bottom == height, $"{r} does not end on a tile");
            for (int ty = r.Top / Tile; ty * Tile < r.Bottom; ty++)
                for (int tx = r.Left / Tile; tx * Tile < r.Right; tx++)
                    covered[tx, ty]++;
        }

        for (int ty = 0; ty < rows; ty++)
            for (int tx = 0; tx < columns; tx++)
            {
                // A clean tile is never sent: rects grow only by runs of the same extent, all dirty.
                Assert.True(covered[tx, ty] <= 1, $"tile ({tx}, {ty}) is sent {covered[tx, ty]} times");
                Assert.True(dirty[tx, ty] == (covered[tx, ty] == 1),
                    $"tile ({tx}, {ty}) is {(dirty[tx, ty] ? "dirty but not sent" : "clean but sent")}");
            }
    }
}
