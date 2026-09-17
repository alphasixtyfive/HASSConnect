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
             "data":{"image":"/local/door.jpg","actions":[{"action":"ACK_DOOR","title":"Acknowledge"}]}}
            """);
        var message = NotificationMessage.Parse(json.RootElement);
        Assert.Equal("Door", message.Title);
        Assert.Equal("Someone is outside", message.Message);
        Assert.Equal("/local/door.jpg", message.Image);
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
