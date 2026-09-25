using System.Text.Json;

namespace HassConnect.Core;

public enum PcCommandKind
{
    Lock = 0,
    MonitorSleep = 1,
    Sleep = 2,
    Media = 3,
    VolumeMute = 4,
    VolumeLevel = 5,
    Custom = 6,
    Shutdown = 7,
    Restart = 8,
    MonitorWake = 9
}
public enum MediaCommand { PlayPause, Next, Previous, Stop }

public static class PcCommandIds
{
    public const string Lock = "command_lock";
    public const string MonitorSleep = "command_monitor_sleep";
    public const string MonitorWake = "command_monitor_wake";
    public const string Sleep = "command_sleep";
    public const string Shutdown = "command_shutdown";
    public const string Restart = "command_restart";
    public const string Media = "command_media";
    public const string VolumeMute = "command_volume_mute";
    public const string VolumeLevel = "command_volume_level";

    public static IReadOnlyList<string> All { get; } =
    [
        Lock, MonitorSleep, MonitorWake, Sleep, Shutdown, Restart, Media, VolumeMute, VolumeLevel
    ];

    public static bool IsKnown(string id) => All.Contains(id, StringComparer.Ordinal);
}

public sealed record PcCommand(
    PcCommandKind Kind,
    MediaCommand? Media = null,
    int? VolumeLevel = null,
    string? CustomId = null)
{
    public bool RequiresDeferredExecution =>
        Kind is PcCommandKind.Sleep or PcCommandKind.Shutdown or PcCommandKind.Restart;

    public string Id => Kind switch
    {
        PcCommandKind.Lock => PcCommandIds.Lock,
        PcCommandKind.MonitorSleep => PcCommandIds.MonitorSleep,
        PcCommandKind.MonitorWake => PcCommandIds.MonitorWake,
        PcCommandKind.Sleep => PcCommandIds.Sleep,
        PcCommandKind.Shutdown => PcCommandIds.Shutdown,
        PcCommandKind.Restart => PcCommandIds.Restart,
        PcCommandKind.Media => PcCommandIds.Media,
        PcCommandKind.VolumeMute => PcCommandIds.VolumeMute,
        PcCommandKind.VolumeLevel => PcCommandIds.VolumeLevel,
        PcCommandKind.Custom when CustomCommandPolicy.IsCustomCommandId(CustomId) => CustomId!,
        _ => throw new InvalidOperationException("The PC command kind is invalid.")
    };

    public static PcCommand? Parse(string message, JsonElement? data)
    {
        return message switch
        {
            PcCommandIds.Lock => new(PcCommandKind.Lock),
            PcCommandIds.MonitorSleep => new(PcCommandKind.MonitorSleep),
            PcCommandIds.MonitorWake => new(PcCommandKind.MonitorWake),
            PcCommandIds.Sleep => new(PcCommandKind.Sleep),
            PcCommandIds.Shutdown => new(PcCommandKind.Shutdown),
            PcCommandIds.Restart => new(PcCommandKind.Restart),
            PcCommandIds.Media => new(PcCommandKind.Media, ParseMedia(Text(data, "media_command"))),
            PcCommandIds.VolumeMute => new(PcCommandKind.VolumeMute),
            PcCommandIds.VolumeLevel => new(PcCommandKind.VolumeLevel, VolumeLevel: ParseVolume(data)),
            _ when CustomCommandPolicy.IsCustomCommandId(message) =>
                new(PcCommandKind.Custom, CustomId: message),
            _ => null
        };
    }

    private static MediaCommand ParseMedia(string? value) => value switch
    {
        "play_pause" => MediaCommand.PlayPause,
        "next" => MediaCommand.Next,
        "previous" => MediaCommand.Previous,
        "stop" => MediaCommand.Stop,
        _ => throw new InvalidDataException("Media commands require play_pause, next, previous, or stop.")
    };

    private static int ParseVolume(JsonElement? data)
    {
        if (data is not JsonElement value || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("volume_level", out var level) ||
            !level.TryGetInt32(out var result) || result is < 0 or > 100)
            throw new InvalidDataException("Volume level must be a whole number from 0 to 100.");
        return result;
    }

    private static string? Text(JsonElement? data, string property)
    {
        if (data is not JsonElement value || value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            return null;
        return item.GetString();
    }
}
