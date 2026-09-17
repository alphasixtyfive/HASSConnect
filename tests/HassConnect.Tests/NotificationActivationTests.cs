using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NotificationActivationTests
{
    [Fact]
    public void OpensAppWithoutAnAction()
    {
        Assert.True(NotificationActivation.TryParse("open", out var action));
        Assert.Null(action);
    }

    [Fact]
    public void AcceptsOnlyAnOpaqueActionToken()
    {
        Assert.True(NotificationActivation.TryParse("0123456789ABCDEF0123456789ABCDEF", out var action));
        Assert.Equal("0123456789abcdef0123456789abcdef", action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("turn_off_lights")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("https://action/0123456789abcdef0123456789abcdef")]
    [InlineData("hassconnect://user@open")]
    [InlineData("hassconnect://open:123")]
    [InlineData("hassconnect://open/settings")]
    [InlineData("hassconnect://open?command=shutdown")]
    [InlineData("hassconnect://open#anything")]
    [InlineData("hassconnect://action/turn_off_lights")]
    [InlineData("hassconnect://action/0123456789abcdef0123456789abcdef/extra")]
    [InlineData("hassconnect://action/0123456789abcdef0123456789abcdef?extra=yes")]
    public void RejectsOtherActivationShapes(string value) =>
        Assert.False(NotificationActivation.TryParse(value, out _));
}
