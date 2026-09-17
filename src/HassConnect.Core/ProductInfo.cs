using System.Reflection;

namespace HassConnect.Core;

public static class ProductInfo
{
    public static string? Repository { get; } = "alphasixtyfive/HASSConnect";

    public const string Name = "HASS Connect";
    public const string HomeAssistantAppId = "hass_connect";
    public const string DeveloperName = "alphasixtyfive";
    public static Uri DeveloperPage { get; } = new("https://github.com/alphasixtyfive");

    public static Uri? RepositoryPage => Page("");
    public static Uri? ReleasesPage => Page("/releases");
    public static Uri? IssuesPage => Page("/issues");
    public static Uri? LicensePage => Page("/blob/main/LICENSE");

    private static Uri? Page(string path) => Repository is null ? null : new($"https://github.com/{Repository}{path}");

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(ProductInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = informational?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString(3);
        return ReleaseVersion.TryParse(value, out var version) ? version.ToString(3) : "unknown";
    }
}
