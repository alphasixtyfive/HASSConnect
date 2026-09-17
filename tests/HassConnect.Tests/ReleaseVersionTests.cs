using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.10.2")]
    [InlineData("255.255.65535")]
    public void AcceptsStableThreePartVersions(string value)
    {
        Assert.True(ReleaseVersion.TryParse(value, out var version));
        Assert.Equal(value, version.ToString(3));
        Assert.True(ReleaseVersion.TryParseTag("v" + value, out var tag));
        Assert.Equal(version, tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3+build")]
    [InlineData("v1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n")]
    [InlineData("999999999999.2.3")]
    public void RejectsUnsupportedFormats(string? value) => Assert.False(ReleaseVersion.TryParse(value, out _));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("vv1.2.3")]
    [InlineData("V1.2.3")]
    public void TagsRequireOneLowercasePrefix(string tag) => Assert.False(ReleaseVersion.TryParseTag(tag, out _));

    [Fact]
    public void ProductVersionIsAStableReleaseVersion() => Assert.True(ReleaseVersion.TryParse(ProductInfo.Version, out _));
}
