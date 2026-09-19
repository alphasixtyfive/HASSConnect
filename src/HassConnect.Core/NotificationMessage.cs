using System.Text.Json;

namespace HassConnect.Core;

public sealed record NotificationAction(string Id, string Title, string? Link = null);

public sealed record NotificationMessage(string? Title, string Message, string? Image,
    IReadOnlyList<NotificationAction> Actions, string? ConfirmationId)
{
    public string? Tag { get; init; }
    public JsonElement? ActionData { get; init; }
    public string NotificationId { get; init; } = Guid.NewGuid().ToString("N");
    public bool Persistent { get; init; }
    public bool IsClear => Message == "clear_notification";
    public PcCommand? Command { get; init; }

    public static NotificationMessage Parse(JsonElement payload)
    {
        var message = Text(payload, "message", 2000);
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidDataException("Notification has no message.");
        var title = Text(payload, "title", 200);
        var actions = new List<NotificationAction>();
        string? image = null;
        string? tag = null;
        var persistent = false;
        JsonElement? actionData = null;
        JsonElement? commandData = null;
        if (payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            commandData = data;
            tag = Text(data, "tag", int.MaxValue);
            if (tag?.Length > 256) throw new InvalidDataException("Notification tag exceeds 256 characters.");
            if (string.IsNullOrWhiteSpace(tag)) tag = null;
            persistent = data.TryGetProperty("persistent", out var persistentValue) &&
                persistentValue.ValueKind == JsonValueKind.True;
            if (data.TryGetProperty("action_data", out var context) && context.ValueKind == JsonValueKind.Object)
                actionData = context.Clone();
            image = Text(data, "image", 2048);
            if (data.TryGetProperty("actions", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var id = Text(item, "action", int.MaxValue);
                    var label = Text(item, "title", 60);
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || string.IsNullOrWhiteSpace(label) || id == "REPLY") continue;
                    var uri = id == "URI" ? Text(item, "uri", 2048) : null;
                    if (id == "URI" && string.IsNullOrWhiteSpace(uri)) continue;
                    actions.Add(new(id, label, uri));
                    if (actions.Count == 3) break;
                }
        }
        if (message == "clear_notification" && tag is null)
            throw new InvalidDataException("Clearing a notification requires a tag.");
        return new(title, message, image, actions, ReadConfirmationId(payload))
        {
            Tag = tag,
            ActionData = actionData,
            Command = PcCommand.Parse(message, commandData),
            Persistent = persistent
        };
    }

    public static string? ReadConfirmationId(JsonElement payload) => Text(payload, "hass_confirm_id", 256);

    private static string? Text(JsonElement element, string property, int limit)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return text?.Length > limit ? text[..limit] : text;
    }
}
