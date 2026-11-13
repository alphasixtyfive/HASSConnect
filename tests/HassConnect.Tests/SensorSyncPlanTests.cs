using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class SensorSyncPlanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingLocalChoiceWinsUntilReconciledEvenWhenPaused(bool sharing)
    {
        var settings = new Settings
        {
            ShareSensors = sharing, EnabledSensors = ["cpu_usage"],
            PendingSensorChanges = new() { ["cpu_usage"] = true, ["memory_usage"] = false }
        };
        var plan = SensorSyncPlan.Create(settings, SensorDefinition.Available,
            new Dictionary<string, bool> { ["cpu_usage"] = false, ["memory_usage"] = true });
        Assert.Contains("cpu_usage", plan.EnabledIds);
        Assert.DoesNotContain("memory_usage", plan.EnabledIds);
        if (!sharing) Assert.Empty(plan.SensorsToRead);
        var synced = SensorSyncPlan.Create(settings with { PendingSensorChanges = [] }, SensorDefinition.Available,
            new Dictionary<string, bool> { ["cpu_usage"] = false, ["memory_usage"] = true });
        Assert.DoesNotContain("cpu_usage", synced.EnabledIds);
        Assert.Contains("memory_usage", synced.EnabledIds);
    }
    private static readonly SensorDefinition[] Sensors =
    [
        new("first", "First", "", "sensor", ""),
        new("second", "Second", "", "binary_sensor", "")
    ];

    [Fact]
    public void ServerChoicesOverrideLocalChoicesForRegisteredSensors()
    {
        var plan = SensorSyncPlan.Create(new Settings { EnabledSensors = ["first"] }, Sensors,
            new Dictionary<string, bool> { ["first"] = false, ["second"] = true });

        Assert.Equal(["second"], plan.EnabledIds);
        Assert.Equal(["second"], plan.SensorsToRead.Select(s => s.Id));
        Assert.Empty(plan.SensorsToRegister);
    }

    [Fact]
    public void MasterOffPreservesChoicesWithoutSchedulingAnyCollectionOrRegistration()
    {
        var plan = SensorSyncPlan.Create(new Settings { ShareSensors = false, EnabledSensors = ["first", "second"] },
            Sensors, new Dictionary<string, bool> { ["first"] = true });

        Assert.True(plan.EnabledIds.SetEquals(["first", "second"]));
        Assert.Empty(plan.SensorsToRead);
        Assert.Empty(plan.SensorsToRegister);
    }

    [Fact]
    public void ServerDisablesAreRememberedWhileMasterIsOff()
    {
        var settings = new Settings { ShareSensors = false, EnabledSensors = ["first"] };
        var remote = new Dictionary<string, bool> { ["first"] = false };
        var paused = SensorSyncPlan.Create(settings, Sensors, remote);
        var resumed = SensorSyncPlan.Create(settings with { ShareSensors = true, EnabledSensors = new(paused.EnabledIds) }, Sensors, remote);

        Assert.Empty(paused.EnabledIds);
        Assert.Empty(resumed.SensorsToRead);
    }

    [Fact]
    public void OnlySelectedMissingSensorsAreRegistered()
    {
        var plan = SensorSyncPlan.Create(new Settings { EnabledSensors = ["second"] }, Sensors, new Dictionary<string, bool>());
        Assert.Equal(["second"], plan.SensorsToRegister);
        Assert.Equal(["second"], plan.SensorsToRead.Select(s => s.Id));
    }

    [Fact]
    public void RetiredOrUnknownSensorsAreNeverCollected()
    {
        var plan = SensorSyncPlan.Create(new Settings { EnabledSensors = ["retired"] }, Sensors,
            new Dictionary<string, bool> { ["unrelated"] = true });
        Assert.Empty(plan.EnabledIds);
        Assert.Empty(plan.SensorsToRead);
    }
}
