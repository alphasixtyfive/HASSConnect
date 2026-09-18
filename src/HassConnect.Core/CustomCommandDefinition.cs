using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HassConnect.Core;

public sealed record CustomCommandDefinition(
    string Id,
    string Name,
    string ExecutablePath,
    List<string> Arguments,
    bool Enabled = true);

public static partial class CustomCommandPolicy
{
    public const int MaximumCommands = 20;
    public const int MaximumArguments = 16;

    public static CustomCommandDefinition Create(
        string? id,
        string? name,
        string? executablePath,
        IEnumerable<string>? arguments,
        bool enabled = true,
        bool requireExecutable = true)
    {
        var normalizedId = id?.Trim() ?? string.Empty;
        if (!CommandId().IsMatch(normalizedId))
            throw new ArgumentException(
                "Use command_custom_ followed by 1–32 lowercase letters, numbers, or underscores.", nameof(id));

        var normalizedName = name?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
        if (normalizedName.Length is < 1 or > 60 || normalizedName.Any(char.IsControl))
            throw new ArgumentException("Enter a command name from 1 to 60 characters.", nameof(name));

        var path = executablePath?.Trim() ?? string.Empty;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose an absolute path to a local .exe file.", nameof(executablePath));
        }
        try { path = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Choose a valid absolute path to a local .exe file.", nameof(executablePath));
        }
        if (requireExecutable)
        {
            if (!File.Exists(path))
                throw new ArgumentException("The selected executable does not exist.", nameof(executablePath));
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("The selected executable cannot be a symbolic link or reparse point.", nameof(executablePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException("The selected executable cannot be inspected.", nameof(executablePath), exception);
            }
        }

        var normalizedArguments = (arguments ?? []).Select(value => value ?? string.Empty).ToList();
        if (normalizedArguments.Count > MaximumArguments)
            throw new ArgumentException($"Use no more than {MaximumArguments} fixed arguments.", nameof(arguments));
        if (normalizedArguments.Any(value => value.Length > 512 || value.Any(char.IsControl)) ||
            normalizedArguments.Sum(value => value.Length) > 4096)
        {
            throw new ArgumentException("Arguments must be printable, at most 512 characters each and 4096 characters in total.", nameof(arguments));
        }

        return new(normalizedId, normalizedName, path, normalizedArguments, enabled);
    }

    public static IReadOnlyList<CustomCommandDefinition> ValidateCollection(
        IEnumerable<CustomCommandDefinition>? commands,
        bool requireExecutables = false)
    {
        var source = commands?.ToList() ?? [];
        if (source.Count > MaximumCommands)
            throw new InvalidDataException($"No more than {MaximumCommands} custom commands can be configured.");
        var result = new List<CustomCommandDefinition>(source.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in source)
        {
            CustomCommandDefinition validated;
            try
            {
                validated = Create(command.Id, command.Name, command.ExecutablePath, command.Arguments,
                    command.Enabled, requireExecutables);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Custom command configuration is invalid: {exception.Message}", exception);
            }
            if (!ids.Add(validated.Id))
                throw new InvalidDataException($"Custom command name '{validated.Id}' is used more than once.");
            result.Add(validated);
        }
        return result;
    }

    public static ProcessStartInfo CreateStartInfo(CustomCommandDefinition command)
    {
        var validated = Create(command.Id, command.Name, command.ExecutablePath, command.Arguments,
            command.Enabled, requireExecutable: true);
        var start = new ProcessStartInfo(validated.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(validated.ExecutablePath)!
        };
        foreach (var argument in validated.Arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static bool IsCustomCommandId(string? value) =>
        value is not null && CommandId().IsMatch(value);

    [GeneratedRegex(@"\Acommand_custom_[a-z][a-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommandId();
}
