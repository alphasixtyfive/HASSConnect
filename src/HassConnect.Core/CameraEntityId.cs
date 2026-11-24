using System.Text.RegularExpressions;

namespace HassConnect.Core;

public static partial class CameraEntityId
{
    public static bool IsValid(string? value) => value is { Length: <= 255 } && Pattern().IsMatch(value);

    [GeneratedRegex(@"\Acamera\.[a-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
