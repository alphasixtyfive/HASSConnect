using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void ExistingSettingsKeepSharingEnabled()
    {
        var settings = JsonSerializer.Deserialize<Settings>("""{"EnabledSensors":["uptime"]}""")!;
        Assert.True(settings.ShareSensors);
        Assert.Contains("uptime", settings.EnabledSensors);
        Assert.Equal("", settings.HomeAssistantPath);
        Assert.False(settings.PcControlEnabled);
        Assert.Equal(PcCommandIds.All.Count, settings.EnabledPcCommands.Count);
    }

    [Fact]
    public void SharingOffSurvivesReloadWithoutLosingSensorChoices()
    {
        var settings = new Settings { ShareSensors = false, EnabledSensors = ["uptime", "idle_time"] };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.False(restored.ShareSensors);
        Assert.True(settings.EnabledSensors.SetEquals(restored.EnabledSensors));
    }

    [Fact]
    public void DashboardPathSurvivesReload()
    {
        var settings = new Settings { HomeAssistantPath = "/dashboard-cameras" };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("/dashboard-cameras", restored.HomeAssistantPath);
    }
}
