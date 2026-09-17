using System.Net.Http.Headers;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public sealed class NotificationImageClient : IDisposable
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private readonly HttpClient _http;

    public NotificationImageClient(HttpMessageHandler? handler = null) => _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<byte[]> DownloadAsync(Uri server, string token, string image, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ct = timeout.Token;
        if (!Uri.TryCreate(server, image, out var address) || !string.IsNullOrEmpty(address.UserInfo) ||
            address.Scheme != "https" && !(address.Scheme == "http" && ServerAddress.SameOrigin(server, address)))
            throw new InvalidDataException("Notification image URL is unsupported.");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (ServerAddress.SameOrigin(server, address)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Headers.Location is not null || response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("Notification image was redirected or too large.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("Notification image is too large.");
            bytes.Write(buffer, 0, count);
        }
        var content = bytes.ToArray();
        if (!IsSupportedImage(content)) throw new InvalidDataException("Notification image must be PNG, JPEG or GIF.");
        return content;
    }

    public static bool IsSupportedImage(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        bytes.StartsWith(new byte[] { 255, 216, 255 }) || bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8);

    public void Dispose() => _http.Dispose();
}
