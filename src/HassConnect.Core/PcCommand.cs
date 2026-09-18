using System.Text.Json;

namespace HassConnect.Core;

public enum PcCommandKind { Lock, MonitorSleep, Sleep, Media, VolumeMute, VolumeLevel, Custom }
public enum MediaCommand { PlayPause, Next, Previous, Stop }

public static class PcCommandIds
{
    public const string Lock = "command_lock";
    public const string MonitorSleep = "command_monitor_sleep";
    public const string Sleep = "command_sleep";
    public const string Media = "command_media";
    public const string VolumeMute = "command_volume_mute";
    public const string VolumeLevel = "command_volume_level";

    public static IReadOnlyList<string> All { get; } =
    [
        Lock, MonitorSleep, Sleep, Media, VolumeMute, VolumeLevel
    ];

    public static bool IsKnown(string id) => All.Contains(id, StringComparer.Ordinal);
}

public sealed record PcCommand(
    PcCommandKind Kind,
    MediaCommand? Media = null,
    int? VolumeLevel = null,
    string? CustomId = null)
{
    public string Id => Kind switch
    {
        PcCommandKind.Lock => PcCommandIds.Lock,
        PcCommandKind.MonitorSleep => PcCommandIds.MonitorSleep,
        PcCommandKind.Sleep => PcCommandIds.Sleep,
        PcCommandKind.Media => PcCommandIds.Media,
        PcCommandKind.VolumeMute => PcCommandIds.VolumeMute,
        PcCommandKind.VolumeLevel => PcCommandIds.VolumeLevel,
        PcCommandKind.Custom when CustomCommandPolicy.IsCustomCommandId(CustomId) => CustomId!,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    public static PcCommand? Parse(string message, JsonElement? data)
    {
        return message switch
        {
            PcCommandIds.Lock => new(PcCommandKind.Lock),
            PcCommandIds.MonitorSleep => new(PcCommandKind.MonitorSleep),
            PcCommandIds.Sleep => new(PcCommandKind.Sleep),
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
