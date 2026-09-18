using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using HassConnect.App.Services;
using HassConnect.App.Views;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.App;

public sealed partial class MainWindow : Window
{
    private readonly SensorSession? _session;
    private readonly TrayIcon _tray;
    private readonly nint _windowHandle;
    private bool _quitting;
    private bool _loading = true;
    private bool _connecting;
    private bool _loadingDashboards;
    private bool _customCommandDialogOpen;
    private bool _customCommandDeleteRequested;
    private bool _customCommandSaving;
    private bool _customCommandTesting;
    private CustomCommandDefinition? _editingCustomCommand;
    private double _windowScale;
    internal event Action? QuitRequested;

    internal MainWindow(WindowsNotifications notifications, DesktopActivation activation)
    {
        InitializeComponent();
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(_windowHandle) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(800 * scale), workArea.Width - (int)(32 * scale)),
            Math.Min((int)(840 * scale), workArea.Height - (int)(32 * scale))));
        UpdateWindowMinimumSize();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        SystemBackdrop = new MicaBackdrop();
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) HideToken(); };
        _tray = new TrayIcon(_windowHandle, ShowWindow, () => _ = QuitAsync(), OpenHomeAssistant);
        AppWindow.Closing += (_, args) => { if (!_quitting) { args.Cancel = true; AppWindow.Hide(); } };
        try
        {
            _session = new SensorSession(new SettingsStore(), _windowHandle,
                new NotificationService(notifications, activation, new WindowsPcControl(), new CustomCommandLauncher()));
            _session.Changed += () => DispatcherQueue.TryEnqueue(Refresh);
            ServerBox.Text = _session.Settings.ServerUrl;
            DeviceBox.Text = _session.Settings.DeviceName;
            ServerBox.IsReadOnly = _session.IsRegistered;
            DeviceBox.IsReadOnly = _session.IsRegistered;
            ToolTipService.SetToolTip(ServerBox, _session.IsRegistered ? "This device is registered with this server." : null);
            ToolTipService.SetToolTip(DeviceBox, _session.IsRegistered ? "Rename this device in Home Assistant." : null);
            TokenBox.PlaceholderText = _session.GetSavedAccessToken() is null ? "Paste your access token" : "Saved token";
            ThemeBox.SelectedItem = _session.Settings.Theme;
            ApplyTheme(_session.Settings.Theme);
            SensorsPage.SensorChanged += Sensor_Changed;
            SensorsPage.SharingChanged += Reporting_Changed;
            NotificationsPage.EnabledChanged += Notifications_Changed;
            NotificationsPage.SoundChanged += NotificationSound_Changed;
            NotificationsPage.TestRequested += TestNotification_Requested;
            ControlsPage.EnabledChanged += PcControl_Changed;
            ControlsPage.CommandChanged += PcCommand_Changed;
            ControlsPage.AddCustomCommandRequested += () => _ = ShowCustomCommandDialogAsync(null);
            ControlsPage.EditCustomCommandRequested += id => _ = ShowCustomCommandDialogAsync(id);
            ControlsPage.CustomCommandEnabledChanged += CustomCommandEnabled_Changed;
            UpdatesPage.AvailabilityChanged += available =>
                SettingsUpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings load", ex.GetType().Name);
            ShowError("Unable to load saved settings. Your existing files have been preserved. Check the diagnostic logs.");
            SaveConnectionButton.IsEnabled = false;
        }
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        StartupRow.IsOn = run?.GetValue("HassConnect") is string;
        _loading = false;
        SelectPage(_session?.IsRegistered == true ? "sensors" : "settings");
        Root.Loaded += async (_, _) =>
        {
            UpdateTitleBar();
            Root.XamlRoot.Changed += (_, _) => UpdateWindowMinimumSize();
            UpdateWindowMinimumSize();
            if (Environment.GetCommandLineArgs().Contains("--tray")) AppWindow.Hide();
            await LoadDashboardsAsync(showErrors: false);
        };
    }

    internal void ShowWindow()
    {
        AppWindow.Show();
        WindowActivation.RestoreAndActivate(_windowHandle);
        Activate();
    }

    private async void OpenHomeAssistant()
    {
        if (_session is null) return;
        try
        {
            var server = HassConnect.Core.ServerAddress.Parse(_session.Settings.ServerUrl);
            var destination = HassConnect.Core.HomeAssistantNavigation.Resolve(server, _session.Settings.HomeAssistantPath);
            if (!await Windows.System.Launcher.LaunchUriAsync(destination))
                throw new InvalidOperationException("Windows could not open the browser.");
        }
        catch (Exception ex)
        {
            ShowWindow();
            ShowError(UserMessage(ex));
        }
    }

    private void UpdateWindowMinimumSize()
    {
        var scale = Root.XamlRoot?.RasterizationScale ??
            GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (Math.Abs(scale - _windowScale) < 0.001 || AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        _windowScale = scale;
        // Presenter limits include the native frame; the content needs 720 × 440 DIPs.
        var frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(720 * scale) + frameWidth;
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(440 * scale) + frameHeight;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private void Refresh()
    {
        if (_session is null) return;
        var status = _session.Connected ? "Connected" : _session.Status;
        ConnectionStatus.Text = SettingsConnectionStatus.Text = status;
        SidebarDeviceName.Text = _session.Settings.DeviceName;
        var styleName = _session.State switch
        {
            SessionState.Connected or SessionState.Paused => "HealthyStatusDot",
            SessionState.Connecting => "ConnectingStatusDot",
            SessionState.Reconnecting => "CautionStatusDot",
            SessionState.Failed or SessionState.SignInRequired or SessionState.DeviceRemoved => "ErrorStatusDot",
            _ => "StatusDot"
        };
        SidebarStatusDot.Style = SettingsStatusDot.Style = (Style)Root.Resources[styleName];
        AutomationProperties.SetName(StatusIdentity, $"{_session.Settings.DeviceName}. {status}");
        ToolTipService.SetToolTip(StatusIdentity, $"{_session.Settings.DeviceName} · {status}");
        ConnectionDetail.Text = _session.Detail;
        var hasConnectionError = new[] { ServerError, DeviceError, TokenError, ConnectionError }.Any(error => error.Visibility == Visibility.Visible);
        ConnectionDetail.Visibility = hasConnectionError || _session.Connected && !_session.Paused ? Visibility.Collapsed : Visibility.Visible;
        LastReport.Text = _session.LastReported is { } last ? $"Last reported at {last:HH:mm:ss}" : "No sensors reported yet";
        LastReport.Visibility = _session.IsRegistered ? Visibility.Visible : Visibility.Collapsed;
        SensorsPage.Update(_session.Settings, _session.Values);
        NotificationsPage.Update(_session.Settings, _session.NotificationsSupported, _session.IsRegistered,
            _session.RemoteMessagesConnected, _session.RemoteMessageStatus);
        ControlsPage.Update(_session.Settings, _session.IsRegistered,
            _session.RemoteMessagesConnected, _session.RemoteMessageStatus);
        RefreshConnectionAction();
        _tray.CanOpenHomeAssistant = !string.IsNullOrWhiteSpace(_session.Settings.ServerUrl);
        _tray.SetStatus(_session.Connected && _session.Paused ? "Connected · sensors disabled" : _session.Status);
    }

    private void Nav_Click(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { Tag: string page }) SelectPage(page);
    }

    private void SelectPage(string page)
    {
        HideToken();
        if (page != "notifications") NotificationsPage.ClearTransientFeedback();
        SensorsNav.IsChecked = page == "sensors";
        NotificationsNav.IsChecked = page == "notifications";
        ControlsNav.IsChecked = page == "controls";
        SettingsNav.IsChecked = page == "settings";
        NotificationsPage.Visibility = page == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        ControlsPage.Visibility = page == "controls" ? Visibility.Visible : Visibility.Collapsed;
        SensorsPage.Visibility = page == "sensors" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            "notifications" => "Notifications",
            "controls" => "Controls",
            "settings" => "Settings",
            _ => "Sensors"
        };
    }

    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        if (_session is null || _connecting) return;
        _connecting = true;
        RefreshConnectionAction();
        ServerBox.IsEnabled = DeviceBox.IsEnabled = TokenBox.IsEnabled = RevealTokenButton.IsEnabled = false;
        ClearConnectionErrors();
        try
        {
            await _session.ConnectAsync(ServerBox.Text, DeviceBox.Text, TokenBox.Password);
            HideToken();
            TokenBox.Password = "";
            TokenBox.PlaceholderText = "Saved token";
            ServerBox.IsReadOnly = DeviceBox.IsReadOnly = _session.IsRegistered;
            await LoadDashboardsAsync(showErrors: false);
        }
        catch (Exception ex) { ShowConnectionError(ex); }
        finally
        {
            _connecting = false;
            ServerBox.IsEnabled = DeviceBox.IsEnabled = TokenBox.IsEnabled = RevealTokenButton.IsEnabled = true;
            Refresh();
        }
    }

    private void ConnectionField_Changed(object sender, TextChangedEventArgs args) => ConnectionInputChanged();
    private void Token_Changed(object sender, RoutedEventArgs args) => ConnectionInputChanged();

    private void ConnectionInputChanged()
    {
        if (_loading) return;
        ClearConnectionErrors();
        RefreshConnectionAction();
    }

    private void ClearConnectionErrors()
    {
        foreach (var error in new[] { ServerError, DeviceError, TokenError, ConnectionError })
        {
            error.Text = "";
            error.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowConnectionError(Exception error)
    {
        var field = error switch
        {
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } or
                ArgumentException { ParamName: "token" } => TokenError,
            ArgumentException { ParamName: "deviceName" } => DeviceError,
            HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or
                InvalidDataException or ArgumentException => ServerError,
            _ => ConnectionError
        };
        field.Text = UserMessage(error);
        field.Visibility = Visibility.Visible;
    }

    private void RefreshConnectionAction()
    {
        if (_session is null || SaveConnectionButton is null) return;
        var changed = !string.IsNullOrWhiteSpace(TokenBox.Password) && TokenBox.Password.Trim() != _session.GetSavedAccessToken();
        SaveConnectionButton.Content = _connecting ? "Connecting…" : !_session.IsRegistered ? "Connect" : changed ? "Save" : "Reconnect";
        SaveConnectionButton.Visibility = _session.Connected && !changed && !_connecting ? Visibility.Collapsed : Visibility.Visible;
        SaveConnectionButton.IsEnabled = !_connecting;
    }

    private async void RefreshDashboards_Click(object sender, RoutedEventArgs args) =>
        await LoadDashboardsAsync(showErrors: true);

    private async void Dashboard_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingDashboards || _session is null || DashboardBox.SelectedItem is not HomeAssistantDashboard dashboard) return;
        try { await _session.SetHomeAssistantPathAsync(dashboard.Path); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
    }

    private async Task LoadDashboardsAsync(bool showErrors)
    {
        if (_session is null || !_session.IsRegistered || _loadingDashboards) return;
        _loadingDashboards = true;
        DashboardBox.IsEnabled = RefreshDashboardsButton.IsEnabled = false;
        var savedPath = _session.Settings.HomeAssistantPath ?? "";
        ShowDashboardChoices([], savedPath);
        try
        {
            ShowDashboardChoices(await _session.GetDashboardsAsync(), savedPath);
        }
        catch (Exception ex)
        {
            AppLog.Write("Dashboard list", ex.GetType().Name);
            if (showErrors) ShowError(UserMessage(ex));
        }
        finally
        {
            _loadingDashboards = false;
            DashboardBox.IsEnabled = RefreshDashboardsButton.IsEnabled = _session.IsRegistered;
        }
    }

    private void ShowDashboardChoices(IEnumerable<HomeAssistantDashboard> dashboards, string savedPath)
    {
        var choices = new List<HomeAssistantDashboard> { new("Default Home Assistant page", "") };
        choices.AddRange(dashboards);
        if (savedPath.Length > 0 && choices.All(choice => !string.Equals(choice.Path, savedPath, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new($"Saved dashboard ({savedPath})", savedPath));
        DashboardBox.ItemsSource = choices;
        DashboardBox.SelectedItem = choices.First(choice =>
            string.Equals(choice.Path, savedPath, StringComparison.OrdinalIgnoreCase));
    }

    private async void OpenAbout_Click(object sender, RoutedEventArgs args)
    {
        HideToken();
        AboutDialog.XamlRoot = Root.XamlRoot;
        await AboutDialog.ShowAsync();
    }

    private void CloseAboutDialog_Click(object sender, RoutedEventArgs args) => AboutDialog.Hide();

    private void AboutDialog_KeyDown(object sender, KeyRoutedEventArgs args) =>
        DialogLayout.CloseOnEscape(AboutDialog, args);

    private async void Sensor_Changed(string id, bool enabled)
    {
        if (_session is null) return;
        SensorsPage.SetSensorBusy(id, true);
        try { await _session.SetEnabledAsync(id, enabled); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally
        {
            SensorsPage.SetSensorBusy(id, false);
            Refresh();
        }
    }

    private void RevealToken_Click(object sender, RoutedEventArgs e)
    {
        if (TokenBox.PasswordRevealMode == PasswordRevealMode.Visible) { HideToken(); return; }
        if (string.IsNullOrEmpty(TokenBox.Password)) TokenBox.Password = _session?.GetSavedAccessToken() ?? "";
        if (string.IsNullOrEmpty(TokenBox.Password)) return;
        TokenBox.PasswordRevealMode = PasswordRevealMode.Visible;
        ToolTipService.SetToolTip(RevealTokenButton, "Hide access token");
        AutomationProperties.SetName(RevealTokenButton, "Hide access token");
    }

    private void HideToken()
    {
        TokenBox.PasswordRevealMode = PasswordRevealMode.Hidden;
        ToolTipService.SetToolTip(RevealTokenButton, "Show access token");
        AutomationProperties.SetName(RevealTokenButton, "Show access token");
    }

    private async void Reporting_Changed(bool enabled)
    {
        if (_session is null) return;
        SensorsPage.SetMasterBusy(true);
        try { await _session.SetPausedAsync(!enabled); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally
        {
            SensorsPage.SetMasterBusy(false);
            Refresh();
        }
    }

    private async void Notifications_Changed(bool enabled)
    {
        if (_session is null) return;
        await RunNotificationActionAsync(() => _session.SetNotificationsEnabledAsync(enabled));
    }

    private async void NotificationSound_Changed(bool sound)
    {
        if (_session is null) return;
        await RunNotificationActionAsync(() => _session.SetNotificationSoundAsync(sound));
    }

    private async void TestNotification_Requested()
    {
        if (_session is null) return;
        await RunNotificationActionAsync(_session.TestNotificationAsync, isTest: true);
    }

    private async Task RunNotificationActionAsync(Func<Task> action, bool isTest = false)
    {
        NotificationsPage.SetBusy(true);
        if (isTest)
        {
            ErrorBar.IsOpen = false;
            NotificationsPage.BeginTest();
        }
        try
        {
            await action();
            if (isTest) NotificationsPage.ShowTestResult(accepted: true);
        }
        catch (Exception ex)
        {
            if (isTest) NotificationsPage.ShowTestResult(accepted: false);
            ShowError(UserMessage(ex));
        }
        finally
        {
            NotificationsPage.SetBusy(false);
            Refresh();
        }
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _session is null || ThemeBox.SelectedItem is not string theme) return;
        ApplyTheme(theme);
        try { await _session.SetThemeAsync(theme); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
    }

    private void ApplyTheme(string theme) => Root.RequestedTheme = theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void UpdateTitleBar()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var enabled = dark ? 1 : 0;
        var background = dark ? 0x202020 : 0xF3F3F3;
        var foreground = dark ? 0xFFFFFF : 0x000000;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);

    private void Startup_Toggled(object? sender, EventArgs e)
    {
        if (_loading) return;
        var enabled = StartupRow.IsOn;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("HassConnect", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("HassConnect", false);
        }
        catch (Exception ex)
        {
            StartupRow.IsOn = !enabled;
            ShowError(UserMessage(ex));
        }
    }

    private async void PcControl_Changed(bool enabled)
    {
        if (_session is null) return;
        ControlsPage.SetMasterBusy(true);
        try { await _session.SetPcControlEnabledAsync(enabled); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { ControlsPage.SetMasterBusy(false); Refresh(); }
    }

    private async void PcCommand_Changed(string id, bool enabled)
    {
        if (_session is null) return;
        ControlsPage.SetCommandBusy(id, true);
        try { await _session.SetPcCommandEnabledAsync(id, enabled); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { ControlsPage.SetCommandBusy(id, false); Refresh(); }
    }

    private async void CustomCommandEnabled_Changed(string id, bool enabled)
    {
        if (_session is null) return;
        ControlsPage.SetCommandBusy(id, true);
        try { await _session.SetCustomCommandEnabledAsync(id, enabled); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { ControlsPage.SetCommandBusy(id, false); Refresh(); }
    }

    private async Task ShowCustomCommandDialogAsync(string? id)
    {
        if (_session is null || _customCommandDialogOpen) return;
        _customCommandDialogOpen = true;
        try { await ShowCustomCommandDialogCoreAsync(id); }
        finally
        {
            _editingCustomCommand = null;
            _customCommandDialogOpen = false;
        }
    }

    private async Task ShowCustomCommandDialogCoreAsync(string? id)
    {
        if (_session is null) return;
        var existing = id is null
            ? null
            : _session.Settings.CustomCommands.SingleOrDefault(command => command.Id == id);
        if (id is not null && existing is null) return;

        CustomCommandDialog.XamlRoot = Root.XamlRoot;
        CustomCommandDialog.Title = existing is null ? "Add custom command" : "Edit custom command";
        SaveCustomCommandButton.Content = existing is null ? "Add" : "Save";
        DeleteCustomCommandButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        CustomCommandNameBox.Text = existing?.Name ?? "";
        CustomCommandIdBox.Text = existing?.Id ?? "command_custom_";
        CustomCommandIdBox.IsReadOnly = existing is not null;
        CustomCommandExecutableBox.Text = existing?.ExecutablePath ?? "";
        CustomCommandArgumentsBox.Text = existing is null ? "" : string.Join(Environment.NewLine, existing.Arguments);
        CustomCommandError.Visibility = Visibility.Collapsed;
        CustomCommandTestStatus.Visibility = Visibility.Collapsed;
        _editingCustomCommand = existing;
        _customCommandDeleteRequested = false;
        _customCommandSaving = false;
        _customCommandTesting = false;
        SetCustomCommandDialogEnabled(true);
        while (true)
        {
            await CustomCommandDialog.ShowAsync();
            if (!_customCommandDeleteRequested) return;

            var confirmed = await DialogLayout.ShowConfirmationAsync(
                Root.XamlRoot,
                "Delete custom command?",
                $"Delete “{existing!.Name}”? Home Assistant calls using {existing.Id} will stop working.",
                "Delete",
                defaultToPrimary: false,
                destructive: true);
            if (!confirmed)
            {
                _customCommandDeleteRequested = false;
                continue;
            }

            try { await _session.RemoveCustomCommandAsync(existing.Id); }
            catch (Exception ex) { ShowError(UserMessage(ex)); }
            finally { Refresh(); }
            return;
        }
    }

    private async void SaveCustomCommand_Click(object sender, RoutedEventArgs args) =>
        await SaveCustomCommandAsync();

    private void DeleteCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        if (_editingCustomCommand is null || _customCommandSaving) return;
        _customCommandDeleteRequested = true;
        CustomCommandDialog.Hide();
    }

    private void CancelCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        if (!_customCommandSaving) CustomCommandDialog.Hide();
    }

    private async void CustomCommandDialog_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_customCommandSaving || _customCommandTesting) return;
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            CustomCommandDialog.Hide();
            return;
        }
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        if (ReferenceEquals(focused, CustomCommandArgumentsBox) || focused is Button) return;
        args.Handled = true;
        await SaveCustomCommandAsync();
    }

    private async Task SaveCustomCommandAsync()
    {
        if (_session is null || _customCommandSaving) return;

        _customCommandSaving = true;
        SetCustomCommandDialogEnabled(false);
        try
        {
            var command = ReadCustomCommandDialog(_editingCustomCommand?.Enabled ?? true);
            if (_editingCustomCommand is null && _session.Settings.CustomCommands.Any(item => item.Id == command.Id))
                throw new ArgumentException("That Home Assistant command is already configured.", "id");
            await _session.SaveCustomCommandAsync(command);
            CustomCommandError.Visibility = Visibility.Collapsed;
            Refresh();
            CustomCommandDialog.Hide();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            CustomCommandError.Text = ex.Message;
            CustomCommandError.Visibility = Visibility.Visible;
            FocusInvalidCustomCommandField(ex);
        }
        finally
        {
            _customCommandSaving = false;
            SetCustomCommandDialogEnabled(true);
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
        var arguments = CustomCommandArgumentsBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
        _customCommandTesting = true;
        SetCustomCommandDialogEnabled(false);
        CustomCommandError.Visibility = Visibility.Collapsed;
        CustomCommandTestStatus.Visibility = Visibility.Collapsed;
        try
        {
            var command = ReadCustomCommandDialog(_editingCustomCommand?.Enabled ?? true);
            await _session.TestCustomCommandAsync(command);
            CustomCommandTestStatus.Text = "Started. Confirm it behaved as expected before saving.";
            CustomCommandTestStatus.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            CustomCommandError.Text = ex.Message;
            CustomCommandError.Visibility = Visibility.Visible;
            FocusInvalidCustomCommandField(ex);
        }
        finally
        {
            _customCommandTesting = false;
            SetCustomCommandDialogEnabled(true);
        }
    }

    private async void BrowseCustomCommand_Click(object sender, RoutedEventArgs args)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) CustomCommandExecutableBox.Text = file.Path;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", AppLog.DataDirectory) { UseShellExecute = true });
    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        NotificationsPage.ClearTransientFeedback();
        _tray.Dispose();
        try
        {
            if (_session is not null) await _session.DisposeAsync();
        }
        catch (Exception ex) { AppLog.Write("Application shutdown", ex.GetType().Name); }
        finally { QuitRequested?.Invoke(); }
    }

    private void ShowError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; }
    private static string UserMessage(Exception ex)
    {
        AppLog.Write("Operation failed", ex.GetType().Name);
        return ex switch
        {
            ArgumentException { ParamName: "token" } => "Paste a complete access token without spaces or line breaks.",
            ArgumentException { ParamName: "deviceName" } => "Enter a device name.",
            ArgumentException or InvalidOperationException => ex.Message,
            UnauthorizedAccessException => "Windows denied access to the app's files. Check the folder permissions.",
            IOException => "The app could not save or read its files. Check free disk space and folder access.",
            _ => ConnectionErrorMessage.Describe(ex)
        };
    }
}
