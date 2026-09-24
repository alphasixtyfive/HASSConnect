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

    public async Task<IReadOnlyList<QuickActionEntity>> GetQuickActionEntitiesAsync(CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, "api/states");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var entities = new List<QuickActionEntity>();
        try
        {
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                stream, cancellationToken: ct).ConfigureAwait(false))
            {
                if (ReadQuickActionEntity(item) is { } entity) entities.Add(entity);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The server did not return Home Assistant entities.", exception);
        }
        return entities.OrderBy(entity => entity.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entity => entity.EntityId, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetQuickActionStatesAsync(
        IReadOnlyCollection<string> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count > QuickActionPolicy.MaximumActions || entityIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choose up to eight configured entities.", nameof(entityIds));
        var remaining = new HashSet<string>(entityIds, StringComparer.Ordinal);
        var states = new Dictionary<string, string>(remaining.Count, StringComparer.Ordinal);
        if (remaining.Count == 0) return states;

        using var request = Authorized(HttpMethod.Get, "api/states");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                stream, cancellationToken: ct).ConfigureAwait(false))
            {
                var id = StringProperty(item, "entity_id");
                if (id is null || !remaining.Contains(id)) continue;
                var state = StringProperty(item, "state");
                if (state is null) continue;
                remaining.Remove(id);
                states.Add(id, state);
                if (remaining.Count == 0) break;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The server did not return Home Assistant entities.", exception);
        }
        return states;
    }

    public async Task InvokeQuickActionAsync(QuickActionDefinition action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var validated = QuickActionPolicy.Create(action.EntityId, action.Label, action.Icon, action.Operation);
        var dot = validated.EntityId.IndexOf('.');
        var domain = validated.EntityId[..dot];
        var service = validated.Operation == QuickActionPolicy.Run
            ? domain == "button" ? "press" : "turn_on"
            : validated.Operation;
        using var request = Authorized(HttpMethod.Post, $"api/services/{domain}/{service}");
        request.Content = JsonContent.Create(new { entity_id = validated.EntityId });
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string? StringProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static QuickActionEntity? ReadQuickActionEntity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = StringProperty(item, "entity_id");
        if (id is null) return null;

        var attributes = item.TryGetProperty("attributes", out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;
        int? supportedFeatures = attributes.ValueKind == JsonValueKind.Object &&
            attributes.TryGetProperty("supported_features", out var featureValue) &&
            featureValue.ValueKind == JsonValueKind.Number && featureValue.TryGetInt32(out var features) && features >= 0
                ? features : null;
        // Unknown cover capabilities cannot safely be presented as supported actions.
        if (id.StartsWith("cover.", StringComparison.Ordinal) && supportedFeatures is null) return null;
        var operations = QuickActionPolicy.AllowedOperations(id, supportedFeatures);
        if (operations.Count == 0) return null;

        var name = (StringProperty(attributes, "friendly_name") ?? id).Trim();
        if (name.Length > 40) name = name[..40].TrimEnd();
        if (name.Length == 0 || name.Any(char.IsControl)) name = id;
        var coverClass = id.StartsWith("cover.", StringComparison.Ordinal)
            ? StringProperty(attributes, "device_class") : null;
        var defaultIcon = coverClass switch
        {
            "gate" => "mdi:gate",
            "garage" => "mdi:garage",
            "door" => "mdi:door",
            _ => ""
        };
        var icon = StringProperty(attributes, "icon") ?? defaultIcon;
        var state = StringProperty(item, "state") ?? "unknown";
        try
        {
            var action = QuickActionPolicy.Create(id, name, icon, operations[0]);
            return new(action.EntityId, action.Label, action.Icon, state, supportedFeatures);
        }
        catch (ArgumentException)
        {
            // Home Assistant may report an unsupported icon; retain the entity with its domain icon.
            try
            {
                var action = QuickActionPolicy.Create(id, name, defaultIcon, operations[0]);
                return new(action.EntityId, action.Label, action.Icon, state, supportedFeatures);
            }
            catch (ArgumentException) { return null; }
        }
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
            Name = sensor.Name,
            Icon = sensor.Icon,
            Unit = sensor.Unit,
            DeviceClass = sensor.DeviceClass,
            Disabled = !enabled
        }, ct);

    public Task<JsonElement> UpdateSensorsAsync(string webhook, IEnumerable<SensorReading> readings, CancellationToken ct) =>
        UpdateSensorsAsync(webhook, readings,
            SensorDefinition.Available.ToDictionary(sensor => sensor.Id, StringComparer.Ordinal), ct);

    public Task<JsonElement> UpdateSensorsAsync(string webhook, IEnumerable<SensorReading> readings,
        IReadOnlyDictionary<string, SensorDefinition> definitions, CancellationToken ct) =>
        WebhookAsync(webhook, "update_sensor_states", readings.Select(reading =>
        {
            if (!definitions.TryGetValue(reading.Id, out var sensor))
                throw new InvalidOperationException($"Sensor definition is missing for {reading.Id}.");
            return new SensorPayload(reading.Id, sensor.Type, reading.Value) { Icon = sensor.Icon };
        }), ct);

    public Task<JsonElement> SetNotificationCapabilityAsync(string webhook, Settings settings, bool enabled, CancellationToken ct) =>
        WebhookAsync(webhook, "update_registration", new
        {
            app_version = ProductInfo.Version,
            device_name = settings.DeviceName,
            manufacturer = "Microsoft",
            model = "Windows PC",
            os_version = Environment.OSVersion.Version.ToString(),
            app_data = new { push_websocket_channel = enabled }
        }, ct);

    public Task<JsonElement> SendNotificationActionAsync(string webhook, string deviceId, string action, CancellationToken ct, string? tag = null, string? notificationId = null, JsonElement? actionData = null) =>
        WebhookAsync(webhook, "fire_event", new
        {
            event_type = "mobile_app_notification_action",
            event_data = new { action, device_id = deviceId, tag, notification_id = notificationId, action_data = actionData }
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
