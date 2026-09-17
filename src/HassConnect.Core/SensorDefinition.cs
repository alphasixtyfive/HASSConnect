namespace HassConnect.Core;

public sealed record SensorDefinition(string Id, string Name, string Description, string Type,
    string Icon, string? Unit = null, string? DeviceClass = null, string Group = "System",
    string? OnText = null, string? OffText = null)
{
    public static IReadOnlyList<SensorDefinition> Available { get; } =
    [
        new("idle_time", "Idle time", "Seconds since the last keyboard or mouse input.", "sensor", "mdi:timer-outline", "s", "duration"),
        new("uptime", "Uptime", "Time since Windows last started.", "sensor", "mdi:clock-outline", "s", "duration"),
        new("cpu_usage", "CPU usage", "Processor use across all cores.", "sensor", "mdi:chip", "%"),
        new("memory_usage", "Memory usage", "Physical memory currently in use.", "sensor", "mdi:memory", "%"),
        new("session_locked", "Session locked", "Whether your Windows session is locked.", "binary_sensor", "mdi:lock-outline", OnText: "Locked", OffText: "Unlocked"),
        new("display_state", "Display state", "Display power state for this Windows session: on, dimmed or off. Not individual monitor power switches.", "sensor", "mdi:monitor"),
        new("last_seen", "Last seen", "Time of the latest successful sensor report. Stops updating while sensors are disabled, the PC sleeps or the connection is lost.", "sensor", "mdi:clock-check-outline", DeviceClass: "timestamp"),
        new("microphone_in_use", "Microphone in use", "Whether a Windows app is currently using the microphone.", "binary_sensor", "mdi:microphone", Group: "Activity", OnText: "In use", OffText: "Not in use"),
        new("webcam_in_use", "Webcam in use", "Whether a Windows app is currently using the camera.", "binary_sensor", "mdi:webcam", Group: "Activity", OnText: "In use", OffText: "Not in use"),
        new("battery_level", "Battery level", "Remaining system battery charge.", "sensor", "mdi:battery",
            Unit: "%", DeviceClass: "battery", Group: "Power"),
        new("battery_charging", "Battery charging", "Whether the system battery is currently charging.", "binary_sensor", "mdi:battery-charging", DeviceClass: "battery_charging", Group: "Power", OnText: "Charging", OffText: "Not charging"),
        new("disk_usage", "System disk usage", "Space currently used on the disk containing Windows.", "sensor", "mdi:harddisk", "%", Group: "Storage"),
        new("disk_free_space", "System disk free space", "Space available on the disk containing Windows.", "sensor", "mdi:harddisk-plus",
            Unit: "GB", DeviceClass: "data_size", Group: "Storage"),
        new("ip_address", "IP address", "Local IPv4 address on the adapter Windows uses to reach Home Assistant.", "sensor", "mdi:ip-network", Group: "Network"),
        new("network_adapter", "Network adapter", "Adapter Windows uses to reach Home Assistant. Traffic readings cover all traffic on this adapter.", "sensor", "mdi:ethernet", Group: "Network"),
        new("download_speed", "Download speed", "Received traffic on this adapter, averaged over the reporting interval.", "sensor", "mdi:download", "Mbit/s", "data_rate", "Network"),
        new("upload_speed", "Upload speed", "Sent traffic on this adapter, averaged over the reporting interval.", "sensor", "mdi:upload", "Mbit/s", "data_rate", "Network")
    ];
}

public sealed record SensorReading(string Id, object? Value);
