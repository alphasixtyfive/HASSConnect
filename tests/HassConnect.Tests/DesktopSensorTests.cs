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

    [Theory]
    [InlineData("microphone_in_use", "binary_sensor", null, "Activity")]
    [InlineData("webcam_in_use", "binary_sensor", null, "Activity")]
    [InlineData("battery_charging", "binary_sensor", "battery_charging", "Power")]
    [InlineData("disk_usage", "sensor", null, "Storage")]
    [InlineData("disk_free_space", "sensor", "data_size", "Storage")]
    public void WindowsSensorDefinitionsUseHomeAssistantMetadata(string id, string type, string? deviceClass, string group)
    {
        var sensor = SensorDefinition.Available.Single(sensor => sensor.Id == id);

        Assert.Equal(type, sensor.Type);
        Assert.Equal(deviceClass, sensor.DeviceClass);
        Assert.Equal(group, sensor.Group);
    }

    [Fact]
    public void BinarySensorsHaveReadableStateLabels()
    {
        var sensors = SensorDefinition.Available.Where(sensor => sensor.Type == "binary_sensor");

        Assert.All(sensors, sensor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(sensor.OnText));
            Assert.False(string.IsNullOrWhiteSpace(sensor.OffText));
        });
    }
}
