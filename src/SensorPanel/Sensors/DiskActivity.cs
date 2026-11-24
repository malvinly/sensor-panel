using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SensorPanel.Sensors;

/// <summary>
/// disk.read and disk.write: drive C:'s read and write rates in MB/s, from the byte counters Windows
/// keeps for every volume (IOCTL_DISK_PERFORMANCE). One call per tick, a few microseconds, and no
/// administrator rights. LibreHardwareMonitor's storage support would give the same rates, but it
/// opens every drive, reads their SMART data and needs elevation, all for two numbers.
/// </summary>
internal sealed class DiskActivity : IDisposable
{
    public const string ReadKey = "disk.read", WriteKey = "disk.write";

    /// <summary>
    /// Longer than any refresh: a gap like this between two reads is the panel having been paused
    /// (monitors off, sleep), and a rate averaged over it would be no reading of now.
    /// </summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);

    private readonly SafeFileHandle _volume;
    // DISK_PERFORMANCE (winioctl.h), 88 bytes; only its first two fields are read:
    // BytesRead (LARGE_INTEGER at offset 0) and BytesWritten (at offset 8), totals since boot.
    private readonly byte[] _counters = new byte[88];
    private (long Read, long Written, long At)? _last;
    private bool _failing;                      // the last call failed; logged once per run of failures

    private DiskActivity(SafeFileHandle volume) => _volume = volume;

    /// <summary>Drive C:, or null (logged) when Windows will not give its counters.</summary>
    public static DiskActivity? Open()
    {
        // No access rights asked for: the counters are metadata, readable without elevation.
        var volume = CreateFile(@"\\.\C:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!volume.IsInvalid) return new DiskActivity(volume);
        Log.Info($"Cannot open drive C: for its read and write counters (error {Marshal.GetLastPInvokeError()}); disk.read and disk.write show \"--\".");
        volume.Dispose();
        return null;
    }

    /// <summary>
    /// Writes both keys from the bytes moved since the last call. The first call, and the first after
    /// a pause, have nothing recent to compare with, so they only take the counters, and the keys
    /// stay absent until the next one.
    /// </summary>
    public void Read(Dictionary<string, double> snapshot)
    {
        long now = Stopwatch.GetTimestamp();
        if (!DeviceIoControl(_volume, IoctlDiskPerformance, IntPtr.Zero, 0, _counters, _counters.Length, out _, IntPtr.Zero))
        {
            if (!_failing) Log.Info($"Cannot read drive C:'s read and write counters (error {Marshal.GetLastPInvokeError()}); disk.read and disk.write show \"--\".");
            _failing = true;
            _last = null;
            snapshot.Remove(ReadKey);
            snapshot.Remove(WriteKey);
            return;
        }

        _failing = false;
        long read = BitConverter.ToInt64(_counters, 0), written = BitConverter.ToInt64(_counters, 8);
        if (_last is var (lastRead, lastWritten, at) && now > at && Stopwatch.GetElapsedTime(at, now) < MaxGap)
        {
            double seconds = Stopwatch.GetElapsedTime(at, now).TotalSeconds;
            // The counters only grow; a drop would mean they were reset, and a negative rate is no reading.
            snapshot[ReadKey] = Math.Max(0, read - lastRead) * SensorHub.BytesToMB / seconds;
            snapshot[WriteKey] = Math.Max(0, written - lastWritten) * SensorHub.BytesToMB / seconds;
        }
        else
        {
            snapshot.Remove(ReadKey);   // the first read, or the first after a pause: nothing to compare with yet
            snapshot.Remove(WriteKey);
        }
        _last = (read, written, now);
    }

    public void Dispose() => _volume.Dispose();

    private const uint IoctlDiskPerformance = 0x00070020, FileShareReadWrite = 3, OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
                                               byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
