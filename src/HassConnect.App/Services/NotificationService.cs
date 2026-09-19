using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.App.Services;

internal sealed class NotificationService : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, PendingAction> _actions = new();
    private readonly NotificationImageClient _images = new();
    private readonly NotificationImageCache _cache = new();
    private Settings _settings = new();
    private Credentials? _credentials;
    private CancellationTokenSource? _receiverStop;
    private Task? _receiver;
    private bool _initialized;
    private readonly WindowsNotifications _windows;
    private readonly DesktopActivation _activation;
    private readonly IPcCommandExecutor _pcCommands;
    private readonly ICustomCommandLauncher _customCommands;
    private bool _disposed;
    private readonly object _taskGate = new();
    private readonly List<Task> _backgroundTasks = [];

    public NotificationService(WindowsNotifications windows, DesktopActivation activation,
        IPcCommandExecutor pcCommands, ICustomCommandLauncher customCommands)
    {
        _windows = windows;
        _activation = activation;
        _pcCommands = pcCommands;
        _customCommands = customCommands;
        _activation.ActionInvoked += QueueAction;
    }

    public event Action? Changed;
    public bool Supported => WindowsNotifications.Supported;
    public bool Connected { get; private set; }
    public string Status { get; private set; } = "Off";

    public async Task ConfigureAsync(Settings settings, Credentials? credentials, CancellationToken ct)
    {
        await StopReceiverAsync();
        _settings = settings;
        _credentials = credentials;
        var receiverEnabled = settings.NotificationsEnabled || settings.PcControlEnabled;
        if (!receiverEnabled)
        {
            SetStatus("Off", false);
            await SetRemoteCapabilityAsync(settings, credentials, false, ct);
            return;
        }
        if (settings.NotificationsEnabled && !Supported)
        {
            SetStatus("Windows notifications are unavailable on this PC", false);
            throw new InvalidOperationException("Windows notifications are unavailable on this PC.");
        }
        if (settings.NotificationsEnabled)
        {
            try { await EnsureRegisteredAsync(); }
            catch
            {
                SetStatus("Windows notifications could not start", false);
                throw;
            }
        }
        if (credentials?.WebhookId is null)
        {
            SetStatus("Connect to Home Assistant", false);
            return;
        }
        ct.ThrowIfCancellationRequested();
        await SetRemoteCapabilityAsync(settings, credentials, true, ct);
        _receiverStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _receiver = RunAsync(settings, credentials, _receiverStop.Token);
    }

    public void UpdateOptions(Settings settings) => _settings = settings;

    public void TestCustomCommand(CustomCommandDefinition command) => _customCommands.Execute(command);

    public async Task DisableNotificationsAsync(Settings settings, Credentials? credentials, CancellationToken ct)
    {
        await ConfigureAsync(settings, credentials, ct);
        _actions.Clear();
        await _windows.ClearAsync();
    }

    public async Task TestAsync(Settings settings)
    {
        await EnsureRegisteredAsync();
        ShowNative(new("HASS Connect", "Notifications are working on this PC.", null, [], null), settings, null, []);
    }

    private async Task EnsureRegisteredAsync()
    {
        if (_initialized) return;
        _windows.Register();
        // Actions from an earlier process cannot be replayed.
        await _windows.ClearAsync();
        _initialized = true;
    }

    private async Task RunAsync(Settings settings, Credentials credentials, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus("Connecting…", false);
                using var client = new HaClient(ServerAddress.Parse(settings.ServerUrl), credentials.AccessToken);
                await NotificationChannel.ReceiveAsync(ServerAddress.Parse(settings.ServerUrl), credentials,
                    DeliverAsync, () => { failures = 0; SetStatus("Connected", true); },
                    ex => AppLog.Write("Remote message", ex.GetType().Name), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException)
            {
                SetStatus("Update the access token in Settings", false);
                return;
            }
            catch (RegistrationRemovedException)
            {
                SetStatus("Device removed from Home Assistant", false);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write("Notification connection", ex.GetType().Name);
                SetStatus("Reconnecting…", false);
            }
            failures = Math.Min(failures + 1, 4);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << failures))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task DeliverAsync(NotificationMessage message, CancellationToken ct)
    {
        var settings = _settings;
        var credentials = _credentials;
        if (credentials is null) return;
        if (message.Command is { } command)
        {
            if (command.Kind == PcCommandKind.Custom)
            {
                var configured = settings.CustomCommands.SingleOrDefault(candidate => candidate.Id == command.Id);
                if (settings.PcControlEnabled && configured?.Enabled == true)
                    ExecuteCustomCommand(configured);
                else
                    AppLog.Write("Custom command", "Ignored unknown or disabled command");
            }
            else if (settings.PcControlEnabled && settings.EnabledPcCommands.Contains(command.Id))
            {
                // Give Home Assistant time to receive its delivery confirmation before
                // the network connection is suspended with the PC.
                if (command.RequiresDeferredExecution) QueueDeferredCommand(command);
                else ExecutePcCommand(command);
            }
            else AppLog.Write("PC control", "Ignored while disabled");
            return;
        }
        if (!settings.NotificationsEnabled) return;
        if (message.IsClear)
        {
            await _windows.ClearAsync(WindowsTag(message.Tag!));
            RemoveActions(message.Tag!);
            return;
        }
        string? image = null;
        if (message.Image is { } source)
        {
            try
            {
                var bytes = await _images.DownloadAsync(ServerAddress.Parse(settings.ServerUrl), credentials.AccessToken, source, ct);
                image = await _cache.StoreAsync(bytes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex)
            {
                // Keep signed image URLs and credentials out of diagnostics.
                var detail = ex.StatusCode is { } status ? $"HTTP {(int)status}" : ex.HttpRequestError.ToString();
                AppLog.Write("Notification image", detail);
            }
            catch (OperationCanceledException) { AppLog.Write("Notification image", "Download timed out"); }
            catch (Exception ex) { AppLog.Write("Notification image", ex.GetType().Name); }
        }
        ct.ThrowIfCancellationRequested();
        var actions = new List<(string Title, string Argument)>();
        foreach (var old in _actions.Where(pair => pair.Value.Created < DateTimeOffset.UtcNow.AddHours(-24))) _actions.TryRemove(old.Key, out _);
        foreach (var action in message.Actions)
        {
            if (_actions.Count >= 100) break;
            var key = Guid.NewGuid().ToString("N");
            Uri? link = null;
            if (action.Link is not null)
            {
                try { link = NotificationLink.Resolve(ServerAddress.Parse(settings.ServerUrl), action.Link); }
                catch (InvalidDataException) { AppLog.Write("Notification link", "Unsupported URL"); continue; }
            }
            _actions[key] = new(action.Id, settings, credentials, DateTimeOffset.UtcNow, message, link);
            actions.Add((action.Title, key));
        }
        try
        {
            ShowNative(message, settings, image, actions);
            if (message.Tag is not null) RemoveActions(message.Tag, message.NotificationId);
        }
        catch
        {
            foreach (var action in actions) _actions.TryRemove(action.Argument, out _);
            throw;
        }
    }

    private void ShowNative(NotificationMessage message, Settings settings, string? image, IReadOnlyList<(string Title, string Argument)> actions)
        => _windows.Show(message, settings.NotificationSound, image, actions, message.Tag is null ? null : WindowsTag(message.Tag));

    private void QueueAction(string argument)
        => QueueBackground(() => SendActionAsync(argument));

    private void QueueDeferredCommand(PcCommand command)
        => QueueBackground(() => ExecuteDeferredCommandAsync(command));

    private void QueueBackground(Func<Task> operation)
    {
        lock (_taskGate)
        {
            if (_disposed) return;
            _backgroundTasks.RemoveAll(task => task.IsCompleted);
            _backgroundTasks.Add(operation());
        }
    }

    private async Task ExecuteDeferredCommandAsync(PcCommand command)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
            ExecutePcCommand(command);
        }
        catch (OperationCanceledException) when (_disposed) { }
    }

    private void ExecutePcCommand(PcCommand command)
    {
        try { _pcCommands.Execute(command); }
        catch (Exception exception)
        {
            var detail = exception is Win32Exception win32
                ? $"Win32 error {win32.NativeErrorCode}"
                : exception.GetType().Name;
            AppLog.Write("PC control", detail);
        }
    }

    private void ExecuteCustomCommand(CustomCommandDefinition command)
    {
        try
        {
            _customCommands.Execute(command);
            AppLog.Write("Custom command", $"Started {command.Id}");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidDataException or InvalidOperationException)
        {
            var detail = exception is Win32Exception win32
                ? $"{command.Id}: Win32 error {win32.NativeErrorCode}"
                : $"{command.Id}: {exception.GetType().Name}";
            AppLog.Write("Custom command", detail);
        }
    }

    private async Task SendActionAsync(string argument)
    {
        try
        {
            if (_disposed || !_settings.NotificationsEnabled ||
                !_actions.TryRemove(argument, out var action) ||
                action.Created < DateTimeOffset.UtcNow.AddHours(-24)) return;
            if (action.Link is not null)
            {
                Process.Start(new ProcessStartInfo(action.Link.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            using var client = new HaClient(ServerAddress.Parse(action.Settings.ServerUrl), action.Credentials.AccessToken);
            await client.SendNotificationActionAsync(action.Credentials.WebhookId!, action.Settings.DeviceId, action.Id, _lifetime.Token,
                action.Message.Tag, action.Message.NotificationId, action.Message.ActionData);
            SetStatus("Action sent to Home Assistant", Connected);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            AppLog.Write("Notification action", ex.GetType().Name);
            SetStatus("Notification action could not be completed", Connected);
            try
            {
                ShowNative(new("Action not confirmed", "The action could not be completed. Check Home Assistant before trying again.", null, [], null), _settings, null, []);
            }
            catch (Exception displayError) { AppLog.Write("Action failure feedback", displayError.GetType().Name); }
        }
    }

    private async Task StopReceiverAsync()
    {
        if (_receiverStop is null) return;
        await _receiverStop.CancelAsync();
        if (_receiver is not null) await _receiver;
        _receiverStop.Dispose();
        _receiverStop = null;
        _receiver = null;
        Connected = false;
    }

    private static async Task SetRemoteCapabilityAsync(Settings settings, Credentials? credentials, bool enabled, CancellationToken ct)
    {
        if (credentials?.WebhookId is not { } webhook || string.IsNullOrWhiteSpace(settings.ServerUrl)) return;
        using var client = new HaClient(ServerAddress.Parse(settings.ServerUrl), credentials.AccessToken);
        await client.SetNotificationCapabilityAsync(webhook, settings, enabled, ct);
    }

    private void SetStatus(string status, bool connected)
    {
        Status = status;
        Connected = connected;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] backgroundTasks;
        lock (_taskGate)
        {
            _disposed = true;
            _activation.ActionInvoked -= QueueAction;
            backgroundTasks = _backgroundTasks.ToArray();
        }
        await _lifetime.CancelAsync();
        await Task.WhenAll(backgroundTasks);
        await StopReceiverAsync();
        try { await _windows.ClearAsync(); }
        catch (Exception ex) { AppLog.Write("Notification cleanup", ex.GetType().Name); }
        _actions.Clear();
        _images.Dispose();
        _lifetime.Dispose();
    }

    private static string WindowsTag(string tag) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..32];

    private void RemoveActions(string tag, string? exceptNotificationId = null)
    {
        foreach (var pair in _actions.Where(pair => pair.Value.Message.Tag == tag && pair.Value.Message.NotificationId != exceptNotificationId))
            _actions.TryRemove(pair.Key, out _);
    }

    private sealed record PendingAction(string Id, Settings Settings, Credentials Credentials, DateTimeOffset Created,
        NotificationMessage Message, Uri? Link);
}
