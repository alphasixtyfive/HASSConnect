using HassConnect.Core;
using Microsoft.Windows.AppNotifications;

namespace HassConnect.App.Services;

internal sealed partial class WindowsNotifications : IDisposable
{
    private bool _registered;

    public static bool Supported => AppNotificationManager.IsSupported();
    public event Action<string>? Invoked;

    public void Register()
    {
        if (_registered) return;
        if (!Supported) throw new InvalidOperationException("Windows notifications are unavailable on this PC.");
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnInvoked;
        try
        {
            manager.Register(ProductInfo.Name, new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "hass-connect.png")));
            _registered = true;
        }
        catch
        {
            manager.NotificationInvoked -= OnInvoked;
            throw;
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (NotificationActivation.TryParse(args.Argument, out var action)) Invoked?.Invoke(action ?? "open");
    }

    public void Show(NotificationMessage message, bool sound, string? image,
        IReadOnlyList<(string Title, string Argument)> actions, string? tag, string launchArgument = "open")
    {
        Register();
        var manager = AppNotificationManager.Default;
        if (manager.Setting != AppNotificationSetting.Enabled)
            throw new InvalidOperationException("Allow notifications for HASS Connect in Windows Settings.");
        var toast = new AppNotification(NotificationToast.Create(message, sound, image, actions, launchArgument))
        { ExpiresOnReboot = true };
        if (!message.Persistent) toast.Expiration = DateTimeOffset.Now.AddHours(24);
        if (tag is not null) toast.Tag = tag;
        manager.Show(toast);
        if (toast.Id == 0) throw new InvalidOperationException("Windows could not display the notification.");
    }

    public async Task ClearAsync(string? tag = null)
    {
        if (!_registered) return;
        if (tag is null) await AppNotificationManager.Default.RemoveAllAsync();
        else await AppNotificationManager.Default.RemoveByTagAsync(tag);
    }

    public void Dispose()
    {
        if (!_registered) return;
        var manager = AppNotificationManager.Default;
        _registered = false;
        try { manager.Unregister(); }
        finally { manager.NotificationInvoked -= OnInvoked; }
    }

    public async Task RemoveRegistrationAsync()
    {
        Register();
        var manager = AppNotificationManager.Default;
        try { await ClearAsync(); }
        finally
        {
            try { manager.UnregisterAll(); }
            finally
            {
                manager.NotificationInvoked -= OnInvoked;
                _registered = false;
            }
        }
    }
}
