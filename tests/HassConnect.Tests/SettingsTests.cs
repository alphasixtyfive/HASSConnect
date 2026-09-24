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
        Assert.Null(settings.QuickAccessShortcut);
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

    [Fact]
    public void QuickActionOrderSurvivesReload()
    {
        var first = QuickActionPolicy.Create("light.desk", "Desk", "", QuickActionPolicy.Toggle);
        var second = QuickActionPolicy.Create("scene.evening", "Evening", "mdi:star", QuickActionPolicy.Run);
        var settings = new Settings { QuickActions = [second, first] };

        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal([second, first], restored.QuickActions);
    }

    [Fact]
    public void ShortcutSurvivesReloadAndDisplaysClearly()
    {
        var shortcut = new QuickAccessShortcut('H', Control: true, Alt: false, Shift: true);
        var settings = new Settings { QuickAccessShortcut = shortcut };

        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(shortcut, restored.QuickAccessShortcut);
        Assert.Equal("Ctrl + Shift + H", QuickAccessShortcutPolicy.Display(restored.QuickAccessShortcut));
        Assert.Equal("Off", QuickAccessShortcutPolicy.Display(null));
    }

    [Theory]
    [InlineData('H', true, false, false)]
    [InlineData('H', false, false, false)]
    [InlineData('H', true, true, false)]
    [InlineData('H', true, true, true)]
    [InlineData(0x70, true, true, false)]
    public void ShortcutRejectsRiskyOrUnsupportedCombinations(int key, bool control, bool alt, bool shift)
    {
        Assert.Throws<InvalidDataException>(() => QuickAccessShortcutPolicy.Validate(
            new QuickAccessShortcut(key, control, alt, shift)));
    }
}
