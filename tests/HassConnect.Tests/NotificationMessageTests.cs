using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class NotificationMessageTests
{
    [Fact]
    public void ReadsImageActionsAndDeliveryConfirmation()
    {
        using var json = JsonDocument.Parse("""
            {"title":"Door","message":"Someone is outside","hass_confirm_id":"delivery-1",
             "data":{"image":"/local/door.jpg","url":"https://home.example/dashboard-cameras/front-door","actions":[{"action":"ACK_DOOR","title":"Acknowledge"}]}}
            """);
        var message = NotificationMessage.Parse(json.RootElement);
        Assert.Equal("Door", message.Title);
        Assert.Equal("Someone is outside", message.Message);
        Assert.Equal("/local/door.jpg", message.Image);
        Assert.Equal("https://home.example/dashboard-cameras/front-door", message.Url);
        Assert.Equal("delivery-1", message.ConfirmationId);
        Assert.Equal(new NotificationAction("ACK_DOOR", "Acknowledge"), Assert.Single(message.Actions));
    }

    [Fact]
    public void UnsupportedActionsAreIgnoredAndSupportedActionsAreBounded()
    {
        using var json = JsonDocument.Parse("""
            {"message":"Test","data":{"actions":[
              {"action":"URI","title":"Open","uri":"https://example.com"},
              {"action":"REPLY","title":"Reply"},
              {"action":"","title":"Missing identifier"},
              {"action":"ONE","title":"One"}, {"action":"TWO","title":"Two"},
              {"action":"THREE","title":"Three"}, {"action":"FOUR","title":"Four"}]}}
            """);
        var message = NotificationMessage.Parse(json.RootElement);
        Assert.Equal(["URI", "ONE", "TWO"], message.Actions.Select(action => action.Id));
    }

    [Fact]
    public void PlainMessageDoesNotRequireOptionalFields()
    {
        using var json = JsonDocument.Parse("""{"message":"Hello"}""");
        var message = NotificationMessage.Parse(json.RootElement);
        Assert.Null(message.Title);
        Assert.Null(message.Image);
        Assert.Null(message.ConfirmationId);
        Assert.Empty(message.Actions);
        Assert.False(message.Persistent);
        Assert.Null(message.Url);
    }

    [Fact]
    public void IgnoresBlankBodyUrl()
    {
        using var json = JsonDocument.Parse("""{"message":"Hello","data":{"url":"  "}}""");

        Assert.Null(NotificationMessage.Parse(json.RootElement).Url);
    }

    [Theory]
    [InlineData("camera.front_door", "true", "camera.front_door", true)]
    [InlineData("camera.front_gate", "false", "camera.front_gate", false)]
    [InlineData("camera.front_gate", "\"true\"", "camera.front_gate", false)]
    [InlineData("light.front_door", "true", null, false)]
    [InlineData("https://example.test/stream.m3u8", "true", null, false)]
    public void CameraFieldsAreIndependentOfTheSnapshotAndBrowserLink(string entity, string popup,
        string? expectedCamera, bool expectedPopup)
    {
        using var json = JsonDocument.Parse($$"""
            {"message":"Door","data":{"camera_entity_id":"{{entity}}","camera_popup":{{popup}},
            "image":"/local/door.jpg","url":"/dashboard-intercom/front-door"} }
            """);
        var message = NotificationMessage.Parse(json.RootElement);

        Assert.Equal(expectedCamera, message.Camera);
        Assert.Equal(expectedPopup, message.CameraPopup);
        Assert.Equal("/local/door.jpg", message.Image);
        Assert.Equal("/dashboard-intercom/front-door", message.Url);
    }

    [Fact]
    public void ReadsOnlyAnExplicitPersistentFlag()
    {
        using var persistent = JsonDocument.Parse("""{"message":"Check the oven","data":{"persistent":true}}""");
        using var text = JsonDocument.Parse("""{"message":"Check the oven","data":{"persistent":"true"}}""");

        Assert.True(NotificationMessage.Parse(persistent.RootElement).Persistent);
        Assert.False(NotificationMessage.Parse(text.RootElement).Persistent);
    }

    [Fact]
    public void LongActionIdentifiersAreRejectedRatherThanChanged()
    {
        var identifier = new string('a', 257);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            message = "Test",
            data = new { actions = new[] { new { action = identifier, title = "Action" } } }
        }));
        Assert.Empty(NotificationMessage.Parse(json.RootElement).Actions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":\" \"}")]
    [InlineData("{\"message\":42}")]
    public void RejectsMessagesThatCannotBeDisplayed(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidDataException>(() => NotificationMessage.Parse(json.RootElement));
    }
}
