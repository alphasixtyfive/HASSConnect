using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace HassConnect.HomeAssistant;

internal sealed class HaWebSocket : IAsyncDisposable
{
    private const int MaximumMessageBytes = 128 * 1024;
    private readonly ClientWebSocket _socket;

    private HaWebSocket(ClientWebSocket socket) => _socket = socket;

    public static async Task<HaWebSocket> ConnectAsync(Uri server, string token, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        var connection = new HaWebSocket(socket);

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(WebSocketUri(server), handshake.Token);
            if (MessageType(await connection.ReadAsync(handshake.Token)) != "auth_required")
                throw new InvalidDataException("Unexpected Home Assistant handshake.");

            await connection.SendAsync(new { type = "auth", access_token = token }, handshake.Token);
            if (MessageType(await connection.ReadAsync(handshake.Token)) != "auth_ok")
                throw new HttpRequestException("Home Assistant rejected the access token.", null, HttpStatusCode.Unauthorized);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task SendAsync(object payload, CancellationToken ct) =>
        _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();

    public async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult chunk;
        do
        {
            chunk = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (chunk.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("Home Assistant closed the connection.");
            if (bytes.Length + chunk.Count > MaximumMessageBytes)
                throw new InvalidDataException("Home Assistant sent an unexpectedly large message.");
            bytes.Write(buffer, 0, chunk.Count);
        } while (!chunk.EndOfMessage);

        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    public async Task<JsonElement> ReadResultAsync(int id, CancellationToken ct)
    {
        while (true)
        {
            var message = await ReadAsync(ct);
            if (MessageType(message) != "result" || !message.TryGetProperty("id", out var messageId) || messageId.GetInt32() != id)
                continue;
            if (!message.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Home Assistant rejected a WebSocket request.");
            return message.TryGetProperty("result", out var result) ? result : default;
        }
    }

    public static string? MessageType(JsonElement message) =>
        message.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static Uri WebSocketUri(Uri server) =>
        new UriBuilder(new Uri(server, "api/websocket")) { Scheme = server.Scheme == "https" ? "wss" : "ws" }.Uri;

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (WebSocketException) { }
        }
        _socket.Dispose();
    }
}
