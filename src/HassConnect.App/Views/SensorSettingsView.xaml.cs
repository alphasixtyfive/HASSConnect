using HassConnect.App.Services;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class SensorSettingsView : UserControl
{
    private readonly Dictionary<string, SettingsRow> _rows = [];
    private readonly HashSet<string> _supported = WindowsSensors.Available.Select(sensor => sensor.Id).ToHashSet();
    private readonly HashSet<string> _busy = [];
    private bool _masterBusy;
    private bool _sharingEnabled;

    public event Action<string, bool>? SensorChanged;
    public event Action<bool>? SharingChanged;

    public SensorSettingsView()
    {
        InitializeComponent();
        foreach (var group in SensorDefinition.Available.GroupBy(sensor => sensor.Group))
        {
            var rows = new StackPanel();
            foreach (var sensor in group)
            {
                var row = new SettingsRow { Title = sensor.Name, Glyph = IconFor(sensor.Id) };
                row.IsToggleEnabled = _supported.Contains(sensor.Id);
                ToolTipService.SetToolTip(row, sensor.Description);
                AutomationProperties.SetHelpText(row, sensor.Description);
                row.ToggleChanged += (_, _) => SensorChanged?.Invoke(sensor.Id, row.IsOn);
                _rows.Add(sensor.Id, row);
                rows.Children.Add(row);
            }
            if (rows.Children.LastOrDefault() is SettingsRow last) last.ShowDivider = false;
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SettingsSectionHeader"]
            });
            var card = new ContentControl { Template = (ControlTemplate)Application.Current.Resources["SettingsGroupCard"] };
            card.Content = rows;
            section.Children.Add(card);
            SensorGroups.Children.Add(section);
        }
    }

    public void Update(Settings settings, IReadOnlyDictionary<string, object?> values)
    {
        _sharingEnabled = settings.ShareSensors;
        if (!_masterBusy) MasterRow.IsOn = _sharingEnabled;
        foreach (var sensor in SensorDefinition.Available)
        {
            var row = _rows[sensor.Id];
            bool supported = _supported.Contains(sensor.Id);
            bool enabled = settings.EnabledSensors.Contains(sensor.Id);
            if (!_busy.Contains(sensor.Id)) row.IsOn = enabled;
            row.Opacity = !_sharingEnabled || !supported ? 0.55 : 1;
            row.ValueText = !supported ? "Not available on this PC" : !_sharingEnabled ? "Disabled" : !enabled ? "Off" :
                values.TryGetValue(sensor.Id, out var value) ? FormatValue(sensor, value) : "Waiting…";
        }
        RefreshEnabledState();
    }

    public void SetSensorBusy(string id, bool busy)
    {
        if (busy) _busy.Add(id); else _busy.Remove(id);
        RefreshEnabledState();
    }

    public void SetMasterBusy(bool busy)
    {
        _masterBusy = busy;
        RefreshEnabledState();
    }

    private void RefreshEnabledState()
    {
        MasterRow.IsToggleEnabled = !_masterBusy;
        foreach (var (id, row) in _rows)
            row.IsToggleEnabled = _sharingEnabled && !_masterBusy && !_busy.Contains(id) && _supported.Contains(id);
    }

    private void MasterRow_Changed(object? sender, EventArgs args) => SharingChanged?.Invoke(MasterRow.IsOn);

    private static string IconFor(string id) => id switch
    {
        "idle_time" => "\uE916",
        "uptime" => "\uE823",
        "cpu_usage" => "\uE950",
        "memory_usage" => "\uE964",
        "session_locked" => "\uE72E",
        "display_state" => "\uE7F4",
        "last_seen" => "\uE73E",
        "battery_level" => "\uE850",
        "battery_charging" => "\uE83E",
        "microphone_in_use" => "\uE720",
        "webcam_in_use" => "\uE8B8",
        "disk_usage" or "disk_free_space" => "\uEDA2",
        "ip_address" => "\uE774",
        "network_adapter" => "\uE839",
        "download_speed" => "\uE896",
        "upload_speed" => "\uE898",
        _ => "\uE9D9"
    };

    private static string FormatValue(SensorDefinition sensor, object? value)
    {
        if (value is null) return "Unavailable";
        if (sensor.DeviceClass == "timestamp" && value is string timestamp && DateTimeOffset.TryParse(timestamp, out var reported))
            return reported.ToLocalTime().ToString("HH:mm:ss");
        if (sensor.Id == "display_state" && value is string display)
            return display switch { "on" => "On", "off" => "Off", "dimmed" => "Dimmed", _ => "Waiting…" };
        if (value is string text) return text == "unknown" ? sensor.Id == "cpu_usage" ? "Sampling…" : "Unavailable" : text;
        if (value is bool state) return state ? sensor.OnText ?? "On" : sensor.OffText ?? "Off";
        if (sensor.Unit == "%") return $"{Convert.ToDouble(value):0.#}%";
        if (sensor.Unit == "Mbit/s") return $"{Convert.ToDouble(value):0.00} Mbit/s";
        if (sensor.Unit == "GB") return $"{Convert.ToDouble(value):0.0} GB";
        var duration = TimeSpan.FromSeconds(Convert.ToDouble(value));
        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m" :
            duration.TotalHours >= 1 ? $"{duration.Hours}h {duration.Minutes}m" :
            duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds}s" : $"{duration.Seconds}s";
    }
}
