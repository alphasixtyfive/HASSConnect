using System.Diagnostics;
using HassConnect.App.Services;
using HassConnect.Core;
using HassConnect.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class UpdateSettingsView : UserControl
{
    private static readonly HttpClient Client = CreateClient();
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
            await ConfirmAndInstallAsync(package);
            return;
        }
        if (_update?.ReleasePage is { } release)
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(release))
                SetStatus("Couldn’t open the release page.");
            return;
        }
        await CheckAsync();
    }

    private async void Loaded_CheckUpdates(object sender, RoutedEventArgs args)
    {
        if (_checked) return;
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

    private async Task ConfirmAndInstallAsync(UpdatePackage package)
    {
        if (!await DialogLayout.ShowConfirmationAsync(
                XamlRoot,
                "Install HASS Connect update?",
                $"Version {package.Version} will be downloaded, verified, and opened in Setup. HASS Connect will close while Windows installs the update.",
                "Install",
                defaultToPrimary: true)) return;

        SetBusy(true);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        SetStatus($"Downloading version {package.Version}…");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (value.TotalBytes is > 0)
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = value.BytesReceived * 100d / value.TotalBytes.Value;
                    SetStatus($"Downloading version {package.Version} · {DownloadProgress.Value:0}%");
                }
                else
                {
                    SetStatus($"Downloading version {package.Version} · {value.BytesReceived / 1024d / 1024d:0.0} MB");
                }
            });
            var downloaded = await new UpdateInstaller(Client).DownloadAsync(
                package,
                ProductInfo.Repository!,
                Path.Combine(UserDataDirectory.Path, "Updates"),
                progress,
                _lifetime.Token);
            SetStatus("Download verified. Opening Setup…");
            using var process = Process.Start(new ProcessStartInfo(downloaded.InstallerPath)
            {
                UseShellExecute = true,
                Arguments = "/install"
            });
            if (process is null) throw new InvalidOperationException("Windows could not start Setup.");
            SetStatus($"HASS Connect {package.Version} Setup is ready. Follow the installer to finish updating.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            SetStatus("The update download timed out. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Update install", ex.GetType().Name);
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
        UpdateButton.Content = busy ? "Please wait…" : _update?.Package is not null ? "Install update" :
            _update?.ReleasePage is not null ? "View release" : "Check updates";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024
        };
        return client;
    }
}
