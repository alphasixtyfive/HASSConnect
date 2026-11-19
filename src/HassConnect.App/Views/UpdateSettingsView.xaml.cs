using System.Diagnostics;
using HassConnect.App.Services;
using HassConnect.Core;
using HassConnect.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class UpdateSettingsView : UserControl
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 1024 * 1024
    };
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateResult? _update;
    private bool _busy;
    private bool _checked;

    public event Action<bool>? AvailabilityChanged;

    public UpdateSettingsView()
    {
        InitializeComponent();
        InstalledVersion.Text = $"HASS Connect {ProductInfo.Version}";
        Loaded += Loaded_CheckUpdates;
        Unloaded += (_, _) => _lifetime.Cancel();
    }

    private async void Update_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (_update?.Package is { } package)
        {
            await PrepareUpdateAsync(package);
            return;
        }
        if (_update?.ReleasePage is { } release)
        {
            try
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(release))
                    SetStatus("Couldn’t open the release page.");
            }
            catch (Exception ex)
            {
                AppLog.Write("Open update release", ex);
                SetStatus("Couldn’t open the release page.");
            }
            return;
        }
        await CheckAsync();
    }

    private async void Loaded_CheckUpdates(object sender, RoutedEventArgs args)
    {
        if (_checked || UserDataDirectory.IsIsolated) return;
        _checked = true;
        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        SetBusy(true);
        SetStatus("Checking for updates…");
        try
        {
            _update = await new UpdateChecker(Client).CheckAsync(
                ProductInfo.Repository, ProductInfo.Version, _lifetime.Token);
            SetStatus(_update.Message);
            AvailabilityChanged?.Invoke(_update.State == UpdateState.Available);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Write("Update check", ex.GetType().Name);
            _update = null;
            SetStatus("Couldn’t check for updates. Try again.");
        }
        finally { SetBusy(false); }
    }

    private async Task PrepareUpdateAsync(UpdatePackage package)
    {
        SetBusy(true);
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        SetStatus($"Downloading version {package.Version}…");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (DownloadProgress.Visibility != Visibility.Visible) return;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = value.BytesReceived * 100d / value.TotalBytes;
                SetStatus($"Downloading version {package.Version} · {DownloadProgress.Value:0}%");
            });
            var downloaded = await new UpdateInstaller(Client).DownloadAsync(
                package,
                ProductInfo.Repository!,
                Path.Combine(UserDataDirectory.Path, "Updates"),
                progress,
                _lifetime.Token);
            DownloadProgress.Visibility = Visibility.Collapsed;
            _lifetime.Token.ThrowIfCancellationRequested();
            SetStatus("Download verified. Choose Update in Setup to continue.");
            using var process = Process.Start(new ProcessStartInfo(downloaded.InstallerPath)
            {
                UseShellExecute = true,
                Arguments = "/install /norestart HassConnectInAppUpdate=1"
            });
            if (process is null) throw new InvalidOperationException("Windows could not start Setup.");
            await process.WaitForExitAsync(_lifetime.Token);
            // Setup shuts this app down when it starts replacing the installed files.
            SetStatus("Setup closed. Select Get update to try again.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            SetStatus("The update download timed out. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Update install", ex);
            SetStatus("Couldn’t download or start the update. Try again.");
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void SetStatus(string message)
    {
        UpdateStatus.Text = message;
        ToolTipService.SetToolTip(UpdateStatus, message);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy;
        UpdateButton.Content = busy ? "Please wait…" : _update?.Package is not null ? "Get update" :
            _update?.ReleasePage is not null ? "View release" : "Check now";
    }
}
