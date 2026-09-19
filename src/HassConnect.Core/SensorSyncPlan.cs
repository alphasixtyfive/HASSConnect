namespace HassConnect.Core;

/// <summary>Resolves local choices against registered Home Assistant entities before any sensor is read.</summary>
public sealed record SensorSyncPlan(
    IReadOnlySet<string> EnabledIds,
    IReadOnlyList<SensorDefinition> SensorsToRead,
    IReadOnlySet<string> SensorsToRegister)
{
    public static SensorSyncPlan Create(Settings settings, IReadOnlyList<SensorDefinition> supported,
        IReadOnlyDictionary<string, bool> remoteEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(remoteEnabled);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var readings = new List<SensorDefinition>();
        var registrations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sensor in supported)
        {
            var registered = remoteEnabled.TryGetValue(sensor.Id, out var selected);
            if (!(registered ? selected : settings.EnabledSensors.Contains(sensor.Id))) continue;
            enabled.Add(sensor.Id);
            if (!settings.ShareSensors) continue;
            readings.Add(sensor);
            if (!registered) registrations.Add(sensor.Id);
        }

        return new(enabled, readings, registrations);
    }
}
