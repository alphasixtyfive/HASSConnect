using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class PcCommandTests
{
    [Fact]
    public void CommandIdentifiersAreUniqueAndComplete()
    {
        Assert.Equal(8, PcCommandIds.All.Count);
        Assert.Equal(PcCommandIds.All.Count, PcCommandIds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PcCommandIds.All, id => Assert.True(PcCommandIds.IsKnown(id)));
    }

    [Theory]
    [InlineData("command_lock", PcCommandKind.Lock)]
    [InlineData("command_monitor_sleep", PcCommandKind.MonitorSleep)]
    [InlineData("command_sleep", PcCommandKind.Sleep)]
    [InlineData("command_shutdown", PcCommandKind.Shutdown)]
    [InlineData("command_restart", PcCommandKind.Restart)]
    [InlineData("command_volume_mute", PcCommandKind.VolumeMute)]
    public void ParsesSimpleCommands(string message, PcCommandKind kind)
    {
        using var json = JsonDocument.Parse($$"""{"message":"{{message}}"}""");
        Assert.Equal(kind, NotificationMessage.Parse(json.RootElement).Command?.Kind);
    }

    [Theory]
    [InlineData("play_pause", MediaCommand.PlayPause)]
    [InlineData("next", MediaCommand.Next)]
    [InlineData("previous", MediaCommand.Previous)]
    [InlineData("stop", MediaCommand.Stop)]
    public void ParsesMediaCommands(string action, MediaCommand expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            message = "command_media",
            data = new { media_command = action }
        }));
        Assert.Equal(expected, NotificationMessage.Parse(json.RootElement).Command?.Media);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(100)]
    public void ParsesVolumeLevel(int level)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            message = "command_volume_level",
            data = new { volume_level = level }
        }));
        Assert.Equal(level, NotificationMessage.Parse(json.RootElement).Command?.VolumeLevel);
    }

    [Theory]
    [InlineData("{\"message\":\"command_media\",\"data\":{\"media_command\":\"mute\"}}")]
    [InlineData("{\"message\":\"command_volume_level\",\"data\":{\"volume_level\":101}}")]
    public void RejectsInvalidArguments(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidDataException>(() => NotificationMessage.Parse(json.RootElement));
    }

    [Fact]
    public void LeavesOrdinaryNotificationsAlone()
    {
        using var json = JsonDocument.Parse("""{"message":"Hello"}""");
        Assert.Null(NotificationMessage.Parse(json.RootElement).Command);
    }

    [Theory]
    [InlineData(PcCommandKind.Lock, false)]
    [InlineData(PcCommandKind.Media, false)]
    [InlineData(PcCommandKind.Sleep, true)]
    [InlineData(PcCommandKind.Shutdown, true)]
    [InlineData(PcCommandKind.Restart, true)]
    public void DefersOnlyCommandsThatCanInterruptDelivery(PcCommandKind kind, bool expected) =>
        Assert.Equal(expected, new PcCommand(kind).RequiresDeferredExecution);

    [Fact]
    public void ParsesCustomCommandWithoutAcceptingRemoteArguments()
    {
        using var json = JsonDocument.Parse("""
            {"message":"command_custom_open_music","data":{"executable":"cmd.exe","arguments":["/c","whoami"]}}
            """);
        var command = NotificationMessage.Parse(json.RootElement).Command;
        Assert.Equal(PcCommandKind.Custom, command?.Kind);
        Assert.Equal("command_custom_open_music", command?.Id);
        Assert.Null(command?.Media);
        Assert.Null(command?.VolumeLevel);
    }
}
