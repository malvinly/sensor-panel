namespace SensorPanel.Display;

/// <summary>Wire format of the Turing Smart Screen 3.5" (hardware revision A).</summary>
public static class Protocol
{
    public const byte Reset = 101;
    public const byte Clear = 102;
    public const byte ScreenOff = 108;
    public const byte ScreenOn = 109;
    public const byte SetBrightness = 110;
    public const byte SetOrientation = 121;
    public const byte DisplayBitmap = 197;

    public const int HeaderSize = 6;
    public const int OrientationSize = 16;

    /// <summary>Packs a rectangle (inclusive corners, 10 bits each) and a command into 6 bytes.</summary>
    public static void WriteHeader(Span<byte> b, byte command, int x, int y, int ex, int ey)
    {
        b[0] = (byte)(x >> 2);
        b[1] = (byte)(((x & 3) << 6) + (y >> 4));
        b[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        b[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        b[4] = (byte)(ey & 255);
        b[5] = command;
    }

    /// <summary>Panel brightness is inverted on the wire: 0 = brightest, 255 = darkest.</summary>
    public static void WriteBrightness(Span<byte> b, int percent)
    {
        int level = 255 - Math.Clamp(percent, 0, 100) * 255 / 100;
        WriteHeader(b, SetBrightness, level, 0, 0, 0);
    }

    /// <summary>The one 16-byte command. Width/height are the size in the new orientation.</summary>
    public static void WriteOrientation(Span<byte> b, Orientation orientation, int width, int height)
    {
        b[..OrientationSize].Clear();
        b[5] = SetOrientation;
        b[6] = (byte)((int)orientation + 100);
        b[7] = (byte)(width >> 8);
        b[8] = (byte)(width & 255);
        b[9] = (byte)(height >> 8);
        b[10] = (byte)(height & 255);
    }

    /// <summary>The same orientation turned 180°, for a panel mounted the other way up.</summary>
    public static Orientation Turned(Orientation orientation) => orientation switch
    {
        Orientation.Portrait => Orientation.ReversePortrait,
        Orientation.ReversePortrait => Orientation.Portrait,
        Orientation.Landscape => Orientation.ReverseLandscape,
        Orientation.ReverseLandscape => Orientation.Landscape,
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null),     // Config.Load lets no other through
    };
}

/// <summary>Values are the panel's own orientation codes.</summary>
public enum Orientation
{
    Portrait = 0,
    ReversePortrait = 1,
    Landscape = 2,
    ReverseLandscape = 3,
}
