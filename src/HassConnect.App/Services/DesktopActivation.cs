using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HassConnect.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace HassConnect.App.Services;

internal sealed class DesktopActivation : IDisposable
{
    private readonly AppInstance _current = AppInstance.GetCurrent();
    private readonly ConcurrentQueue<string> _pending = new();
    private volatile DispatcherQueue? _dispatcher;
    private Mutex? _profileMutex;
    private bool _primary;

    private readonly WindowsNotifications _notifications;

    public DesktopActivation(WindowsNotifications notifications)
    {
        _notifications = notifications;
        _notifications.Invoked += QueueNative;
    }

    public event Action<string>? ActionInvoked;
    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public bool ShowOnLaunch { get; private set; }

    public bool Start(bool shutdown, bool startInTray)
    {
        // Subscribe before claiming the key so an immediate second launch is
        // retained even while the first window and its services are starting.
        _current.Activated += Activated;
        var profile = Path.TrimEndingDirectorySeparator(UserDataDirectory.Path).ToUpperInvariant();
        var profileKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)));
        var key = UserDataDirectory.IsIsolated ? "HASSConnect.Test." + profileKey : "HASSConnect.Desktop";
        var instance = AppInstance.FindOrRegisterForKey(key)
            ?? throw new InvalidOperationException("The HASS Connect instance is closing. Try launching again.");
        _primary = instance.IsCurrent;
        if (_primary && (shutdown || !TryClaimProfile(profileKey))) return false;
        if (!_primary && startInTray && !shutdown) return false;

        // Registration must precede GetActivatedEventArgs for native toast activation.
        try { if (!UserDataDirectory.IsIsolated) _notifications.Register(); }
        catch (Exception ex) { AppLog.Write("Windows notification registration", $"0x{ex.HResult:X8} {ex.GetType().Name}"); }
        var activation = _current.GetActivatedEventArgs();
        if (!_primary)
        {
            // The WinUI entry point must remain an STA. Redirect from a worker before creating XAML.
            Task.Run(() => RedirectAsync(instance, activation, shutdown))
                .WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            return false;
        }

        ShowOnLaunch = !startInTray && activation.Kind == ExtendedActivationKind.Launch;
        if (!startInTray && !ShowOnLaunch) Queue(activation);
        return true;
    }

    private bool TryClaimProfile(string profileKey)
    {
        // AppInstance keys for unpackaged apps are scoped to the executable path.
        // Keep different copies pointed at the same settings from running together.
        var mutex = new Mutex(false, @"Local\HASSConnect.Profile." + profileKey);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException) { } // Windows transferred ownership to this process.
        catch
        {
            mutex.Dispose();
            throw;
        }
        _profileMutex = mutex;
        return true;
    }

    private static async Task RedirectAsync(AppInstance instance, AppActivationArguments activation, bool shutdown)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var process = shutdown ? Process.GetProcessById((int)instance.ProcessId) : null;
            await instance.RedirectActivationToAsync(activation).AsTask().WaitAsync(deadline.Token);
            if (process is not null) await process.WaitForExitAsync(deadline.Token);
        }
        catch (ArgumentException) when (shutdown) { } // The running process already exited.
    }

    public void MarkReady(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Drain();
    }

    private void Activated(object? sender, AppActivationArguments args) => Queue(args);

    private void QueueNative(string argument)
    {
        _pending.Enqueue(argument);
        _dispatcher?.TryEnqueue(Drain);
    }

    private void Queue(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Launch)
            QueueNative(args.Data is ILaunchActivatedEventArgs launch &&
                (launch.Arguments.TrimEnd().EndsWith(" --shutdown", StringComparison.Ordinal) ||
                 launch.Arguments.TrimEnd().EndsWith(" \"--shutdown\"", StringComparison.Ordinal)) ? "exit" : "open");
        else if (args.Kind == ExtendedActivationKind.AppNotification &&
                 args.Data is AppNotificationActivatedEventArgs notification &&
                 NotificationActivation.TryParse(notification.Argument, out var action))
            QueueNative(action ?? "open");
    }

    private void Drain()
    {
        while (_pending.TryDequeue(out var action))
        {
            try
            {
                if (action == "open") OpenRequested?.Invoke();
                else if (action == "exit") ExitRequested?.Invoke();
                else ActionInvoked?.Invoke(action);
            }
            catch (Exception ex) { AppLog.Write("Application activation", ex.GetType().Name); }
        }
    }

    public void Dispose()
    {
        _notifications.Invoked -= QueueNative;
        _current.Activated -= Activated;
        _dispatcher = null;
        _pending.Clear();
        try { if (_primary) _current.UnregisterKey(); }
        finally
        {
            if (_profileMutex is { } mutex)
            {
                _profileMutex = null;
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
