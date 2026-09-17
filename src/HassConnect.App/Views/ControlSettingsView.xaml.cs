using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class ControlSettingsView : UserControl
{
    private readonly Dictionary<string, SettingsRow> _rows = [];
    private readonly HashSet<string> _busy = [];
    private bool _masterBusy;
    private bool _enabled;
    private bool _registered;

    public event Action<bool>? EnabledChanged;
    public event Action<string, bool>? CommandChanged;

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
            section.Children.Add(new TextBlock { Text = group.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
    }

    private void MasterRow_Changed(object? sender, EventArgs args) => EnabledChanged?.Invoke(MasterRow.IsOn);

}
