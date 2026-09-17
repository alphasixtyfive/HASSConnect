using System.ComponentModel;
using System.Net;
using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.App.Services;

internal enum SessionState { Disconnected, Connecting, Connected, Paused, Reconnecting, Failed, SignInRequired, DeviceRemoved }

internal sealed class SensorSession : IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly DisplayStateMonitor _display;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private readonly Task _notificationStartup;
    private readonly NotificationService _notifications;
    private HaClient? _client;
    private Credentials? _credentials;
    private Settings _settings;
    private bool _suspended;
    private readonly Dictionary<string, object?> _values = [];
    private readonly HashSet<string> _sensorErrors = [];

    public event Action? Changed;
    public Settings Settings => _settings;
    public SessionState State { get; private set; } = SessionState.Disconnected;
    public string Status => State switch
    {
        SessionState.Connecting => "Connecting",
        SessionState.Connected => "Connected",
        SessionState.Paused => "Paused",
        SessionState.Reconnecting => "Reconnecting",
        SessionState.Failed => "Connection failed",
        SessionState.SignInRequired => "Sign-in required",
        SessionState.DeviceRemoved => "Device removed",
        _ => "Not connected"
    };
    public string Detail { get; private set; } = "Connect to Home Assistant to start sharing sensors.";
    public DateTimeOffset? LastReported { get; private set; }
    public bool Connected { get; private set; }
    public bool Paused => !_settings.ShareSensors;
    public string NotificationStatus => _notifications.Status;
    public bool NotificationsConnected => _notifications.Connected;
    public bool NotificationsSupported => _notifications.Supported;
    public bool IsRegistered => _credentials?.WebhookId is not null;
    internal string? GetSavedAccessToken() => _credentials?.AccessToken;
    public IReadOnlyDictionary<string, object?> Values => new Dictionary<string, object?>(_values);

    public SensorSession(SettingsStore store, nint window, NotificationService notifications)
    {
        _notifications = notifications;
        _store = store;
        _settings = store.LoadSettings();
        _credentials = store.LoadCredentials();
        _display = new DisplayStateMonitor(window);
        _store.Save(_settings);
        if (_credentials?.WebhookId is not null && !string.IsNullOrWhiteSpace(_settings.ServerUrl))
            _client = new HaClient(ServerAddress.Parse(_settings.ServerUrl), _credentials.AccessToken);
        _notifications.Changed += () => Changed?.Invoke();
        _loop = RunAsync(_lifetime.Token);
        _notificationStartup = StartNotificationsAsync(_lifetime.Token);
    }

    public async Task ConnectAsync(string url, string deviceName, string token)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("Enter a device name.", nameof(deviceName));
        var server = ServerAddress.Parse(url);
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (_credentials?.WebhookId is not null && !string.IsNullOrEmpty(_settings.ServerUrl) &&
                !ServerAddress.SameOrigin(server, ServerAddress.Parse(_settings.ServerUrl)))
                throw new InvalidOperationException("This installation is paired with another server. Remove its device and reset the connection before changing servers.");
            var credential = string.IsNullOrWhiteSpace(token) ? _credentials?.AccessToken : token.Trim();
            if (string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Enter a Home Assistant access token.", nameof(token));
            SetStatus(SessionState.Connecting, "Checking the connection…", false);
            var next = _settings with { ServerUrl = server.AbsoluteUri, DeviceName = deviceName.Trim() };
            var client = new HaClient(server, credential);
            try
            {
                await client.ValidateAsync(_lifetime.Token);
                var webhook = _credentials?.WebhookId;
                if (webhook is null) webhook = await client.RegisterAsync(next, _lifetime.Token);
                else
                {
                    try { await client.WebhookAsync(webhook, "get_config", new { }, _lifetime.Token); }
                    catch (RegistrationRemovedException)
                    {
                        // Only an explicit Connect action can recreate a removed device.
                        webhook = await client.RegisterAsync(next, _lifetime.Token);
                    }
                }
                var credentials = new Credentials(credential, webhook);
                _store.Save(next);
                _store.SaveCredentials(credentials);
                _client?.Dispose();
                _client = client;
                _credentials = credentials;
                _settings = next;
                _suspended = false;
            }
            catch { client.Dispose(); throw; }
            await PublishAsync(_lifetime.Token);
            try { await _notifications.ConfigureAsync(_settings, _credentials, _lifetime.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write("Notification startup", ex.GetType().Name); }
        }
        catch (Exception ex)
        {
            var authenticationFailed = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
            SetStatus(authenticationFailed ? SessionState.SignInRequired : SessionState.Failed,
                ex is ArgumentException or InvalidOperationException ? ex.Message : ConnectionErrorMessage.Describe(ex), false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (Paused) throw new InvalidOperationException("Turn on Enable sensors before changing individual sensors.");
            var definition = WindowsSensors.Available.Single(s => s.Id == id);
            if (_client is not null && _credentials?.WebhookId is { } webhook)
            {
                if (enabled && NetworkSensors.Supports(id))
                    await NetworkSensors.ConfigureTargetAsync(ServerAddress.Parse(_settings.ServerUrl).Host, _lifetime.Token);
                var unknown = definition.Type == "binary_sensor" ? null : (object)"unknown";
                var value = enabled ? ReadSensor(id).Value : _values.GetValueOrDefault(id, unknown);
                await _client.RegisterSensorAsync(webhook, definition, value, enabled, _lifetime.Token);
            }
            var sensors = new HashSet<string>(_settings.EnabledSensors);
            if (enabled) sensors.Add(id); else sensors.Remove(id);
            SaveSettings(_settings with { EnabledSensors = sensors });
            if (!enabled)
            {
                _values.Remove(id);
                ResetSensor(id);
            }
            if (_client is not null) await PublishAsync(_lifetime.Token);
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            SaveSettings(_settings with { ShareSensors = !paused });
            if (paused)
                foreach (var sensor in WindowsSensors.Available) ResetSensor(sensor.Id);
            if (_client is not null) await PublishAsync(_lifetime.Token);
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task SetThemeAsync(string theme)
    {
        await _gate.WaitAsync(_lifetime.Token);
        try { SaveSettings(_settings with { Theme = theme }); }
        finally { _gate.Release(); }
    }

    public async Task SetNotificationsEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (enabled && !IsRegistered) throw new InvalidOperationException("Connect to Home Assistant first.");
            if (enabled && !NotificationsSupported) throw new InvalidOperationException("Windows notifications are unavailable on this PC.");
            SaveSettings(_settings with { NotificationsEnabled = enabled });
            if (enabled) await _notifications.ConfigureAsync(_settings, _credentials, _lifetime.Token);
            else await _notifications.DisableRemoteAsync(_settings, _credentials, _lifetime.Token);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task SetNotificationSoundAsync(bool sound)
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            SaveSettings(_settings with { NotificationSound = sound });
            _notifications.UpdateOptions(_settings);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task TestNotificationAsync()
    {
        await _gate.WaitAsync(_lifetime.Token);
        try { await _notifications.TestAsync(_settings); }
        finally { _gate.Release(); }
    }

    private async Task StartNotificationsAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            await _gate.WaitAsync(ct);
            try { await _notifications.ConfigureAsync(_settings, _credentials, ct); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write("Notification startup", ex.GetType().Name); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Let the window subscribe before the first connection result is published.
        await Task.Delay(500, ct);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_client is not null && !_suspended) await PublishAsync(ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RegistrationRemovedException)
            {
                _suspended = true;
                SetStatus(SessionState.DeviceRemoved, "The device was removed in Home Assistant. Its sensors will not be recreated automatically.", false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _suspended = true;
                SetStatus(SessionState.SignInRequired, "Update the access token in Settings.", false);
            }
            catch (Exception ex)
            {
                failures = Math.Min(failures + 1, 4);
                AppLog.Write("Sensor connection", ex.GetType().Name);
                SetStatus(SessionState.Reconnecting, "Home Assistant is unavailable. HASS Connect will retry automatically.", false);
            }
            finally { _gate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 15 : Math.Min(60, 5 * (1 << failures))), ct);
        }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        if (_client is null || _credentials?.WebhookId is not { } webhook) return;
        var configuration = await _client.WebhookAsync(webhook, "get_config", new { }, ct);
        var plan = SensorSyncPlan.Create(_settings, WindowsSensors.Available, ReadRemoteChoices(configuration));
        if (!plan.EnabledIds.SetEquals(_settings.EnabledSensors))
        {
            var disabled = _settings.EnabledSensors.Except(plan.EnabledIds).ToArray();
            SaveSettings(_settings with { EnabledSensors = new(plan.EnabledIds) });
            foreach (var id in disabled) ResetSensor(id);
        }
        foreach (var id in _values.Keys.Where(id => !plan.EnabledIds.Contains(id)).ToArray())
            _values.Remove(id);

        if (Paused)
        {
            SetStatus(SessionState.Paused, "Sensors are off. Home Assistant retains the last reported values.", true);
            return;
        }

        // Sample once per cycle, so registration and state updates use the same reading.
        if (plan.SensorsToRead.Any(sensor => NetworkSensors.Supports(sensor.Id)))
            await NetworkSensors.ConfigureTargetAsync(ServerAddress.Parse(_settings.ServerUrl).Host, ct);
        var readings = plan.SensorsToRead.Select(sensor => ReadSensor(sensor.Id)).ToArray();
        foreach (var sensor in plan.SensorsToRead.Where(sensor => plan.SensorsToRegister.Contains(sensor.Id)))
        {
            var reading = readings.Single(reading => reading.Id == sensor.Id);
            await _client.RegisterSensorAsync(webhook, sensor, reading.Value, true, ct);
        }
        if (readings.Length > 0)
        {
            var results = await _client.UpdateSensorsAsync(webhook, readings, ct);
            foreach (var reading in readings)
            {
                if (results.ValueKind != JsonValueKind.Object ||
                    !results.TryGetProperty(reading.Id, out var result) || result.ValueKind != JsonValueKind.Object ||
                    !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new InvalidDataException("Home Assistant rejected a sensor update.");
            }
            foreach (var reading in readings) _values[reading.Id] = reading.Value;
            LastReported = DateTimeOffset.Now;
        }
        SetStatus(SessionState.Connected, plan.EnabledIds.Count == 0 ? "Choose which sensors this PC shares with Home Assistant." : "Enabled sensors report every 15 seconds.", true);
    }

    private SensorReading ReadSensor(string id)
    {
        try
        {
            var reading = id switch
            {
                "display_state" => new SensorReading(id, _display.Read()),
                "last_seen" => new SensorReading(id, DateTimeOffset.UtcNow.ToString("O")),
                _ => WindowsSensors.Read(id)
            };
            _sensorErrors.Remove(id);
            return reading;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            // One unavailable Windows metric must not stop unrelated sensors or
            // make a healthy Home Assistant connection appear disconnected.
            if (_sensorErrors.Add(id)) AppLog.Write($"Read sensor {id}", ex.GetType().Name);
            ResetSensor(id);
            return new(id, WindowsSensors.Available.Single(sensor => sensor.Id == id).Type == "binary_sensor" ? null : "unknown");
        }
    }

    private static IReadOnlyDictionary<string, bool> ReadRemoteChoices(JsonElement configuration)
    {
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!configuration.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return choices;
        foreach (var entity in entities.EnumerateObject())
        {
            if (entity.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Home Assistant returned an invalid sensor configuration.");
            choices[entity.Name] = !entity.Value.TryGetProperty("disabled", out var disabled) || disabled.ValueKind == JsonValueKind.False;
        }
        return choices;
    }

    private void SaveSettings(Settings settings)
    {
        _store.Save(settings);
        _settings = settings;
    }

    private void ResetSensor(string id)
    {
        if (id == "display_state") _display.Reset();
        else WindowsSensors.Reset(id);
    }

    private void SetStatus(SessionState state, string detail, bool connected)
    {
        State = state; Detail = detail; Connected = connected;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        await _notificationStartup;
        // UI actions share this gate with the polling loop. Wait for their
        // cancellation to finish before releasing the connection resources.
        await _gate.WaitAsync();
        try
        {
            await _notifications.DisposeAsync();
            _display.Dispose();
            _client?.Dispose();
            _lifetime.Dispose();
        }
        finally { _gate.Release(); }
    }
}
