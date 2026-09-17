using System.Net;
using System.Text;
using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class HaClientTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("token with spaces")]
    [InlineData("token\r\nheader")]
    [InlineData("token\u0000")]
    public void RejectsMalformedTokensBeforeMakingARequest(string token)
    {
        var error = Assert.Throws<ArgumentException>(() => new HaClient(new Uri("https://ha.example/"), token));
        Assert.StartsWith("Paste the complete access token without spaces or line breaks.", error.Message);
        Assert.Equal("token", error.ParamName);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"components\":null}")]
    [InlineData("{\"components\":[42]}")]
    public async Task RejectsResponsesFromAnUnrelatedOrMalformedServer(string body)
    {
        using var handler = new Handler(_ => Task.FromResult(Response(body)));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ValidateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RegistrationUsesStableDeviceIdentityAndDoesNotAdvertiseNotifications()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("/api/mobile_app/registrations", request.RequestUri?.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("stable-device", json.RootElement.GetProperty("device_id").GetString());
            Assert.Equal(ProductInfo.Version, json.RootElement.GetProperty("app_version").GetString());
            Assert.Equal(ProductInfo.Name, json.RootElement.GetProperty("app_name").GetString());
            Assert.False(json.RootElement.GetProperty("app_data").TryGetProperty("push_websocket_channel", out _));
            return Response("{\"webhook_id\":\"test-webhook\"}");
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        Assert.Equal("test-webhook", await client.RegisterAsync(new Settings { DeviceId = "stable-device" }, CancellationToken.None));
    }

    [Fact]
    public async Task WebhookDoesNotForwardAccessToken()
    {
        using var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("/api/webhook/test-webhook", request.RequestUri?.AbsolutePath);
            return Task.FromResult(Response("{}"));
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await client.WebhookAsync("test-webhook", "get_config", new { }, CancellationToken.None);
    }

    [Fact]
    public async Task RemovedRegistrationIsNotTreatedAsTransientConnectionFailure()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await Assert.ThrowsAsync<RegistrationRemovedException>(() => client.WebhookAsync("test-webhook", "get_config", new { }, CancellationToken.None));
    }

    [Fact]
    public async Task MissingMobileAppIntegrationGivesActionableError()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("{\"components\":[\"frontend\"]}")));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ValidateAsync(CancellationToken.None));
        Assert.Contains("Mobile App", error.Message);
    }

    [Fact]
    public async Task DisablingSensorUsesRegistrationApiAndKeepsItsIdentity()
    {
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("register_sensor", json.RootElement.GetProperty("type").GetString());
            var sensor = json.RootElement.GetProperty("data");
            Assert.Equal("uptime", sensor.GetProperty("unique_id").GetString());
            Assert.True(sensor.GetProperty("disabled").GetBoolean());
            Assert.Equal("duration", sensor.GetProperty("device_class").GetString());
            return Response("{}");
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await client.RegisterSensorAsync("test-webhook", SensorDefinition.Available.Single(x => x.Id == "uptime"), 10, false, CancellationToken.None);
    }

    [Theory]
    [InlineData("file:///C:/settings")]
    [InlineData("https://user:secret@ha.example")]
    [InlineData("https://ha.example/?token=secret")]
    [InlineData("https://ha.example/lovelace")]
    public void ServerAddressRejectsUnexpectedPathsAndEmbeddedCredentials(string address) =>
        Assert.Throws<ArgumentException>(() => ServerAddress.Parse(address));

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
