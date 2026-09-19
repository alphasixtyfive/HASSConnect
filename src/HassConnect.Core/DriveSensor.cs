namespace HassConnect.Core;

public static class DriveSensor
{
    private const string Prefix = "disk_";
    private const string UsageSuffix = "_usage";
    private const string FreeSpaceSuffix = "_free_space";

    public static IReadOnlyList<SensorDefinition> Create(char driveLetter)
    {
        var letter = NormalizeLetter(driveLetter);
        var label = char.ToUpperInvariant(letter);
        return
        [
            new($"{Prefix}{letter}{UsageSuffix}", $"{label}: disk usage",
                $"Space currently used on the fixed disk mounted as {label}:.",
                "sensor", "mdi:harddisk", "%", Group: "Storage"),
            new($"{Prefix}{letter}{FreeSpaceSuffix}", $"{label}: disk free space",
                $"Space available on the fixed disk mounted as {label}:.",
                "sensor", "mdi:harddisk-plus", "GB", "data_size", "Storage")
        ];
    }

    public static bool TryParse(string? id, out char driveLetter, out bool freeSpace)
    {
        driveLetter = default;
        freeSpace = false;
        if (id is null || id.Length < Prefix.Length + 1 ||
            !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var letter = id[Prefix.Length];
        if (letter is < 'a' or > 'z') return false;
        var suffix = id[(Prefix.Length + 1)..];
        if (suffix == UsageSuffix) { driveLetter = letter; return true; }
        if (suffix == FreeSpaceSuffix) { driveLetter = letter; freeSpace = true; return true; }
        return false;
    }

    public static bool IsPair(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count != 2) return false;
        if (ids.Contains("disk_usage", StringComparer.Ordinal) &&
            ids.Contains("disk_free_space", StringComparer.Ordinal)) return true;

        var parsed = ids.Select(id =>
        {
            var valid = TryParse(id, out var letter, out var freeSpace);
            return (valid, letter, freeSpace);
        }).ToArray();
        return parsed.All(item => item.valid) && parsed[0].letter == parsed[1].letter &&
            parsed[0].freeSpace != parsed[1].freeSpace;
    }

    private static char NormalizeLetter(char driveLetter)
    {
        var letter = char.ToLowerInvariant(driveLetter);
        return letter is >= 'a' and <= 'z' ? letter :
            throw new ArgumentOutOfRangeException(nameof(driveLetter), "A Windows drive letter is required.");
    }
}
