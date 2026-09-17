using System.Net;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class NotificationImageTests
{
    private static readonly Uri Server = new("https://home.example/");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1];

    [Fact]
    public async Task RelativeHomeAssistantImageReceivesToken()
    {
        using var handler = new ImageHandler(Png);
        using var client = new NotificationImageClient(handler);
        await client.DownloadAsync(Server, "private-token", "/api/camera_proxy/camera.front", CancellationToken.None);
        Assert.Equal("Bearer private-token", handler.Authorization);
        Assert.Equal("https://home.example/api/camera_proxy/camera.front", handler.Address?.AbsoluteUri);
    }

    [Fact]
    public async Task ExternalImageNeverReceivesHomeAssistantToken()
    {
        using var handler = new ImageHandler(Png);
        using var client = new NotificationImageClient(handler);
        await client.DownloadAsync(Server, "private-token", "https://images.example/photo.png", CancellationToken.None);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData("http://external.example/image.png")]
    [InlineData("file:///C:/private.png")]
    [InlineData("https://user:secret@home.example/image.png")]
    public async Task UnsupportedImageLocationsAreRejectedBeforeRequest(string address)
    {
        using var handler = new ImageHandler(Png);
        using var client = new NotificationImageClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Server, "secret", address, CancellationToken.None));
        Assert.Null(handler.Address);
    }

    [Fact]
    public async Task HtmlDisguisedAsImageIsRejected()
    {
        using var handler = new ImageHandler("<html>Sign in</html>"u8.ToArray());
        using var client = new NotificationImageClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Server, "secret", "/image.png", CancellationToken.None));
    }

    [Fact]
    public async Task OversizeImageIsRejected()
    {
        using var handler = new ImageHandler(new byte[5 * 1024 * 1024 + 1]);
        using var client = new NotificationImageClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Server, "secret", "/image.png", CancellationToken.None));
    }

    private sealed class ImageHandler(byte[] content) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public Uri? Address { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
