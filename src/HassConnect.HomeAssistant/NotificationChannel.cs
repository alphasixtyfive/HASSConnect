using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public static class NotificationChannel
{
    public static async Task ReceiveAsync(Uri server, Credentials credentials, Func<NotificationMessage, CancellationToken, Task> deliver,
        Action connected, Action<Exception> rejected, CancellationToken ct)
    {
        await using var socket = await HaWebSocket.ConnectAsync(server, credentials.AccessToken, ct);
        await socket.SendAsync(new { id = 1, type = "mobile_app/push_notification_channel", webhook_id = credentials.WebhookId, support_confirm = true }, ct);
        var result = await socket.ReadAsync(ct);
        if (Type(result) != "result" || !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Home Assistant could not start the notification channel.");
        connected();
        var sequence = 1;
        while (!ct.IsCancellationRequested)
        {
            var packet = await socket.ReadAsync(ct);
            if (Type(packet) != "event" || !packet.TryGetProperty("id", out var id) ||
                !id.TryGetInt32(out var channelId) || channelId != 1 ||
                !packet.TryGetProperty("event", out var payload) || payload.ValueKind != JsonValueKind.Object)
                continue;
            await ProcessEventAsync(payload, deliver, rejected,
                confirmation => socket.SendAsync(new
                {
                    id = ++sequence,
                    type = "mobile_app/push_notification_confirm",
                    webhook_id = credentials.WebhookId,
                    confirm_id = confirmation
                }, ct), ct);
        }
    }

    internal static async Task ProcessEventAsync(JsonElement payload,
        Func<NotificationMessage, CancellationToken, Task> deliver, Action<Exception> rejected,
        Func<string, Task> confirm, CancellationToken ct)
    {
        var confirmation = NotificationMessage.ReadConfirmationId(payload);
        try
        {
            await deliver(NotificationMessage.Parse(payload), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { rejected(ex); }

        // Confirmation means the desktop client received the message. A malformed
        // payload or failed Windows action must not poison the push channel.
        if (confirmation is not null) await confirm(confirmation);
    }

    private static string? Type(JsonElement packet) => HaWebSocket.MessageType(packet);
}
