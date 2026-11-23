using SensorPanel.Sensors;

namespace SensorPanel.Tests;

/// <summary>
/// <see cref="FrameRate"/>: the fps reading, from Present events and their timestamps. Times here
/// are in milliseconds (frequency 1000), starting well above 0, as QPC timestamps do.
/// </summary>
public class FrameRateTests
{
    private const int Game = 100, Other = 200;
    private readonly FrameRate _rate = new(1000);

    /// <summary>Frames by <paramref name="pid"/> every <paramref name="spacing"/> ms, the first at <paramref name="first"/>; returns the last one's time.</summary>
    private long Frames(int pid, long first, int count, long spacing)
    {
        for (int i = 0; i < count; i++) _rate.Present(pid, first + i * spacing);
        return first + (count - 1) * spacing;
    }

    /// <summary>Makes <see cref="Game"/> the foreground process and reads it twice, so the next read has a rate.</summary>
    private long Started(long at = 10_000)
    {
        Assert.Null(_rate.Read(Game, at));
        long last = Frames(Game, at + 10, 10, 10);
        Assert.Null(_rate.Read(Game, last));
        return last;
    }

    [Fact]
    public void A_new_foreground_process_reads_null_twice_then_its_rate()
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);     // ten frames, 10 ms apart
        Assert.Equal(100, _rate.Read(Game, last)!.Value, 6);
    }

    [Fact]
    public void The_rate_comes_from_event_timestamps_not_from_when_it_is_read()
    {
        // ETW delivers late and in bursts: read long after the frames, the rate is still theirs.
        long last = Started();
        last = Frames(Game, last + 20, 25, 20);
        Assert.Equal(50, _rate.Read(Game, last + 900)!.Value, 6);
    }

    [Fact]
    public void Reads_with_no_new_frames_keep_the_last_rate_for_up_to_3_seconds()
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);
        Assert.Equal(100, _rate.Read(Game, last)!.Value, 6);

        Assert.Equal(100, _rate.Read(Game, last + 1000)!.Value, 6);
        Assert.Equal(100, _rate.Read(Game, last + 3000)!.Value, 6);
        Assert.Null(_rate.Read(Game, last + 3001));
    }

    [Fact]
    public void After_a_pause_the_gap_is_not_counted_as_a_frame_time()
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);
        _rate.Read(Game, last);
        Assert.Null(_rate.Read(Game, last + 5000));     // stopped presenting

        long resumed = Frames(Game, last + 10_000, 5, 25);
        Assert.Null(_rate.Read(Game, resumed));          // only the first timestamp to measure from
        resumed = Frames(Game, resumed + 25, 8, 25);
        Assert.Equal(40, _rate.Read(Game, resumed)!.Value, 6);
    }

    [Fact]
    public void Frames_of_other_processes_are_not_counted()
    {
        long last = Started();
        Frames(Other, last + 1, 100, 1);
        last = Frames(Game, last + 10, 10, 10);
        Assert.Equal(100, _rate.Read(Game, last)!.Value, 6);
    }

    [Fact]
    public void Switching_the_foreground_starts_over()
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);
        Assert.NotNull(_rate.Read(Game, last));

        Assert.Null(_rate.Read(Other, last + 1));
        Frames(Game, last + 10, 10, 10);                 // the old process's frames no longer count
        long other = Frames(Other, last + 10, 4, 50);
        Assert.Null(_rate.Read(Other, other));
        other = Frames(Other, other + 50, 4, 50);
        Assert.Equal(20, _rate.Read(Other, other)!.Value, 6);
    }

    [Fact]
    public void No_foreground_window_keeps_counting_the_current_process()
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);
        Assert.Equal(100, _rate.Read(0, last)!.Value, 6);
        last = Frames(Game, last + 20, 10, 20);
        Assert.Equal(50, _rate.Read(0, last)!.Value, 6);
        Assert.Equal(50, _rate.Read(Game, last)!.Value, 6);     // and coming back to it is not a switch
    }

    [Theory]
    [InlineData(0)]         // a frame with the same timestamp as the last one read
    [InlineData(-30)]       // or an earlier one: ETW buffers are per CPU
    public void A_frame_that_does_not_move_time_forward_keeps_the_last_rate(long offset)
    {
        long last = Started();
        last = Frames(Game, last + 10, 10, 10);
        Assert.Equal(100, _rate.Read(Game, last)!.Value, 6);

        _rate.Present(Game, last + offset);
        Assert.Equal(100, _rate.Read(Game, last)!.Value, 6);     // not infinite, not negative
    }

    [Fact]
    public void Steady_60_fps_delivered_in_bursts_reads_60()
    {
        var rate = new FrameRate(1_000_000);    // microseconds
        const long Frame = 16_667;
        long t = 5_000_000;
        Assert.Null(rate.Read(Game, t));

        // One second of frames, delivered all at once, then read at uneven moments.
        double? last = null;
        for (int second = 0; second < 5; second++)
        {
            for (int i = 0; i < 60; i++) rate.Present(Game, t + i * Frame);
            t += 60 * Frame;
            last = rate.Read(Game, t + second * 137_000);
        }
        Assert.Equal(1_000_000.0 / Frame, last!.Value, 6);
    }
}
