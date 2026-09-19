using HassConnect.App.Services;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class SensorSettingsView : UserControl
{
    private readonly Dictionary<string, SettingsRow> _rows = [];
    private readonly Dictionary<string, RowSpec> _rowSpecs = [];
    private HashSet<string> _supported = [];
    private IReadOnlyList<string> _rowSignature = [];
    private readonly HashSet<string> _busy = [];
    private bool _masterBusy;
    private bool _sharingEnabled;

    public event Action<string, bool>? SensorChanged;
    public event Action<string, IReadOnlyList<string>, bool>? DriveSensorsChanged;
    public event Action<bool>? SharingChanged;

    public SensorSettingsView()
    {
        InitializeComponent();
        RebuildRows(WindowsSensors.Available);
    }

    public void Update(Settings settings, IReadOnlyDictionary<string, object?> values)
    {
        var available = WindowsSensors.AvailableFor(settings);
        _supported = available.Select(sensor => sensor.Id).ToHashSet(StringComparer.Ordinal);
        var specs = BuildRowSpecs(available);
        if (!_rowSignature.SequenceEqual(specs.Select(spec => spec.Key))) RebuildRows(available);

        _sharingEnabled = settings.ShareSensors;
        if (!_masterBusy) MasterRow.IsOn = _sharingEnabled;
        foreach (var spec in _rowSpecs.Values)
        {
            var row = _rows[spec.Key];
            var supported = spec.Sensors.All(sensor => _supported.Contains(sensor.Id));
            var enabled = spec.Sensors.Count(sensor => settings.EnabledSensors.Contains(sensor.Id));
            if (!_busy.Contains(spec.Key)) row.IsOn = enabled > 0;
            row.Opacity = !_sharingEnabled || !supported ? 0.55 : 1;
            row.ValueText = !supported ? "Not available on this PC" : !_sharingEnabled ? "Disabled" :
                enabled == 0 ? "Off" : spec.Sensors.Count == 1
                    ? FormatSensorValue(spec.Sensors[0], values)
                    : FormatDriveValue(spec, enabled, values);
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

    private void RebuildRows(IReadOnlyList<SensorDefinition> available)
    {
        SensorGroups.Children.Clear();
        _rows.Clear();
        _rowSpecs.Clear();
        var specs = BuildRowSpecs(available);
        _rowSignature = specs.Select(spec => spec.Key).ToArray();

        foreach (var group in specs.GroupBy(spec => spec.Group))
        {
            var rows = new StackPanel();
            foreach (var spec in group)
            {
                var row = new SettingsRow { Title = spec.Name, Glyph = spec.Glyph };
                ToolTipService.SetToolTip(row, spec.Description);
                AutomationProperties.SetHelpText(row, spec.Description);
                row.ToggleChanged += (_, _) =>
                {
                    if (spec.Sensors.Count == 1) SensorChanged?.Invoke(spec.Sensors[0].Id, row.IsOn);
                    else DriveSensorsChanged?.Invoke(spec.Key, spec.Sensors.Select(sensor => sensor.Id).ToArray(), row.IsOn);
                };
                _rows.Add(spec.Key, row);
                _rowSpecs.Add(spec.Key, spec);
                rows.Children.Add(row);
            }
            if (rows.Children.LastOrDefault() is SettingsRow last) last.ShowDivider = false;
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SettingsSectionHeader"]
            });
            section.Children.Add(new ContentControl
            {
                Template = (ControlTemplate)Application.Current.Resources["SettingsGroupCard"],
                Content = rows
            });
            SensorGroups.Children.Add(section);
        }
    }

    private static List<RowSpec> BuildRowSpecs(IReadOnlyList<SensorDefinition> available)
    {
        var specs = new List<RowSpec>();
        foreach (var group in SensorDefinition.Available.Select(sensor => sensor.Group).Distinct())
        {
            foreach (var sensor in SensorDefinition.Available.Where(sensor => sensor.Group == group &&
                         sensor.Id is not "disk_usage" and not "disk_free_space"))
                specs.Add(new(sensor.Id, group, sensor.Name, sensor.Description, IconFor(sensor.Id), [sensor]));

            if (group != "Storage") continue;
            var systemSensors = SensorDefinition.Available
                .Where(sensor => sensor.Id is "disk_usage" or "disk_free_space").ToArray();
            specs.Add(new("drive:system", group,
                StorageSensors.DisplayName(StorageSensors.SystemDriveLetter, system: true),
                "Share used percentage and free space for the Windows disk as two Home Assistant sensors.",
                "\uEDA2", systemSensors));

            foreach (var drive in available
                         .Where(sensor => DriveSensor.TryParse(sensor.Id, out _, out _))
                         .GroupBy(DriveLetter)
                         .OrderBy(drive => drive.Key))
                specs.Add(new($"drive:{drive.Key}", group, StorageSensors.DisplayName(drive.Key, system: false),
                    "Share used percentage and free space for this fixed disk as two Home Assistant sensors.",
                    "\uEDA2", drive.OrderBy(sensor => sensor.Id).ToArray()));
        }
        return specs;
    }

    private static char DriveLetter(SensorDefinition sensor) =>
        DriveSensor.TryParse(sensor.Id, out var letter, out _) ? letter :
            throw new InvalidOperationException($"{sensor.Id} is not a drive sensor.");

    private void RefreshEnabledState()
    {
        MasterRow.IsToggleEnabled = !_masterBusy;
        foreach (var (key, row) in _rows)
            row.IsToggleEnabled = _sharingEnabled && !_masterBusy && !_busy.Contains(key) &&
                _rowSpecs[key].Sensors.All(sensor => _supported.Contains(sensor.Id));
    }

    private void MasterRow_Changed(object? sender, EventArgs args) => SharingChanged?.Invoke(MasterRow.IsOn);

    private static string FormatDriveValue(RowSpec spec, int enabled,
        IReadOnlyDictionary<string, object?> values)
    {
        var usage = spec.Sensors.Single(sensor => sensor.Unit == "%");
        var free = spec.Sensors.Single(sensor => sensor.Unit == "GB");
        var usageReady = TryNumber(values, usage.Id, out var usageValue);
        var freeReady = TryNumber(values, free.Id, out var freeValue);
        var value = usageReady && freeReady ? $"{usageValue:0.#}% used · {freeValue:0.0} GB free" :
            usageReady ? $"{usageValue:0.#}% used" : freeReady ? $"{freeValue:0.0} GB free" :
            values.ContainsKey(usage.Id) || values.ContainsKey(free.Id) ? "Unavailable" : "Waiting…";
        return enabled == spec.Sensors.Count ? value : $"{value} · {enabled} of 2 enabled";
    }

    private static bool TryNumber(IReadOnlyDictionary<string, object?> values, string id, out double value)
    {
        value = 0;
        return values.TryGetValue(id, out var item) && item is not null && item is not string &&
            double.TryParse(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string FormatSensorValue(SensorDefinition sensor, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue(sensor.Id, out var value)) return "Waiting…";
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
        "ip_address" => "\uE774",
        "network_adapter" => "\uE839",
        "download_speed" => "\uE896",
        "upload_speed" => "\uE898",
        _ => "\uE9D9"
    };

    private sealed record RowSpec(string Key, string Group, string Name, string Description,
        string Glyph, IReadOnlyList<SensorDefinition> Sensors);
}
