using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class ControlSettingsView : UserControl
{
    private readonly Dictionary<string, SettingsRow> _rows = [];
    private readonly Dictionary<string, (SettingsRow Row, ToggleSwitch Toggle, Button Settings)> _customRows = [];
    private readonly HashSet<string> _busy = [];
    private readonly HashSet<string> _updatingCustomRows = [];
    private bool _masterBusy;
    private bool _enabled;
    private bool _registered;

    public event Action<bool>? EnabledChanged;
    public event Action<string, bool>? CommandChanged;
    public event Action? AddCustomCommandRequested;
    public event Action<string>? EditCustomCommandRequested;
    public event Action<string, bool>? CustomCommandEnabledChanged;

    public ControlSettingsView()
    {
        InitializeComponent();
        foreach (var group in PcControlCatalog.Options.GroupBy(command => command.Group))
        {
            var rows = new StackPanel();
            foreach (var command in group)
            {
                var row = new SettingsRow { Title = command.Name, Glyph = command.Glyph };
                ToolTipService.SetToolTip(row, command.Description);
                AutomationProperties.SetHelpText(row, command.Description);
                row.ToggleChanged += (_, _) => CommandChanged?.Invoke(command.Id, row.IsOn);
                _rows.Add(command.Id, row);
                rows.Children.Add(row);
            }
            if (rows.Children.LastOrDefault() is SettingsRow last) last.ShowDivider = false;
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SettingsSectionHeader"]
            });
            var card = new ContentControl
            {
                Template = (ControlTemplate)Application.Current.Resources["SettingsGroupCard"],
                Content = rows
            };
            section.Children.Add(card);
            ControlGroups.Children.Add(section);
        }
    }

    public void Update(Settings settings, bool registered, bool connected, string status)
    {
        _enabled = settings.PcControlEnabled;
        _registered = registered;
        if (!_masterBusy) MasterRow.IsOn = _enabled;
        foreach (var command in PcControlCatalog.Options)
        {
            var row = _rows[command.Id];
            if (!_busy.Contains(command.Id)) row.IsOn = settings.EnabledPcCommands.Contains(command.Id);
            row.Opacity = _enabled ? 1 : 0.55;
        }
        RebuildCustomCommands(settings.CustomCommands);
        StatusText.Text = !registered ? "Connect to Home Assistant in Settings." :
            _enabled && (!connected || status != "Connected") ? status : "";
        StatusText.Visibility = string.IsNullOrWhiteSpace(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshEnabledState();
    }

    public void SetMasterBusy(bool busy)
    {
        _masterBusy = busy;
        RefreshEnabledState();
    }

    public void SetCommandBusy(string id, bool busy)
    {
        if (busy) _busy.Add(id); else _busy.Remove(id);
        RefreshEnabledState();
    }

    private void RefreshEnabledState()
    {
        MasterRow.IsToggleEnabled = !_masterBusy && (MasterRow.IsOn || _registered);
        foreach (var (id, row) in _rows)
            row.IsToggleEnabled = _enabled && !_masterBusy && !_busy.Contains(id);
        foreach (var (id, controls) in _customRows)
        {
            var available = !_masterBusy && !_busy.Contains(id);
            controls.Toggle.IsEnabled = _enabled && available;
            controls.Settings.IsEnabled = available;
        }
        AddCustomCommandButton.IsEnabled = !_masterBusy && _customRows.Count < CustomCommandPolicy.MaximumCommands;
    }

    private void MasterRow_Changed(object? sender, EventArgs args) => EnabledChanged?.Invoke(MasterRow.IsOn);

    private void AddCustomCommand_Click(object sender, RoutedEventArgs args) => AddCustomCommandRequested?.Invoke();

    private void RebuildCustomCommands(IReadOnlyList<CustomCommandDefinition> commands)
    {
        var currentIds = commands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _customRows.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            CustomCommandRows.Children.Remove(_customRows[id].Row);
            _customRows.Remove(id);
        }

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            if (!_customRows.TryGetValue(command.Id, out var controls))
            {
                controls = CreateCustomCommandRow(command);
                _customRows.Add(command.Id, controls);
            }
            UpdateCustomCommandRow(command, controls);
            controls.Row.ShowDivider = true;
            var currentIndex = CustomCommandRows.Children.IndexOf(controls.Row);
            if (currentIndex == index) continue;
            if (currentIndex >= 0) CustomCommandRows.Children.RemoveAt(currentIndex);
            CustomCommandRows.Children.Insert(index, controls.Row);
        }
        if (CustomCommandRows.Children.LastOrDefault() is SettingsRow last) last.ShowDivider = false;
        var any = commands.Count > 0;
        NoCustomCommandsText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        CustomCommandRows.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private (SettingsRow Row, ToggleSwitch Toggle, Button Settings) CreateCustomCommandRow(
        CustomCommandDefinition command)
    {
        var toggle = new ToggleSwitch
        {
            Width = 44,
            Height = 44,
            MinWidth = 0,
            OnContent = "",
            OffContent = ""
        };
        var settings = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(settings, $"Edit {command.Name}");
        ToolTipService.SetToolTip(settings, "Edit command");
        settings.Click += (_, _) => EditCustomCommandRequested?.Invoke(command.Id);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(settings);
        actions.Children.Add(toggle);
        var row = new SettingsRow
        {
            Glyph = "\uE756",
            TrailingContent = actions
        };
        toggle.Toggled += (_, _) =>
        {
            if (!_updatingCustomRows.Contains(command.Id))
                CustomCommandEnabledChanged?.Invoke(command.Id, toggle.IsOn);
        };
        return (row, toggle, settings);
    }

    private void UpdateCustomCommandRow(
        CustomCommandDefinition command,
        (SettingsRow Row, ToggleSwitch Toggle, Button Settings) controls)
    {
        controls.Row.Title = command.Name;
        controls.Row.ValueText = command.Id;
        ToolTipService.SetToolTip(controls.Row, command.ExecutablePath);
        AutomationProperties.SetHelpText(
            controls.Row,
            $"Runs {command.ExecutablePath} with locally stored fixed arguments.");
        AutomationProperties.SetName(controls.Toggle, $"{command.Name} enabled");
        AutomationProperties.SetName(controls.Settings, $"Edit {command.Name}");
        if (controls.Toggle.IsOn == command.Enabled) return;
        _updatingCustomRows.Add(command.Id);
        try { controls.Toggle.IsOn = command.Enabled; }
        finally { _updatingCustomRows.Remove(command.Id); }
    }

}
