using System.Collections.Concurrent;
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

    public async Task<bool> StartAsync()
    {
        // Subscribe before claiming the key so an immediate second launch is
        // retained even while the first window and its services are starting.
        _current.Activated += Activated;
        // Registration must precede GetActivatedEventArgs for native toast activation.
        try { _notifications.Register(); }
        catch (Exception ex) { AppLog.Write("Windows notification registration", $"0x{ex.HResult:X8} {ex.GetType().Name}"); }
        var activation = _current.GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey("HASSConnect.Desktop");
        if (!instance.IsCurrent)
        {
            // Await without blocking the UI apartment; Windows keeps the
            // arguments alive until the primary process accepts them.
            await instance.RedirectActivationToAsync(activation);
            if (Environment.GetCommandLineArgs().Contains("--shutdown"))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)instance.ProcessId);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (ArgumentException) { }
                catch (OperationCanceledException) { }
            }
            return false;
        }

        _primary = true;
        if (Environment.GetCommandLineArgs().Contains("--shutdown")) return false;
        Queue(activation);
        return true;
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
            QueueNative(args.Data is ILaunchActivatedEventArgs launch && launch.Arguments.Split(' ').Contains("--shutdown") ? "exit" : "open");
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
        if (_primary) _current.UnregisterKey();
    }
}
