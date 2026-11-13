using HassConnect.App.Services;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HassConnect.App.Views;

public sealed partial class CustomCommandDialog : ContentDialog
{
    private readonly SensorSession _session;
    private readonly nint _windowHandle;
    private bool _initializing;
    private bool _customCommandDeleteRequested;
    private bool _customCommandSaving;
    private bool _customCommandTesting;
    private CustomCommandDefinition? _editingCustomCommand;
    private string? _suggestedId;

    internal CustomCommandDialog(SensorSession session, nint windowHandle)
    {
        _session = session;
        _windowHandle = windowHandle;
        InitializeComponent();
        Opened += (_, _) =>
        {
            ResizeContent();
            XamlRoot.Changed += RootSizeChanged;
        };
        Closed += (_, _) => XamlRoot.Changed -= RootSizeChanged;
    }

    private void RootSizeChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeContent();
    private void ResizeContent() => EditorLayout.MaxHeight = Math.Max(160, XamlRoot.Size.Height - 160);

    internal async Task EditAsync(string? id)
    {
        if (_session is null) return;
        var existing = id is null
            ? null
            : _session.Settings.CustomCommands.SingleOrDefault(command => command.Id == id);
        if (id is not null && existing is null) return;

        this.Title = existing is null ? "Add custom command" : "Edit custom command";
        SaveCustomCommandButton.Content = existing is null ? "Add" : "Save";
        DeleteCustomCommandButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        _initializing = true;
        _editingCustomCommand = existing;
        CustomCommandNameBox.Text = existing?.Name ?? "";
        CustomCommandIdBox.Text = existing?.Id ?? "command_custom_";
        CustomCommandIdBox.IsReadOnly = existing is not null;
        CustomCommandExecutableBox.Text = existing?.ExecutablePath ?? "";
        CustomCommandArgumentsBox.Text = existing is null ? "" : string.Join(Environment.NewLine, existing.Arguments);
        CustomCommandError.Visibility = Visibility.Collapsed;
        CustomCommandTestStatus.Visibility = Visibility.Collapsed;
        _customCommandDeleteRequested = false;
        _customCommandSaving = false;
        _customCommandTesting = false;
        _initializing = false;
        UpdateArgumentsVisibility();
        SetCustomCommandDialogEnabled(true);
        while (true)
        {
            await this.ShowAsync();
            if (!_customCommandDeleteRequested) return;

            var confirmed = await DialogLayout.ShowConfirmationAsync(
                XamlRoot,
                "Remove command?",
                $"Remove “{existing!.Name}”? The program or script stays on this PC.",
                "Remove",
                defaultToPrimary: false,
                destructive: true);
            if (!confirmed)
            {
                _customCommandDeleteRequested = false;
                continue;
            }

            try { await _session.RemoveCustomCommandAsync(existing.Id); }
            catch (Exception ex)
            {
                _customCommandDeleteRequested = false;
                ShowEditorError(ex);
                continue;
            }
            return;
        }
    }

    private async void SaveCustomCommand_Click(object sender, RoutedEventArgs args) =>
        await SaveCustomCommandAsync();

    private void DeleteCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        if (_editingCustomCommand is null || _customCommandSaving || _customCommandTesting) return;
        _customCommandDeleteRequested = true;
        this.Hide();
    }

    private void CancelCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        if (!_customCommandSaving && !_customCommandTesting) this.Hide();
    }

    private async void CustomCommandDialog_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_customCommandSaving || _customCommandTesting) return;
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            this.Hide();
            return;
        }
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (ReferenceEquals(focused, CustomCommandArgumentsBox) || focused is Button) return;
        args.Handled = true;
        await SaveCustomCommandAsync();
    }

    private async Task SaveCustomCommandAsync()
    {
        if (_customCommandSaving || _customCommandTesting) return;

        _customCommandSaving = true;
        Exception? failure = null;
        SetCustomCommandDialogEnabled(false);
        try
        {
            var command = ReadCustomCommandDialog(_editingCustomCommand?.Enabled ?? true);
            if (_editingCustomCommand is null && _session.Settings.CustomCommands.Any(item => item.Id == command.Id))
                throw new ArgumentException("That Home Assistant command is already configured.", "id");
            await _session.SaveCustomCommandAsync(command);
            CustomCommandError.Visibility = Visibility.Collapsed;
            this.Hide();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or UnsupportedSettingsVersionException)
        {
            failure = ex;
            ShowEditorError(ex);
        }
        finally
        {
            _customCommandSaving = false;
            SetCustomCommandDialogEnabled(true);
            if (failure is not null) FocusInvalidCustomCommandField(failure);
        }
    }

    private void SetCustomCommandDialogEnabled(bool enabled)
    {
        var available = enabled && !_customCommandSaving && !_customCommandTesting;
        SaveCustomCommandButton.IsEnabled = available;
        DeleteCustomCommandButton.IsEnabled = available;
        CancelCustomCommandButton.IsEnabled = available;
        BrowseCustomCommandButton.IsEnabled = available;
        TestCustomCommandButton.IsEnabled = available;
        CustomCommandNameBox.IsEnabled = CustomCommandIdBox.IsEnabled = CustomCommandExecutableBox.IsEnabled = CustomCommandArgumentsBox.IsEnabled = available;
    }

    private void FocusInvalidCustomCommandField(Exception exception)
    {
        var field = (exception as ArgumentException)?.ParamName switch
        {
            "id" => CustomCommandIdBox,
            "name" => CustomCommandNameBox,
            "executablePath" => CustomCommandExecutableBox,
            "arguments" => CustomCommandArgumentsBox,
            _ => null
        };
        field?.Focus(FocusState.Programmatic);
    }

    private CustomCommandDefinition ReadCustomCommandDialog(bool enabled)
    {
        var arguments = CustomCommandPolicy.ParseArguments(CustomCommandArgumentsBox.Visibility == Visibility.Visible ? CustomCommandArgumentsBox.Text : "");
        return CustomCommandPolicy.Create(
            CustomCommandIdBox.Text,
            CustomCommandNameBox.Text,
            CustomCommandExecutableBox.Text,
            arguments,
            enabled,
            requireExecutable: true);
    }

    private async void TestCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        if (_session is null || _customCommandSaving || _customCommandTesting) return;
        Exception? failure = null;
        _customCommandTesting = true;
        SetCustomCommandDialogEnabled(false);
        CustomCommandError.Visibility = Visibility.Collapsed;
        CustomCommandTestStatus.Visibility = Visibility.Collapsed;
        try
        {
            var command = ReadCustomCommandDialog(_editingCustomCommand?.Enabled ?? true);
            await _session.TestCustomCommandAsync(command);
            CustomCommandTestStatus.Text = "Started. Check the result on this PC.";
            CustomCommandTestStatus.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            failure = ex;
            ShowEditorError(ex);
        }
        finally
        {
            _customCommandTesting = false;
            SetCustomCommandDialogEnabled(true);
            if (failure is not null) FocusInvalidCustomCommandField(failure);
        }
    }

    private async void BrowseCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".ps1");
            var file = await picker.PickSingleFileAsync();
            if (file is not null) CustomCommandExecutableBox.Text = file.Path;
        }
        catch (Exception ex) { ShowEditorError(ex); }
    }

    private void ShowEditorError(Exception exception)
    {
        CustomCommandError.Text = MainWindow.UserMessage(exception);
        CustomCommandError.Visibility = Visibility.Visible;
    }

    private void CommandName_Changed(object sender, TextChangedEventArgs args)
    {
        if (_initializing || _editingCustomCommand is not null || CustomCommandIdBox is null) return;
        if (CustomCommandIdBox.Text is not ("" or "command_custom_") && CustomCommandIdBox.Text != _suggestedId) return;
        _suggestedId = CustomCommandPolicy.SuggestId(CustomCommandNameBox.Text, _session.Settings.CustomCommands.Select(command => command.Id));
        CustomCommandIdBox.Text = _suggestedId;
    }

    private void CommandPath_Changed(object sender, TextChangedEventArgs args)
    {
        if (!_initializing && CustomCommandArgumentsBox is not null) UpdateArgumentsVisibility();
    }

    private void UpdateArgumentsVisibility()
    {
        var extension = Path.GetExtension(CustomCommandExecutableBox.Text.Trim()).ToLowerInvariant();
        CustomCommandArgumentsBox.Visibility = extension is ".ps1" or ".bat" or ".cmd"
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(CustomCommandIdBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
        catch (Exception ex) { ShowEditorError(ex); }
    }
}
