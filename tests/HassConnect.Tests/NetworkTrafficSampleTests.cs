using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NetworkTrafficSampleTests
{
    [Fact]
    public void ConvertsCounterDeltasToDecimalMegabitsPerSecond()
    {
        var before = new NetworkTrafficSample("ethernet", 100, 200, 1000);
        var after = new NetworkTrafficSample("ethernet", 2_000_100, 500_200, 3000);
        Assert.Equal(new NetworkTrafficRate(8, 2), after.RateSince(before));
    }

    [Fact]
    public void NoTrafficIsAValidZeroRate()
    {
        var sample = new NetworkTrafficSample("ethernet", 100, 200, 1000);
        Assert.Equal(new NetworkTrafficRate(0, 0), (sample with { TimestampMilliseconds = 2000 }).RateSince(sample));
    }

    [Theory]
    [InlineData("wifi", 200, 300, 2000)]
    [InlineData("ethernet", 99, 300, 2000)]
    [InlineData("ethernet", 200, 199, 2000)]
    [InlineData("ethernet", 200, 300, 1000)]
    [InlineData("ethernet", 200, 300, 999)]
    [InlineData("ethernet", -1, 300, 2000)]
    public void AdapterChangesCounterResetsAndInvalidTimeRequireNewBaseline(string adapter, long received, long sent, long time)
    {
        var before = new NetworkTrafficSample("ethernet", 100, 200, 1000);
        Assert.Null(new NetworkTrafficSample(adapter, received, sent, time).RateSince(before));
    }
}
