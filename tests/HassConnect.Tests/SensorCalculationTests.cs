using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class SensorCalculationTests
{
    [Fact]
    public void CpuUsageExcludesIdleFromKernelTime()
    {
        var before = new CpuUsageSample(100, 200, 50);
        var after = new CpuUsageSample(160, 280, 70);
        Assert.Equal(40d, after.UsageSince(before));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(100, 100, 0)]
    public void CpuUsageReportsAnEntirelyIdleInterval(ulong idle, ulong kernel, ulong user)
    {
        var usage = new CpuUsageSample(idle, kernel, user).UsageSince(default);
        if (kernel == 0) Assert.Null(usage);
        else Assert.Equal(0d, usage);
    }

    [Fact]
    public void CpuUsageRejectsResetOrInconsistentCounters()
    {
        Assert.Null(new CpuUsageSample(10, 20, 30).UsageSince(new(20, 30, 40)));
        Assert.Null(new CpuUsageSample(100, 20, 30).UsageSince(default));
    }

    [Fact]
    public void CpuUsageHandlesLargeCumulativeCountersWithoutOverflow()
    {
        var before = new CpuUsageSample(ulong.MaxValue - 100, ulong.MaxValue - 100, ulong.MaxValue - 100);
        var after = new CpuUsageSample(ulong.MaxValue - 50, ulong.MaxValue, ulong.MaxValue);
        Assert.Equal(75d, after.UsageSince(before));
    }
}
