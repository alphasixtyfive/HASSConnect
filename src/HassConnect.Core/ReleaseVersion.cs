using System.Text.RegularExpressions;

namespace HassConnect.Core;

public static partial class ReleaseVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (value is null || !StableVersion().IsMatch(value) ||
            !Version.TryParse(value, out var parsed)) return false;
        version = parsed;
        return true;
    }

    public static bool TryParseTag(string? tag, out Version version) =>
        TryParse(tag?.StartsWith('v') == true ? tag[1..] : null, out version);

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex StableVersion();
}
