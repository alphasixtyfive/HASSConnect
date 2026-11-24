using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

public sealed partial class HaClient : IDisposable
{
    private static readonly SocketsHttpHandler SharedTransport = new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
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
        _http = new(handler ?? SharedTransport, disposeHandler: handler is not null)
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

    public async Task<Uri> GetCameraStreamAsync(string entityId, CancellationToken ct)
    {
        if (!CameraEntityId.IsValid(entityId))
            throw new ArgumentException("Enter a camera entity ID, for example camera.front_door.", nameof(entityId));

        var server = _http.BaseAddress!;
        await using var socket = await HaWebSocket.ConnectAsync(server, _token, ct).ConfigureAwait(false);
        await socket.SendAsync(new { id = 1, type = "camera/stream", entity_id = entityId, format = "hls" }, ct)
            .ConfigureAwait(false);
        var result = await socket.ReadResultAsync(1, ct).ConfigureAwait(false);
        return ResolveCameraStream(server, StringProperty(result, "url"));
    }

    internal static Uri ResolveCameraStream(Uri server, string? path)
    {
        if (path is null || !CameraStreamPathPattern().IsMatch(path))
            throw new InvalidDataException("Home Assistant did not return a camera stream.");
        return new Uri(server, path);
    }

    [GeneratedRegex(@"\A/api/hls/[A-Za-z0-9_-]{12,128}/[A-Za-z0-9_-]{1,64}\.m3u8\z", RegexOptions.CultureInvariant)]
    private static partial Regex CameraStreamPathPattern();

    public async Task<IReadOnlyList<QuickActionEntity>> GetQuickActionEntitiesAsync(CancellationToken ct)
    {
        var entities = new List<QuickActionEntity>();
        await foreach (var item in ReadEntitiesAsync(ct).ConfigureAwait(false))
            if (ReadQuickActionEntity(item) is { } entity) entities.Add(entity);

        return entities.OrderBy(entity => entity.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entity => entity.EntityId, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, QuickActionState>> GetQuickActionStatesAsync(
        IReadOnlyCollection<string> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count > QuickActionPolicy.MaximumActions || entityIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Choose up to {QuickActionPolicy.MaximumActions} configured entities.", nameof(entityIds));
        var remaining = new HashSet<string>(entityIds, StringComparer.Ordinal);
        var states = new Dictionary<string, QuickActionState>(remaining.Count, StringComparer.Ordinal);
        if (remaining.Count == 0) return states;

        await foreach (var item in ReadEntitiesAsync(ct).ConfigureAwait(false))
        {
            var id = StringProperty(item, "entity_id");
            var state = StringProperty(item, "state");
            if (id is null || state is null || !remaining.Remove(id)) continue;
            states.Add(id, ReadQuickActionState(id, state, EntityAttributes(item)));
            if (remaining.Count == 0) break;
        }
        return states;
    }

    private async IAsyncEnumerable<JsonElement> ReadEntitiesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_http.Timeout);
        using var request = Authorized(HttpMethod.Get, "api/states");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var entities = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            bool hasNext;
            try { hasNext = await entities.MoveNextAsync().ConfigureAwait(false); }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The server did not return Home Assistant entities.", ex);
            }
            if (!hasNext) yield break;
            yield return entities.Current;
        }
    }

    public async Task InvokeQuickActionAsync(QuickActionDefinition action, CancellationToken ct, int? percentage = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var validated = QuickActionPolicy.Create(action.EntityId, action.Label, action.Icon, action.Operation);
        if (validated.Operation == QuickActionPolicy.ViewCamera)
            throw new ArgumentException("Camera tiles open the local viewer.", nameof(action));
        if (percentage is < 0 or > 100 || QuickActionPolicy.IsLevel(validated.Operation) != percentage.HasValue)
            throw new ArgumentException("Enter a percentage from 0 to 100 for a level action.", nameof(percentage));

        var dot = validated.EntityId.IndexOf('.');
        var domain = validated.EntityId[..dot];
        var entity = new { entity_id = validated.EntityId };
        (string Service, object Data) command = validated.Operation switch
        {
            QuickActionPolicy.Run => (domain == "button" ? "press" : "turn_on", entity),
            QuickActionPolicy.Brightness when percentage == 0 => ("turn_off", entity),
            QuickActionPolicy.Brightness => ("turn_on", new { entity.entity_id, brightness_pct = percentage }),
            QuickActionPolicy.Volume => ("volume_set", new { entity.entity_id, volume_level = percentage / 100d }),
            _ => (validated.Operation, entity)
        };
        using var request = Authorized(HttpMethod.Post, $"api/services/{domain}/{command.Service}");
        request.Content = JsonContent.Create(command.Data);
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

        var attributes = EntityAttributes(item);
        var supportedFeatures = NumberProperty(attributes, "supported_features");
        var supportsBrightness = id.StartsWith("light.", StringComparison.Ordinal) && SupportsBrightness(attributes);
        var operations = QuickActionPolicy.AllowedOperations(id, supportedFeatures ?? 0, supportsBrightness);
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
        try { return CreateEntity(icon); }
        catch (ArgumentException ex) when (ex.ParamName == "icon") { return CreateEntity(defaultIcon); }
        catch (ArgumentException) { return null; }

        QuickActionEntity CreateEntity(string selectedIcon)
        {
            var action = QuickActionPolicy.Create(id, name, selectedIcon, operations[0]);
            return new(action.EntityId, action.Label, action.Icon, state, supportedFeatures, supportsBrightness);
        }
    }

    private static JsonElement EntityAttributes(JsonElement item) =>
        item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
            ? attributes : default;

    private static int? NumberProperty(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool SupportsBrightness(JsonElement attributes) =>
        attributes.ValueKind == JsonValueKind.Object &&
        (attributes.TryGetProperty("supported_color_modes", out var modes) && modes.ValueKind == JsonValueKind.Array
            ? modes.EnumerateArray().Any(mode => mode.ValueKind == JsonValueKind.String &&
                mode.GetString() is "brightness" or "color_temp" or "hs" or "xy" or "rgb" or "rgbw" or "rgbww" or "white")
            : NumberProperty(attributes, "brightness") is >= 0 and <= 255);

    private static QuickActionState ReadQuickActionState(string id, string state, JsonElement attributes)
    {
        if (state is "unavailable" or "unknown") return new(state);

        if (id.StartsWith("light.", StringComparison.Ordinal) && SupportsBrightness(attributes))
        {
            var brightness = NumberProperty(attributes, "brightness");
            return new(state, Brightness: state == "off" ? 0 :
                brightness is >= 0 and <= 255 ? (int)Math.Round(brightness.Value * 100d / 255) : null);
        }
        if (id.StartsWith("media_player.", StringComparison.Ordinal) &&
            QuickActionPolicy.AllowedOperations(id, NumberProperty(attributes, "supported_features") ?? 0).Count != 0 &&
            attributes.TryGetProperty("volume_level", out var volume) && volume.ValueKind == JsonValueKind.Number &&
            volume.TryGetDouble(out var level) && level is >= 0 and <= 1)
            return new(state, Volume: (int)Math.Round(level * 100));
        return new(state);
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
