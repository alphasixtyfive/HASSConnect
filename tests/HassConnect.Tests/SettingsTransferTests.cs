using System.Text;
using System.Text.Json.Nodes;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class SettingsTransferTests
{
    [Fact]
    public void RoundTripCopiesPreferencesWithoutCarryingMachineIdentityOrTrust()
    {
        var source = new Settings
        {
            DeviceId = "source-device",
            DeviceName = "Source PC",
            ServerUrl = "https://private.example/",
            HomeAssistantPath = "/dashboard",
            Theme = "Dark",
            ShareSensors = false,
            NotificationSound = false,
            EnabledSensors = ["cpu_usage", "speakers_in_use", "disk_d_usage"],
            NotificationsEnabled = true,
            PcControlEnabled = true,
            QuickAccessShortcut = new('Q', true, false, true),
            CustomCommands = [new("command_custom_example", "Example", @"C:\Example.exe", ["private-argument"])],
            QuickActions = [QuickActionPolicy.Create("light.desk", "Desk brightness", "", QuickActionPolicy.Brightness)]
        };
        var bytes = SettingsTransfer.Export(source);
        var json = JsonNode.Parse(bytes)!.AsObject();
        Assert.Equal(
            ["enabledSensors", "formatVersion", "notificationSound", "quickActions", "shareSensors", "theme"],
            json.Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(bytes));

        var destination = new Settings
        {
            DeviceId = "destination-device",
            ServerUrl = "https://destination.example/",
            HomeAssistantPath = "/my-dashboard",
            NotificationsEnabled = true,
            EnabledSensors = ["idle_time", "cpu_usage"],
            PendingSensorChanges = new() { ["uptime"] = true }
        };
        var result = SettingsTransfer.Read(bytes).ApplyTo(destination);
        Assert.Equal(source.Theme, result.Theme);
        Assert.False(result.ShareSensors);
        Assert.False(result.NotificationSound);
        Assert.True(result.EnabledSensors.SetEquals(source.EnabledSensors));
        Assert.Equal(source.QuickActions, result.QuickActions);
        Assert.True(result.PendingSensorChanges["disk_d_usage"]);
        Assert.True(result.PendingSensorChanges["cpu_usage"]);
        Assert.False(result.PendingSensorChanges["idle_time"]);
        Assert.False(result.PendingSensorChanges["uptime"]);
        Assert.Equal(destination.DeviceId, result.DeviceId);
        Assert.Equal(destination.ServerUrl, result.ServerUrl);
        Assert.Equal(destination.HomeAssistantPath, result.HomeAssistantPath);
        Assert.Equal(destination.QuickAccessShortcut, result.QuickAccessShortcut);
        Assert.True(result.NotificationsEnabled);
        Assert.False(result.PcControlEnabled);
        Assert.Empty(result.CustomCommands);
    }

    [Theory]
    [InlineData("formatVersion", "2")]
    [InlineData("accessToken", "\"secret\"")]
    [InlineData("pcControlEnabled", "true")]
    [InlineData("theme", "\"Custom\"")]
    [InlineData("quickActions", "null")]
    [InlineData("enabledSensors", "[\"unknown\"]")]
    [InlineData("enabledSensors", "[null]")]
    public void RejectsUnrecognizedOrUnsafeSettingsBeforeApplyingThem(string property, string replacement)
    {
        var payload = JsonNode.Parse(SettingsTransfer.Export(new Settings()))!.AsObject();
        payload[property] = JsonNode.Parse(replacement);
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(Encoding.UTF8.GetBytes(payload.ToJsonString())));
    }

    [Fact]
    public void RejectsIncompleteFilesAndFilesAboveTheSizeLimit()
    {
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read("""{"formatVersion":1}"""u8));
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(new byte[SettingsTransfer.MaximumBytes + 1]));
    }
}
