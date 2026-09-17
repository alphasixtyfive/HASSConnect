namespace HassConnect.Core;

public sealed record Settings
{
    public int SchemaVersion { get; init; } = 1;
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; init; } = Environment.MachineName;
    public string ServerUrl { get; init; } = "";
    public string Theme { get; init; } = "System";
    public bool ShareSensors { get; init; } = true;
    public bool NotificationsEnabled { get; init; }
    public bool NotificationSound { get; init; } = true;
    public HashSet<string> EnabledSensors { get; init; } = [];
}

public sealed record Credentials(string AccessToken, string? WebhookId = null);
