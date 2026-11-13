using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class DashboardDeadlineTests
{
    [Fact]
    public async Task AuthenticatedServerThatWithholdsResultCannotHoldDiscoveryOpen()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var requested = false;
        var server = ServeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DashboardCatalog.GetAsync(address, "test-only", stop.Token, TimeSpan.FromSeconds(1)));
        Assert.False(stop.IsCancellationRequested);
        Assert.True(requested);
        await stop.CancelAsync();
        try { await server; } catch (OperationCanceledException) { }

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? key = null;
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Split(':', 2)[1].Trim();
            Assert.NotNull(key);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
            await SendAsync("auth_required");
            var buffer = new byte[4096];
            await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
            await SendAsync("auth_ok");
            var message = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
            using var payload = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
            requested = payload.RootElement.GetProperty("type").GetString() == "get_panels";
            await Task.Delay(Timeout.Infinite, stop.Token);

            Task SendAsync(string type) => socket.SendAsync(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type })).AsMemory(), WebSocketMessageType.Text, true, stop.Token).AsTask();
        }
    }
}
