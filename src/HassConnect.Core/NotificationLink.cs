namespace HassConnect.Core;

public static class NotificationLink
{
    public static Uri Resolve(Uri server, string value)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(value);
        Uri target;
        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) &&
            !value.Contains('\\', StringComparison.Ordinal))
            target = new Uri(server, value);
        else if (!Uri.TryCreate(value, UriKind.Absolute, out target!))
            throw new InvalidDataException("Use an HTTP(S) URL or a dashboard path beginning with /.");
        if (target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
            throw new InvalidDataException("Notification links must use HTTP or HTTPS without embedded credentials.");
        return target;
    }
}
