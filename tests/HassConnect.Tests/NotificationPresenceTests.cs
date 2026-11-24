using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NotificationPresenceTests
{
    [Theory]
    [InlineData("on", false, 0, true)]
    [InlineData("dimmed", false, 299, true)]
    [InlineData("unknown", false, 120, true)]
    [InlineData("off", false, 0, false)]
    [InlineData("on", true, 0, false)]
    [InlineData("on", false, 300, false)]
    [InlineData("on", false, -1, false)]
    public void RequiresAnUnlockedAndRecentlyActiveSession(string display, bool locked, long idleSeconds, bool expected) =>
        Assert.Equal(expected, NotificationPresence.IsActive(display, locked, idleSeconds));
}
