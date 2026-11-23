namespace SensorPanel.Sensors;

/// <summary>
/// The arithmetic behind <see cref="FrameCounter"/>, apart from ETW and the foreground window:
/// fed every Present event on the ETW thread, and read once per tick on the panel's thread.
/// A timestamp of 0 means "none yet", which QPC never gives.
/// </summary>
/// <param name="frequency">Ticks per second of the timestamps (QPC: Stopwatch.Frequency).</param>
internal sealed class FrameRate(long frequency)
{
    private readonly Lock _lock = new();
    private volatile int _pid;          // the process whose presents are counted
    private long _count, _lastStamp;    // presents since _pid was set, and the time of the last one
    private long _readCount, _readStamp;  // the same two at the previous Read that had frames
    private double? _rate;

    /// <summary>One Present event: by process <paramref name="pid"/>, at <paramref name="stamp"/>.</summary>
    public void Present(int pid, long stamp)
    {
        if (pid != _pid) return;    // most events are other processes': skip them without the lock
        lock (_lock)
        {
            if (pid != _pid) return;    // Read switched process since the check above: not one of its frames
            _count++;
            _lastStamp = stamp;
        }
    }

    /// <summary>
    /// Frames per second of <paramref name="foregroundPid"/> at time <paramref name="now"/>. The rate
    /// is measured between the events' own timestamps, not between calls: ETW delivers events in
    /// bursts, up to FlushTimer late (1 s, set in FrameCounter.Properties; keep it well under the 3 s
    /// below), so a call can find no new frames. It then returns the
    /// previous rate, until nothing has arrived for 3 s, which means the process stopped presenting: null.
    /// When the foreground process changes, counting starts over: null for that call, and for the
    /// next, which only takes the first timestamp to measure from. With no foreground window at all
    /// (pid 0, briefly, while switching windows), the current process goes on being counted.
    /// </summary>
    public double? Read(int foregroundPid, long now)
    {
        lock (_lock)
        {
            if (foregroundPid != _pid && foregroundPid != 0)
            {
                _pid = foregroundPid;
                (_count, _lastStamp, _readCount, _readStamp, _rate) = (0, 0, 0, 0, null);
                return null;
            }
            if (_count > _readCount)
            {
                if (_readStamp != 0 && _lastStamp > _readStamp)
                    _rate = (_count - _readCount) * (double)frequency / (_lastStamp - _readStamp);
                (_readCount, _readStamp) = (_count, _lastStamp);
            }
            else if (now - _lastStamp > 3 * frequency)
                (_rate, _readStamp) = (null, 0);    // the gap is not a frame time either
            return _rate;
        }
    }
}
