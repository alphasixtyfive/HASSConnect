namespace HassConnect.Core;

using System.Diagnostics.CodeAnalysis;

public sealed record Settings
{
    public int SchemaVersion { get; init; } = 1;
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; init; } = Environment.MachineName;
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
        Justification = "The persisted UI field may be empty while the app is disconnected.")]
    public string ServerUrl { get; init; } = "";
    public string HomeAssistantPath { get; init; } = "";
    public string Theme { get; init; } = "System";
    public bool ShareSensors { get; init; } = true;
    public bool NotificationsEnabled { get; init; }
    public bool NotificationSound { get; init; } = true;
    public bool PcControlEnabled { get; init; }
    public HashSet<string> EnabledPcCommands { get; init; } = new(PcCommandIds.All);
    public IReadOnlyList<CustomCommandDefinition> CustomCommands { get; init; } = [];
    public HashSet<string> EnabledSensors { get; init; } = [];
}

public sealed record Credentials(string AccessToken, string? WebhookId = null);
