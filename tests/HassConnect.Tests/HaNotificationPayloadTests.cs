using System.Net;
using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class HaNotificationPayloadTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotificationOptInUpdatesRegistrationWithRequiredIdentity(bool enabled)
    {
        using var handler = new CaptureHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "secret", handler);
        await client.SetNotificationCapabilityAsync("webhook", new Settings { DeviceName = "Study PC" }, enabled, CancellationToken.None);
        Assert.Equal("update_registration", handler.Payload.GetProperty("type").GetString());
        var payload = handler.Payload.GetProperty("data");
        Assert.Equal("Study PC", payload.GetProperty("device_name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("app_version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("manufacturer").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("model").GetString()));
        Assert.Equal(enabled, payload.GetProperty("app_data").GetProperty("push_websocket_channel").GetBoolean());
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task ButtonSendsNamedMobileAppEventWithExactActionIdentifier()
    {
        using var handler = new CaptureHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "secret", handler);
        await client.SendNotificationActionAsync("webhook", "device", "ACK_STUDY", CancellationToken.None);
        Assert.Equal("fire_event", handler.Payload.GetProperty("type").GetString());
        var data = handler.Payload.GetProperty("data");
        Assert.Equal("mobile_app_notification_action", data.GetProperty("event_type").GetString());
        Assert.Equal("ACK_STUDY", data.GetProperty("event_data").GetProperty("action").GetString());
        Assert.Equal("device", data.GetProperty("event_data").GetProperty("device_id").GetString());
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task ButtonReturnsAlertContextWithoutReplacingActionOrDevice()
    {
        using var handler = new CaptureHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "secret", handler);
        using var context = JsonDocument.Parse("""{"event_id":"camera-42","action":"not-the-action"}""");
        await client.SendNotificationActionAsync("webhook", "device", "CONFIRM", CancellationToken.None,
            "kitchen", "alert-42", context.RootElement);
        var data = handler.Payload.GetProperty("data").GetProperty("event_data");
        Assert.Equal("CONFIRM", data.GetProperty("action").GetString());
        Assert.Equal("device", data.GetProperty("device_id").GetString());
        Assert.Equal("kitchen", data.GetProperty("tag").GetString());
        Assert.Equal("alert-42", data.GetProperty("notification_id").GetString());
        Assert.Equal("camera-42", data.GetProperty("action_data").GetProperty("event_id").GetString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonElement Payload { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Payload = document.RootElement.Clone();
            Authorization = request.Headers.Authorization?.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
