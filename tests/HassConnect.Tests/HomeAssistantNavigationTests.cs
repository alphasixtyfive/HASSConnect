using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class HomeAssistantNavigationTests
{
    private static readonly Uri Server = new("https://ha.example:8123/");

    [Theory]
    [InlineData(null, "https://ha.example:8123/")]
    [InlineData("", "https://ha.example:8123/")]
    [InlineData("dashboard-cameras", "https://ha.example:8123/dashboard-cameras")]
    [InlineData("/dashboard-cameras/front-door", "https://ha.example:8123/dashboard-cameras/front-door")]
    public void ResolvesPathsAgainstTheConfiguredServer(string? path, string expected) =>
        Assert.Equal(expected, HomeAssistantNavigation.Resolve(Server, path).AbsoluteUri);

    [Theory]
    [InlineData("//other.example/dashboard")]
    [InlineData("\\\\other.example\\dashboard")]
    public void RejectsPathsThatCouldChangeTheServer(string path) =>
        Assert.Throws<ArgumentException>(() => HomeAssistantNavigation.Resolve(Server, path));
}
