using System.Net.WebSockets;
using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public sealed class NotificationChannel
{
    public async Task ReceiveAsync(Uri server, Credentials credentials, Func<NotificationMessage, CancellationToken, Task> deliver,
        Action connected, Action<Exception> rejected, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        var endpoint = new UriBuilder(new Uri(server, "api/websocket")) { Scheme = server.Scheme == "https" ? "wss" : "ws" }.Uri;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(endpoint, handshake.Token);
        var hello = await ReadAsync(socket, handshake.Token);
        if (Type(hello) != "auth_required") throw new InvalidDataException("Unexpected Home Assistant handshake.");
        await SendAsync(socket, new { type = "auth", access_token = credentials.AccessToken }, handshake.Token);
        var authenticated = await ReadAsync(socket, handshake.Token);
        if (Type(authenticated) != "auth_ok") throw new UnauthorizedAccessException("Home Assistant rejected the notification connection.");
        await SendAsync(socket, new { id = 1, type = "mobile_app/push_notification_channel", webhook_id = credentials.WebhookId, support_confirm = true }, handshake.Token);
        var result = await ReadAsync(socket, handshake.Token);
        if (Type(result) != "result" || !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Home Assistant could not start the notification channel.");
        connected();
        var sequence = 1;
        while (!ct.IsCancellationRequested)
        {
            var packet = await ReadAsync(socket, ct);
            if (Type(packet) != "event" || !packet.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
            NotificationMessage notification;
            try
            {
                notification = NotificationMessage.Parse(packet.GetProperty("event"));
                await deliver(notification, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { rejected(ex); continue; }
            if (notification.ConfirmationId is { } confirmation)
                await SendAsync(socket, new { id = ++sequence, type = "mobile_app/push_notification_confirm", webhook_id = credentials.WebhookId, confirm_id = confirmation }, ct);
        }
    }

    private static string? Type(JsonElement packet) => packet.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken ct) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload).AsMemory(), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReadAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult chunk;
        do
        {
            chunk = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (chunk.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Notification connection closed.");
            if (bytes.Length + chunk.Count > 128 * 1024) throw new InvalidDataException("Notification payload is too large.");
            bytes.Write(buffer, 0, chunk.Count);
        } while (!chunk.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }
}
