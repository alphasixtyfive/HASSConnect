using System.Text.Json;
using System.Text.Json.Serialization;

namespace HassConnect.Core;

/// <summary>The preferences that can be copied to another PC.</summary>
public sealed record SettingsTransfer
{
    public const int MaximumBytes = 256 * 1024;
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        MaxDepth = 12
    };
    private static readonly HashSet<string> KnownSensorIds = SensorDefinition.Available
        .Select(sensor => sensor.Id).ToHashSet(StringComparer.Ordinal);

    public required int FormatVersion { get; init; }
    public required string Theme { get; init; }
    public required bool ShareSensors { get; init; }
    public required bool NotificationSound { get; init; }
    public required IReadOnlyList<string> EnabledSensors { get; init; }
    public required IReadOnlyList<QuickActionDefinition> QuickActions { get; init; }

    public static byte[] Export(Settings settings) => JsonSerializer.SerializeToUtf8Bytes(new SettingsTransfer
    {
        FormatVersion = CurrentVersion,
        Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System",
        ShareSensors = settings.ShareSensors,
        NotificationSound = settings.NotificationSound,
        EnabledSensors = settings.EnabledSensors.Where(IsKnownSensor).Order(StringComparer.Ordinal).ToArray(),
        QuickActions = settings.QuickActions
    }, Json);

    public static SettingsTransfer Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The settings file is too large.");
        if (bytes.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
        SettingsTransfer? transfer;
        try { transfer = JsonSerializer.Deserialize<SettingsTransfer>(bytes, Json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Choose a JSON settings file exported by HASS Connect.", ex);
        }
        if (transfer is null) throw new InvalidDataException("The settings file is empty.");
        if (transfer.FormatVersion != CurrentVersion)
            throw new InvalidDataException("This settings file was created in a different format version.");
        if (transfer.Theme is not ("System" or "Light" or "Dark") || transfer.EnabledSensors is null ||
            transfer.QuickActions is null || transfer.EnabledSensors.Any(id => !IsKnownSensor(id)))
            throw new InvalidDataException("The settings file contains unsupported preferences.");

        return transfer with { QuickActions = QuickActionPolicy.ValidateCollection(transfer.QuickActions) };
    }

    public Settings ApplyTo(Settings current)
    {
        var enabled = EnabledSensors.ToHashSet(StringComparer.Ordinal);
        var pending = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in current.EnabledSensors.Concat(current.PendingSensorChanges.Keys).Concat(enabled))
            if (IsKnownSensor(id)) pending[id] = enabled.Contains(id);
        return current with
        {
            Theme = Theme,
            ShareSensors = ShareSensors,
            NotificationSound = NotificationSound,
            EnabledSensors = enabled,
            PendingSensorChanges = pending,
            QuickActions = QuickActions
        };
    }

    private static bool IsKnownSensor(string? id) =>
        id is not null && (KnownSensorIds.Contains(id) || DriveSensor.TryParse(id, out _, out _));
}
