namespace HassConnect.Core;

public static class ServerAddress
{
    public static Uri Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter the Home Assistant address, for example https://home.example.com.");
        return uri;
    }

    public static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
}
