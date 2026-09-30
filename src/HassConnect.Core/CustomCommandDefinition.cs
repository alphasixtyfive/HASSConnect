using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HassConnect.Core;

public sealed record CustomCommandDefinition(
    string Id,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    bool Enabled = true);

public static partial class CustomCommandPolicy
{
    public const int MaximumCommands = 20;
    public const int MaximumArguments = 16;
    private static readonly char[] BatchMetacharacters = ['&', '|', '<', '>', '^', '%', '!'];

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
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            extension is not (".exe" or ".bat" or ".cmd" or ".ps1"))
        {
            throw new ArgumentException("Choose an absolute path to a local .exe, .bat, .cmd or .ps1 file.", nameof(executablePath));
        }
        try { path = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Choose a valid absolute path to a local program or script.", nameof(executablePath));
        }
        if (extension is ".bat" or ".cmd" && path.IndexOfAny(BatchMetacharacters) >= 0)
            throw new ArgumentException("Batch file paths cannot contain &, |, <, >, ^, % or !.", nameof(executablePath));
        if (requireExecutable)
        {
            if (!File.Exists(path))
                throw new ArgumentException("The selected program or script does not exist.", nameof(executablePath));
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("The selected file cannot be a symbolic link or reparse point.", nameof(executablePath));
                if (extension != ".exe") ValidateScriptDirectory(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException("The selected file cannot be inspected.", nameof(executablePath), exception);
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
        if (extension != ".exe" && normalizedArguments.Count > 0)
            throw new ArgumentException("Scripts do not accept launch options.", nameof(arguments));

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
            if (command is null)
                throw new InvalidDataException("Custom command configuration contains an empty entry.");
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
        ArgumentNullException.ThrowIfNull(command);
        var validated = Create(command.Id, command.Name, command.ExecutablePath, command.Arguments,
            command.Enabled, requireExecutable: true);
        var extension = Path.GetExtension(validated.ExecutablePath).ToLowerInvariant();
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = extension switch
        {
            ".bat" or ".cmd" => Path.Combine(systemDirectory, "cmd.exe"),
            ".ps1" => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            _ => validated.ExecutablePath
        };
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(validated.ExecutablePath)!,
            RedirectStandardInput = extension != ".exe"
        };
        if (extension is ".bat" or ".cmd")
        {
            // cmd.exe reparses its command string, so never place arguments or HA data here.
            // /D disables AutoRun and /V:OFF disables delayed environment expansion.
            start.Arguments = $"/D /V:OFF /S /C \"\"{validated.ExecutablePath}\"\"";
        }
        else if (extension == ".ps1")
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", validated.ExecutablePath })
                start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in validated.Arguments) start.ArgumentList.Add(argument);
        }
        return start;
    }

    public static bool IsScript(CustomCommandDefinition command) =>
        !Path.GetExtension(command.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static void ValidateScriptDirectory(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new ArgumentException("Choose a script on a fixed local drive.", "executablePath");
        for (var directory = Path.GetDirectoryName(path); directory is not null && directory.Length > root.Length;
             directory = Path.GetDirectoryName(directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Script folders cannot be symbolic links or reparse points.", "executablePath");
        }
    }

    public static string[] ParseArguments(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    public static bool IsCustomCommandId(string? value) =>
        value is not null && CommandId().IsMatch(value);

    [GeneratedRegex(@"\Acommand_custom_[a-z][a-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommandId();
}
