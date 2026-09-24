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
    public IReadOnlyList<QuickActionDefinition> QuickActions { get; init; } = [];
    public QuickAccessShortcut? QuickAccessShortcut { get; init; }
    public HashSet<string> EnabledSensors { get; init; } = [];
}

public sealed record QuickAccessShortcut(int VirtualKey, bool Control, bool Alt, bool Shift);

public static class QuickAccessShortcutPolicy
{
    public static QuickAccessShortcut? Validate(QuickAccessShortcut? shortcut)
    {
        if (shortcut is null) return null;
        var key = shortcut.VirtualKey;
        if (key is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') ||
            !shortcut.Shift || !(shortcut.Control ^ shortcut.Alt))
            throw new InvalidDataException("Use Ctrl + Shift or Alt + Shift with a letter or number.");
        return shortcut;
    }

    public static string Display(QuickAccessShortcut? shortcut)
    {
        if (shortcut is null) return "Off";
        Validate(shortcut);
        var modifiers = new List<string>(3);
        if (shortcut.Control) modifiers.Add("Ctrl");
        if (shortcut.Alt) modifiers.Add("Alt");
        if (shortcut.Shift) modifiers.Add("Shift");
        modifiers.Add(((char)shortcut.VirtualKey).ToString());
        return string.Join(" + ", modifiers);
    }
}

public sealed record Credentials(string AccessToken, string? WebhookId = null);
