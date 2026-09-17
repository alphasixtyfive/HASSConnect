using HassConnect.App.Services;
using HassConnect.Core;
using HassConnect.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class UpdateSettingsView : UserControl
{
    private static readonly HttpClient Client = new() { MaxResponseContentBufferSize = 1024 * 1024 };
    private Uri? _release;

    public UpdateSettingsView()
    {
        InitializeComponent();
        InstalledVersion.Text = $"HASS Connect {ProductInfo.Version}";
    }

    private async void Update_Click(object sender, RoutedEventArgs args)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (_release is not null)
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(_release))
                    SetStatus("Couldn’t open the release page.");
                return;
            }
            SetStatus("Checking for updates…");
            var result = await new UpdateChecker(Client).CheckAsync(ProductInfo.Repository, ProductInfo.Version);
            _release = result.ReleasePage;
            UpdateButton.Content = _release is null ? "Check updates" : "View release";
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            AppLog.Write("Update check", ex.GetType().Name);
            SetStatus("Couldn’t check for updates. Try again.");
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void SetStatus(string message)
    {
        UpdateStatus.Text = message;
        ToolTipService.SetToolTip(UpdateStatus, message);
    }
}
