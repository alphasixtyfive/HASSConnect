using Microsoft.UI.Xaml;
using HassConnect.App.Services;

namespace HassConnect.App;

public partial class App : Application
{
    private MainWindow? _window;
    private DesktopActivation? _activation;
    private readonly WindowsNotifications _notifications = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => AppLog.Write("Unhandled error", args.Exception.GetType().Name);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--unregister-notifications"))
            {
                try { await _notifications.RemoveRegistrationAsync(); }
                finally { RemoveStartupEntry(); }
                Exit();
                return;
            }
            _activation = new DesktopActivation(_notifications);
            if (!await _activation.StartAsync())
            {
                Shutdown();
                return;
            }
            _window = new MainWindow(_notifications, _activation);
            _window.QuitRequested += Shutdown;
            _activation.OpenRequested += _window.ShowWindow;
            _activation.ExitRequested += () => _ = _window.QuitAsync();
            _window.Activate();
            _activation.MarkReady(_window.DispatcherQueue);
        }
        catch (Exception ex)
        {
            AppLog.Write("Application startup", ex.GetType().Name);
            Environment.ExitCode = 1;
            Shutdown();
        }
    }

    private static void RemoveStartupEntry()
    {
        using var startup = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (startup?.GetValue("HassConnect") is string command &&
            command.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase))
            startup.DeleteValue("HassConnect", false);
    }

    private void Shutdown()
    {
        try
        {
            _activation?.Dispose();
            _notifications.Dispose();
        }
        catch (Exception ex) { AppLog.Write("Windows notification shutdown", ex.GetType().Name); }
        finally { Exit(); }
    }
}
