using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class DesktopSensorTests
{
    [Theory]
    [InlineData(0, "off")]
    [InlineData(1, "on")]
    [InlineData(2, "dimmed")]
    [InlineData(255, "unknown")]
    public void DisplayValuesDoNotInventAnOnState(int value, string expected) => Assert.Equal(expected, DisplayPowerState.FromWindowsValue(value));

    [Fact]
    public void AddedSensorsAreOptInAndUseCorrectHaTypes()
    {
        var settings = new Settings();
        Assert.Empty(settings.EnabledSensors);
        Assert.Equal("timestamp", SensorDefinition.Available.Single(sensor => sensor.Id == "last_seen").DeviceClass);
    }
}
