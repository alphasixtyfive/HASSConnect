using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class NotificationChannelTests
{
    [Fact]
    public async Task FailedDeliveryIsConfirmedAndDoesNotBlockNextMessage()
    {
        var delivered = new List<string>();
        var confirmations = new List<string>();
        var rejected = new List<Exception>();

        Task Deliver(NotificationMessage message, CancellationToken _)
        {
            delivered.Add(message.Message);
            if (message.Message == "command_lock") throw new InvalidOperationException("simulated failure");
            return Task.CompletedTask;
        }

        await ProcessAsync("command_lock", "first");
        await ProcessAsync("command_volume_mute", "second");

        Assert.Equal(["command_lock", "command_volume_mute"], delivered);
        Assert.Equal(["first", "second"], confirmations);
        Assert.Single(rejected);

        Task ProcessAsync(string message, string confirmation)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                message,
                hass_confirm_id = confirmation
            }));
            return NotificationChannel.ProcessEventAsync(document.RootElement.Clone(), Deliver,
                rejected.Add, value => { confirmations.Add(value); return Task.CompletedTask; },
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task MalformedCommandIsRejectedButStillConfirmed()
    {
        using var document = JsonDocument.Parse("""
            {
              "message": "command_volume_level",
              "hass_confirm_id": "invalid-volume",
              "data": { "volume_level": 101 }
            }
            """);
        var confirmed = new List<string>();
        Exception? failure = null;

        await NotificationChannel.ProcessEventAsync(document.RootElement,
            (_, _) => Task.CompletedTask, ex => failure = ex,
            value => { confirmed.Add(value); return Task.CompletedTask; }, CancellationToken.None);

        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(["invalid-volume"], confirmed);
    }
}
