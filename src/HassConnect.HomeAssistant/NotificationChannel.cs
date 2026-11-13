using System.Text.Json;
using System.Threading.Channels;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public static class NotificationChannel
{
    public static async Task ReceiveAsync(Uri server, Credentials credentials, Func<NotificationMessage, CancellationToken, Task> deliver,
        Action connected, Action<Exception> rejected, CancellationToken ct)
    {
        await using var socket = await HaWebSocket.ConnectAsync(server, credentials.AccessToken, ct);
        using (var subscription = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            subscription.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.SendAsync(new { id = 1, type = "mobile_app/push_notification_channel", webhook_id = credentials.WebhookId, support_confirm = true }, subscription.Token);
            await socket.ReadResultAsync(1, subscription.Token, registrationRequest: true);
        }
        connected();
        var sequence = 1;
        await ReceiveMessagesAsync(socket.ReadAsync, deliver, rejected,
            async (confirmation, token) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(new
                {
                    id = ++sequence, type = "mobile_app/push_notification_confirm",
                    webhook_id = credentials.WebhookId, confirm_id = confirmation
                }, deadline.Token);
            }, ct);
    }

    internal static async Task ReceiveMessagesAsync(Func<CancellationToken, Task<JsonElement>> read,
        Func<NotificationMessage, CancellationToken, Task> deliver, Action<Exception> rejected,
        Func<string, CancellationToken, Task> confirm, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = Channel.CreateBounded<NotificationMessage>(new BoundedChannelOptions(32)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var worker = DeliverQueuedAsync();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await read(stop.Token);
                if (Type(packet) == "result")
                {
                    HaWebSocket.EnsureSuccess(packet);
                    continue;
                }
                if (Type(packet) != "event" || !packet.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var channelId) || channelId != 1 ||
                    !packet.TryGetProperty("event", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    continue;
                await ProcessEventAsync(payload, (message, _) =>
                {
                    // Receipt does not imply execution. Reject overload rather than replaying
                    // old PC commands or letting image downloads block the socket reader.
                    if (!queue.Writer.TryWrite(message)) rejected(new InvalidOperationException("Notification queue is full."));
                    return Task.CompletedTask;
                }, rejected, value => confirm(value, stop.Token), stop.Token);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            await stop.CancelAsync();
            await worker;
        }

        async Task DeliverQueuedAsync()
        {
            try
            {
                await foreach (var message in queue.Reader.ReadAllAsync(stop.Token))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    try { await deliver(message, stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                    catch (Exception ex) { rejected(ex); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    internal static async Task ProcessEventAsync(JsonElement payload,
        Func<NotificationMessage, CancellationToken, Task> deliver, Action<Exception> rejected,
        Func<string, Task> confirm, CancellationToken ct)
    {
        var confirmation = NotificationMessage.ReadConfirmationId(payload);
        ct.ThrowIfCancellationRequested();
        // Confirm receipt before image loading or any PC action. A malformed payload
        // is still received and must not poison the channel.
        if (confirmation is not null) await confirm(confirmation);
        try
        {
            await deliver(NotificationMessage.Parse(payload), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { rejected(ex); }
    }

    private static string? Type(JsonElement packet) => HaWebSocket.MessageType(packet);
}
