using System.Text.Json;

namespace HassConnect.HomeAssistant;

public sealed record HomeAssistantDashboard(string Title, string Path);

public sealed class DashboardCatalog
{
    public async Task<IReadOnlyList<HomeAssistantDashboard>> GetAsync(Uri server, string token, CancellationToken ct)
    {
        await using var socket = await HaWebSocket.ConnectAsync(server, token, ct);
        var dashboards = new Dictionary<string, HomeAssistantDashboard>(StringComparer.OrdinalIgnoreCase);

        await socket.SendAsync(new { id = 1, type = "get_panels" }, ct);
        AddPanels(await socket.ReadResultAsync(1, ct), dashboards);

        await socket.SendAsync(new { id = 2, type = "lovelace/dashboards/list" }, ct);
        AddDashboardMetadata(await socket.ReadResultAsync(2, ct), dashboards);

        return dashboards.Values
            .OrderBy(dashboard => dashboard.Path == "/home" || dashboard.Path == "/lovelace" ? 0 : 1)
            .ThenBy(dashboard => dashboard.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<HomeAssistantDashboard> Parse(JsonElement panels, JsonElement metadata)
    {
        var dashboards = new Dictionary<string, HomeAssistantDashboard>(StringComparer.OrdinalIgnoreCase);
        AddPanels(panels, dashboards);
        AddDashboardMetadata(metadata, dashboards);
        return dashboards.Values.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPanels(JsonElement result, IDictionary<string, HomeAssistantDashboard> dashboards)
    {
        if (result.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        foreach (var panel in result.EnumerateObject())
        {
            if (panel.Value.ValueKind != JsonValueKind.Object ||
                ReadString(panel.Value, "component_name") != "lovelace") continue;
            Add(dashboards, ReadString(panel.Value, "title"), ReadString(panel.Value, "url_path") ?? panel.Name);
        }
    }

    private static void AddDashboardMetadata(JsonElement result, IDictionary<string, HomeAssistantDashboard> dashboards)
    {
        if (result.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        foreach (var dashboard in result.EnumerateArray())
        {
            if (dashboard.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            Add(dashboards, ReadString(dashboard, "title"), ReadString(dashboard, "url_path"));
        }
    }

    private static void Add(IDictionary<string, HomeAssistantDashboard> dashboards, string? title, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalizedPath = "/" + path.Trim().TrimStart('/');
        var displayTitle = string.IsNullOrWhiteSpace(title) ? path : title.Trim();
        dashboards[normalizedPath] = new(displayTitle, normalizedPath);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static InvalidDataException InvalidResponse() =>
        new("Home Assistant returned an invalid dashboard list.");
}
