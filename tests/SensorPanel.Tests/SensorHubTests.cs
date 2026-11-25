using SensorPanel.Sensors;

namespace SensorPanel.Tests;

/// <summary>The parts of SensorHub that decide what is polled and how often, without opening any hardware.</summary>
public class SensorHubTests
{
    [Theory]
    [InlineData(1000, null, 1)]         // not listed: every tick
    [InlineData(1000, 3000, 3)]
    [InlineData(1000, 2001, 3)]         // rounded up to whole ticks
    [InlineData(1000, 1000, 1)]
    [InlineData(1000, 500, 1)]          // never faster than the refresh
    [InlineData(1000, 1, 1)]
    [InlineData(100, 3_600_000, 36_000)]  // the largest interval at the shortest refresh
    public void TicksPerPoll_rounds_up_and_never_polls_faster_than_the_refresh(int refreshMs, int? gpuMs, int expected)
    {
        var intervals = gpuMs is int ms ? new Dictionary<string, int> { ["gpu"] = ms } : [];
        Assert.Equal(expected, SensorHub.TicksPerPoll(refreshMs, intervals, "gpu"));
    }

    [Fact]
    public void TicksPerPoll_uses_only_the_groups_own_interval()
    {
        var intervals = new Dictionary<string, int> { ["gpu"] = 5000 };
        Assert.Equal(1, SensorHub.TicksPerPoll(1000, intervals, "cpu"));
    }

    [Fact]
    public void A_computed_key_brings_the_keys_it_is_computed_from()
    {
        Assert.True(SensorHub.KeysToRead(["mem.total"]).SetEquals(["mem.total", "mem.used", "mem.free"]));
    }

    [Fact]
    public void Total_power_brings_the_cpu_and_gpu_power()
    {
        Assert.True(SensorHub.KeysToRead(["power.total"]).SetEquals(["power.total", "cpu.power", "gpu.power"]));
    }

    [Fact]
    public void Wall_power_brings_the_cpu_and_gpu_power()
    {
        Assert.True(SensorHub.KeysToRead(["power.wall"]).SetEquals(["power.wall", "cpu.power", "gpu.power"]));
    }

    [Fact]
    public void Wall_power_is_a_straight_line_through_the_total()
    {
        var wall = new WallPowerSettings { Scale = 1.1, Offset = 40 };
        Assert.Equal(40, wall.At(0), 9);
        Assert.Equal(480, wall.At(400), 9);
    }

    [Fact]
    public void Other_keys_are_read_as_they_are_once_each()
    {
        Assert.True(SensorHub.KeysToRead(["cpu.load", "cpu.load", "fps", "nope"]).SetEquals(["cpu.load", "fps", "nope"]));
        Assert.Empty(SensorHub.KeysToRead([]));
    }

    [Fact]
    public void Groups_are_the_first_segment_of_each_hardware_key()
    {
        Assert.Equal(["cpu", "gpu", "mem", "net", "fan"], SensorHub.Groups);
        Assert.All(SensorHub.HardwareKeys, k => Assert.Contains(k.Split('.')[0], SensorHub.Groups));
    }

    [Fact]
    public void Computed_keys_are_not_hardware_keys()
    {
        // ComputedKeys is a hand-kept list; a key in both would mean one of them is stale.
        Assert.Empty(SensorHub.ComputedKeys.Intersect(SensorHub.HardwareKeys));
        Assert.Equal(SensorHub.ComputedKeys.Length, SensorHub.ComputedKeys.Distinct().Count());
    }

    [Fact]
    public void Nvml_reads_the_same_gpu_keys_as_LibreHardwareMonitor()
    {
        // settings.json "lhmGpu" swaps one source for the other; a layout must not notice.
        var lhmGpuKeys = SensorHub.HardwareKeys.Where(k => k.StartsWith("gpu.")).Distinct().Order();
        Assert.Equal(lhmGpuKeys, Nvml.Keys.Order());
    }
}
