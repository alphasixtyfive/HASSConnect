using HassConnect.Core;

namespace HassConnect.App.Views;

internal sealed record PcControlOption(string Id, string Name, string Description, string Group, string Glyph);

internal static class PcControlCatalog
{
    public static IReadOnlyList<PcControlOption> Options { get; } =
    [
        new(PcCommandIds.Lock, "Lock PC", "Lock the current Windows session.", "System", "\uE72E"),
        new(PcCommandIds.MonitorSleep, "Turn off displays", "Put connected displays into power-saving mode.", "System", "\uE7F4"),
        new(PcCommandIds.MonitorWake, "Wake displays", "Turn on displays that Windows has powered down.", "System", "\uE714"),
        new(PcCommandIds.Sleep, "Sleep PC", "Put this PC into sleep mode.", "System", "\uE708"),
        new(PcCommandIds.Shutdown, "Shut down PC", "Close apps and turn off this PC.", "System", "\uE7E8"),
        new(PcCommandIds.Restart, "Restart PC", "Close apps and restart this PC.", "System", "\uE777"),
        new(PcCommandIds.Media, "Media playback", "Play or pause, skip tracks, or stop playback.", "Media", "\uE768"),
        new(PcCommandIds.VolumeMute, "Mute", "Toggle mute for the main audio output.", "Media", "\uE74F"),
        new(PcCommandIds.VolumeLevel, "Set volume", "Set the main audio output volume from 0 to 100.", "Media", "\uE767")
    ];
}
