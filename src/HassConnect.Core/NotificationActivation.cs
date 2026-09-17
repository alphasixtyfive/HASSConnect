namespace HassConnect.Core;

public static class NotificationActivation
{
    public static bool TryParse(string argument, out string? action)
    {
        action = null;
        if (argument == "open") return true;
        if (!Guid.TryParseExact(argument, "N", out var token)) return false;
        action = token.ToString("N");
        return true;
    }
}
