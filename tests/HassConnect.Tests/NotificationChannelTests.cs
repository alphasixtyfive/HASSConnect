using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class NotificationChannelTests
{
    [Fact]
    public async Task SlowDeliveryDoesNotDelayLaterReceiptsAndKeepsOrder()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packets = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<string>();
        var confirmations = new List<string>();
        for (var i = 0; i < 3; i++)
            packets.Writer.TryWrite(JsonSerializer.SerializeToElement(new
            {
                type = "event", id = 1, @event = new { message = $"message-{i}", hass_confirm_id = $"confirm-{i}" }
            }));
        var running = NotificationChannel.ReceiveMessagesAsync(ct => packets.Reader.ReadAsync(ct).AsTask(),
            async (message, ct) =>
            {
                await release.Task.WaitAsync(ct);
                delivered.Add(message.Message);
                if (delivered.Count == 3) stop.Cancel();
            }, ex => throw new Exception("Unexpected delivery failure", ex),
            (confirmation, _) =>
            {
                confirmations.Add(confirmation);
                if (confirmations.Count == 3) allConfirmed.SetResult();
                return Task.CompletedTask;
            }, stop.Token);
        await allConfirmed.Task.WaitAsync(stop.Token);
        Assert.Empty(delivered);
        release.SetResult();
        try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.Equal(["message-0", "message-1", "message-2"], delivered);
    }

    [Fact]
    public async Task QueueOverloadIsBoundedAndReportedWithoutExecutingDroppedCommands()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packets = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        for (var i = 0; i < 100; i++)
            packets.Writer.TryWrite(JsonSerializer.SerializeToElement(new
            {
                type = "event", id = 1, @event = new { message = "message", hass_confirm_id = i.ToString() }
            }));
        var rejected = 0;
        var confirmed = 0;
        var delivered = 0;
        var running = NotificationChannel.ReceiveMessagesAsync(ct => packets.Reader.ReadAsync(ct).AsTask(),
            async (_, ct) => { Interlocked.Increment(ref delivered); await Task.Delay(Timeout.Infinite, ct); },
            _ => Interlocked.Increment(ref rejected),
            (_, _) => { if (++confirmed == 100) stop.Cancel(); return Task.CompletedTask; }, stop.Token);
        try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.Equal(100, confirmed);
        Assert.InRange(delivered, 0, 1);
        Assert.InRange(rejected, 67, 68);
    }

    [Fact]
    public void LostChannelRetriesButRemovedRegistrationStops()
    {
        using var packet = JsonDocument.Parse("""{"type":"result","id":2,"success":false,"error":{"code":"not_found"}}""");
        Assert.Throws<InvalidDataException>(() => HaWebSocket.EnsureSuccess(packet.RootElement));
        Assert.Throws<RegistrationRemovedException>(() => HaWebSocket.EnsureSuccess(packet.RootElement, registrationRequest: true));
    }

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
