using HassConnect.App.Services;
using HassConnect.App.Views;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Windows.Storage.Pickers;

namespace HassConnect.App;

public sealed partial class MainWindow
{
    private bool _settingsFileBusy;

    private async void ImportSettings_Click(object sender, RoutedEventArgs args) =>
        await UseSettingsFileAsync(import: true);

    private async void ExportSettings_Click(object sender, RoutedEventArgs args) =>
        await UseSettingsFileAsync(import: false);

    private async Task UseSettingsFileAsync(bool import)
    {
        if (_session is null || _settingsFileBusy) return;
        _settingsFileBusy = true;
        TransferSettingsButton.IsEnabled = false;
        ShowSettingsFileResult("");
        try
        {
            if (import)
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;

                var settings = await ReadSettingsFileAsync(file.Path);
                if (!await DialogLayout.ShowConfirmationAsync(Root.XamlRoot, "Import these settings?",
                        "This replaces the appearance, sensor choices, notification sound and Quick access actions on this PC.",
                        "Import")) return;

                await _session.ImportSettingsAsync(settings);
                _quickAccessWindow?.Hide(commitPendingLevel: false);
                ThemeBox.SelectedItem = settings.Theme;
                ApplyTheme(settings.Theme);
                ShowSettingsFileResult("Settings imported");
            }
            else
            {
                var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "hass-connect-settings" };
                picker.FileTypeChoices.Add("JSON settings file", [".json"]);
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;

                await WriteSettingsFileAsync(file.Path, SettingsTransfer.Export(_session.Settings));
                ShowSettingsFileResult("Settings exported");
            }
        }
        catch (Exception ex)
        {
            ShowSettingsFileResult(ex is InvalidDataException ? ex.Message : UserMessage(ex));
        }
        finally
        {
            _settingsFileBusy = false;
            TransferSettingsButton.IsEnabled = true;
        }
    }

    private void ShowSettingsFileResult(string message)
    {
        SettingsTransferRow.ValueText = message;
        AutomationProperties.SetItemStatus(TransferSettingsButton, message);
    }

    private static async Task<SettingsTransfer> ReadSettingsFileAsync(string path)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[SettingsTransfer.MaximumBytes + 1];
        var size = await source.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
        return SettingsTransfer.Read(bytes.AsSpan(0, size));
    }

    private static async Task WriteSettingsFileAsync(string path, byte[] content)
    {
        var relative = Path.GetRelativePath(UserDataDirectory.Path, path);
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("..\\", StringComparison.Ordinal))
            throw new InvalidDataException("Choose a location outside the HASS Connect app data folder.");

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".hass-connect-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Settings export cleanup", ex);
            }
        }
    }
}
