namespace HassConnect.Core;

public static class HomeAssistantNavigation
{
    public static string NormalizePath(string? path)
    {
        var value = path?.Trim() ?? "";
        if (value.Length == 0) return "";

        if (!value.StartsWith('/')) value = "/" + value;
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal))
            throw new ArgumentException("Choose a path on your Home Assistant server.", nameof(path));

        return value;
    }

    public static Uri Resolve(Uri server, string? path)
    {
        ArgumentNullException.ThrowIfNull(server);
        var normalized = NormalizePath(path);
        if (normalized.Length == 0) return server;

        var destination = new Uri(server, normalized);
        if (!ServerAddress.SameOrigin(server, destination))
            throw new ArgumentException("Choose a path on your Home Assistant server.", nameof(path));
        return destination;
    }
}
