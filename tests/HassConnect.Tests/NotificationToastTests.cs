using System.Xml.Linq;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NotificationToastTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTitleShowsOnlyMessage(string? title)
    {
        var toast = XDocument.Parse(NotificationToast.Create(new(title, "Motion detected", null, [], null), true, null, []));
        Assert.Equal("Motion detected", Assert.Single(toast.Descendants("text")).Value);
    }

    [Fact]
    public void PreservesEmojiAndEscapesMessageMarkup()
    {
        var message = new NotificationMessage("Door 🔔", "<outside> & ready\u0001", null, [], null);
        var toast = XDocument.Parse(NotificationToast.Create(message, true, null, []));
        var text = toast.Descendants("text").Select(element => element.Value).ToArray();
        Assert.Equal(["Door 🔔", "<outside> & ready"], text);
        Assert.Empty(toast.Descendants("outside"));
    }

    [Fact]
    public void SilentImageNotificationKeepsOpaqueButtonArguments()
    {
        var message = new NotificationMessage("Test", "Message", null, [], null);
        const string token = "0123456789abcdef0123456789abcdef";
        var toast = XDocument.Parse(NotificationToast.Create(message, false, "file:///C:/cache/image.png", [("Acknowledge & close", token)]));
        Assert.Null(toast.Root!.Attribute("activationType"));
        Assert.Equal("open", toast.Root.Attribute("launch")?.Value);
        Assert.Equal("true", toast.Descendants("audio").Single().Attribute("silent")?.Value);
        Assert.Equal("file:///C:/cache/image.png", toast.Descendants("image").Single().Attribute("src")?.Value);
        var action = toast.Descendants("action").Single();
        Assert.Equal(token, action.Attribute("arguments")?.Value);
        Assert.Equal("foreground", action.Attribute("activationType")?.Value);
        Assert.Equal("Acknowledge & close", action.Attribute("content")?.Value);
    }

    [Fact]
    public void KeepsHeroImageAndTwoButtonsWithoutDuplicatingSenderIcon()
    {
        var toast = XDocument.Parse(NotificationToast.Create(new("Test", "Message", null, [], null), true,
            "file:///C:/cache/banner.png",
            [("Confirm", "0123456789abcdef0123456789abcdef"), ("Later", "abcdef0123456789abcdef0123456789")]));
        var images = toast.Descendants("image").ToArray();
        Assert.Single(images);
        Assert.Equal("hero", images[0].Attribute("placement")?.Value);
        var actions = toast.Descendants("action").ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Null(toast.Root!.Attribute("protocolActivationTargetApplicationPfn"));
        Assert.All(actions, action => Assert.Null(action.Attribute("protocolActivationTargetApplicationPfn")));
        Assert.NotEqual(actions[0].Attribute("arguments")?.Value, actions[1].Attribute("arguments")?.Value);
        Assert.All(actions, action => Assert.True(Guid.TryParseExact(action.Attribute("arguments")!.Value, "N", out _)));
    }

    [Fact]
    public void PersistentNotificationsUseTheWindowsReminderScenario()
    {
        var message = new NotificationMessage("Reminder", "Check the oven", null, [], null)
        {
            Persistent = true
        };

        var toast = XDocument.Parse(NotificationToast.Create(message, true, null, []));

        Assert.Equal("long", toast.Root!.Attribute("duration")?.Value);
        Assert.Equal("reminder", toast.Root.Attribute("scenario")?.Value);
    }

    [Fact]
    public void BodyLinkUsesAnOpaqueActivationToken()
    {
        const string token = "0123456789abcdef0123456789abcdef";

        var toast = XDocument.Parse(NotificationToast.Create(
            new("Camera", "Motion detected", null, [], null), true, null, [], token));

        Assert.Equal(token, toast.Root!.Attribute("launch")?.Value);
        Assert.Null(toast.Root.Attribute("activationType"));
    }
}
