using Microsoft.UI.Xaml;
using HassConnect.App.Services;

namespace HassConnect.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly DesktopActivation _activation;
    private readonly WindowsNotifications _notifications;

    internal App(WindowsNotifications notifications, DesktopActivation activation)
    {
        _notifications = notifications;
        _activation = activation;
        InitializeComponent();
        UnhandledException += (_, args) => AppLog.Write("Unhandled error", args.Exception.GetType().Name);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow(_notifications, _activation);
            _window.QuitRequested += Exit;
            _activation.OpenRequested += _window.ShowWindow;
            _activation.ExitRequested += () => _ = _window.QuitAsync();
            if (_activation.ShowOnLaunch) _window.Activate();
            _activation.MarkReady(_window.DispatcherQueue);
        }
        catch (Exception ex)
        {
            AppLog.Write("Application startup", ex.GetType().Name);
            Environment.ExitCode = 1;
            Exit();
        }
    }
}
