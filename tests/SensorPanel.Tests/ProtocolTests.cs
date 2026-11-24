using SensorPanel.Display;

namespace SensorPanel.Tests;

public class ProtocolTests
{
    /// <summary>The inverse of <see cref="Protocol.WriteHeader"/>: four 10-bit coordinates and the command.</summary>
    private static (int X, int Y, int Ex, int Ey, byte Command) Unpack(ReadOnlySpan<byte> b) =>
        ((b[0] << 2) | (b[1] >> 6),
         ((b[1] & 63) << 4) | (b[2] >> 4),
         ((b[2] & 15) << 6) | (b[3] >> 2),
         ((b[3] & 3) << 8) | b[4],
         b[5]);

    [Fact]
    public void Full_landscape_frame_header_has_the_known_bytes()
    {
        var b = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(b, Protocol.DisplayBitmap, 0, 0, 479, 319);
        Assert.Equal(new byte[] { 0, 0, 7, 125, 63, 197 }, b);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1023, 1023, 1023, 1023)]
    [InlineData(1, 2, 3, 4)]
    [InlineData(479, 319, 0, 0)]
    [InlineData(0, 0, 479, 319)]
    [InlineData(333, 77, 512, 255)]
    [InlineData(3, 15, 63, 256)]   // values whose low bits land at each byte boundary
    public void Header_coordinates_round_trip(int x, int y, int ex, int ey)
    {
        var b = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(b, Protocol.DisplayBitmap, x, y, ex, ey);
        Assert.Equal((x, y, ex, ey, Protocol.DisplayBitmap), Unpack(b));
    }

    [Theory]
    [InlineData(0, 255)]
    [InlineData(100, 0)]
    [InlineData(20, 204)]
    [InlineData(50, 128)]
    [InlineData(-5, 255)]    // clamped to 0 %
    [InlineData(150, 0)]     // clamped to 100 %
    public void Brightness_is_inverted_and_clamped(int percent, int level)
    {
        var b = new byte[Protocol.HeaderSize];
        Protocol.WriteBrightness(b, percent);
        Assert.Equal((level, 0, 0, 0, Protocol.SetBrightness), Unpack(b));
    }

    [Fact]
    public void Orientation_command_is_16_bytes_with_the_size_big_endian()
    {
        var b = Enumerable.Repeat((byte)0xAA, Protocol.OrientationSize).ToArray();   // stale bytes are cleared
        Protocol.WriteOrientation(b, Orientation.Landscape, 480, 320);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 121, 102, 1, 224, 1, 64, 0, 0, 0, 0, 0 }, b);
    }

    [Theory]
    [InlineData(Orientation.Portrait, 100)]
    [InlineData(Orientation.ReversePortrait, 101)]
    [InlineData(Orientation.Landscape, 102)]
    [InlineData(Orientation.ReverseLandscape, 103)]
    public void Orientation_codes_are_offset_by_100(Orientation orientation, byte code)
    {
        var b = new byte[Protocol.OrientationSize];
        Protocol.WriteOrientation(b, orientation, 320, 480);
        Assert.Equal(code, b[6]);
    }

    [Theory]
    [InlineData(Orientation.Portrait, Orientation.ReversePortrait)]
    [InlineData(Orientation.ReversePortrait, Orientation.Portrait)]
    [InlineData(Orientation.Landscape, Orientation.ReverseLandscape)]
    [InlineData(Orientation.ReverseLandscape, Orientation.Landscape)]
    public void Turning_an_orientation_keeps_its_shape_and_flips_it(Orientation orientation, Orientation turned) =>
        Assert.Equal(turned, Protocol.Turned(orientation));
}
