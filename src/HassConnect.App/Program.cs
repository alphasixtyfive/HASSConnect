using HassConnect.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HassConnect.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var notifications = new WindowsNotifications();
            if (args.Contains("--unregister-notifications"))
            {
                using (notifications)
                {
                    try { Task.Run(notifications.RemoveRegistrationAsync).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                    finally { StartupRegistration.RemoveForCurrentExecutable(); }
                }
                return 0;
            }

            using var activation = new DesktopActivation(notifications);
            using (notifications)
            {
                if (!activation.Start(args.Contains("--shutdown"), args.Contains("--tray"))) return 0;

                Application.Start(initialized =>
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    _ = new App(notifications, activation);
                });
                return Environment.ExitCode;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Application startup", ex);
            return 1;
        }
    }
}
