using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NotificationContextTests
{
    [Fact]
    public void ContextSurvivesPayloadDisposalAndAlertsHaveDifferentIds()
    {
        NotificationMessage first;
        NotificationMessage second;
        using (var json = JsonDocument.Parse("""{"message":"Motion","data":{"tag":"door","action_data":{"event_id":"123"}}}"""))
        {
            first = NotificationMessage.Parse(json.RootElement);
            second = NotificationMessage.Parse(json.RootElement);
        }
        Assert.Equal("door", first.Tag);
        Assert.Equal("123", first.ActionData!.Value.GetProperty("event_id").GetString());
        Assert.NotEqual(first.NotificationId, second.NotificationId);
    }

    [Fact]
    public void ClearRequiresTag()
    {
        using var missing = JsonDocument.Parse("""{"message":"clear_notification"}""");
        Assert.Throws<InvalidDataException>(() => NotificationMessage.Parse(missing.RootElement));
        using var tagged = JsonDocument.Parse("""{"message":"clear_notification","data":{"tag":"door"}}""");
        Assert.True(NotificationMessage.Parse(tagged.RootElement).IsClear);
    }

    [Theory]
    [InlineData("/dashboard-cameras/kitchen", "https://ha.example/dashboard-cameras/kitchen")]
    [InlineData("https://camera.example/view", "https://camera.example/view")]
    public void ResolvesWebLinks(string input, string expected) =>
        Assert.Equal(expected, NotificationLink.Resolve(new Uri("https://ha.example/"), input).AbsoluteUri);

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("hassconnect://action/test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("//other.example/view")]
    [InlineData("relative/path")]
    public void RejectsNonWebLinksAndAmbiguousPaths(string input) =>
        Assert.Throws<InvalidDataException>(() => NotificationLink.Resolve(new Uri("https://ha.example/"), input));
}
