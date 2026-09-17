using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public sealed class HaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;
    private static readonly JsonSerializerOptions WebhookJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public HaClient(Uri server, string token, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Paste the complete access token without spaces or line breaks.", nameof(token));
        _token = token;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = server,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, "api/config");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array ||
            components.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("The server did not return a Home Assistant configuration.");
        if (!components.EnumerateArray().Any(x => x.GetString() == "mobile_app"))
            throw new InvalidOperationException("Enable the Mobile App integration in Home Assistant first.");
    }

    public async Task<string> RegisterAsync(Settings settings, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, "api/mobile_app/registrations");
        request.Content = JsonContent.Create(new
        {
            device_id = settings.DeviceId,
            app_id = ProductInfo.HomeAssistantAppId,
            app_name = ProductInfo.Name,
            app_version = ProductInfo.Version,
            device_name = settings.DeviceName,
            manufacturer = "Microsoft",
            model = "Windows PC",
            os_name = "Windows",
            os_version = Environment.OSVersion.Version.ToString(),
            supports_encryption = false,
            app_data = new { }
        });
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("webhook_id").GetString() ?? throw new InvalidDataException("Registration returned no webhook.");
    }

    public async Task<JsonElement> WebhookAsync(string webhook, string type, object data, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/webhook/" + Uri.EscapeDataString(webhook), new { type, data }, WebhookJson, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
            throw new RegistrationRemovedException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public Task<JsonElement> RegisterSensorAsync(string webhook, SensorDefinition sensor, object? state, bool enabled, CancellationToken ct) =>
        WebhookAsync(webhook, "register_sensor", new SensorPayload(sensor.Id, sensor.Type, state)
        {
            Name = sensor.Name, Icon = sensor.Icon, Unit = sensor.Unit,
            DeviceClass = sensor.DeviceClass, Disabled = !enabled
        }, ct);

    public Task<JsonElement> UpdateSensorsAsync(string webhook, IEnumerable<SensorReading> readings, CancellationToken ct) =>
        WebhookAsync(webhook, "update_sensor_states", readings.Select(reading =>
        {
            var sensor = SensorDefinition.Available.Single(sensor => sensor.Id == reading.Id);
            return new SensorPayload(reading.Id, sensor.Type, reading.Value) { Icon = sensor.Icon };
        }), ct);

    public Task<JsonElement> SetNotificationCapabilityAsync(string webhook, Settings settings, bool enabled, CancellationToken ct) =>
        WebhookAsync(webhook, "update_registration", new
        {
            app_version = ProductInfo.Version, device_name = settings.DeviceName, manufacturer = "Microsoft", model = "Windows PC",
            os_version = Environment.OSVersion.Version.ToString(), app_data = new { push_websocket_channel = enabled }
        }, ct);

    public Task<JsonElement> SendNotificationActionAsync(string webhook, string deviceId, string action, CancellationToken ct, string? tag = null, string? notificationId = null, JsonElement? actionData = null) =>
        WebhookAsync(webhook, "fire_event", new
        {
            event_type = "mobile_app_notification_action", event_data = new { action, device_id = deviceId, tag, notification_id = notificationId, action_data = actionData }
        }, ct);

    private sealed record SensorPayload(
        [property: JsonPropertyName("unique_id")] string UniqueId,
        string Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? State)
    {
        public string? Name { get; init; }
        public string? Icon { get; init; }
        [JsonPropertyName("unit_of_measurement")]
        public string? Unit { get; init; }
        [JsonPropertyName("device_class")]
        public string? DeviceClass { get; init; }
        public bool? Disabled { get; init; }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class RegistrationRemovedException() : Exception("This device was removed from Home Assistant. Reconnect to register it again.");
