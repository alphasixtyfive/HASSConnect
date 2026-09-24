using System.Text;
using System.Text.RegularExpressions;

namespace HassConnect.Core;

public sealed record QuickActionDefinition(string EntityId, string Label, string Icon, string Operation);

public sealed record QuickActionEntity(string EntityId, string Name, string Icon, string State, int? SupportedFeatures = null);

public static partial class QuickActionPolicy
{
    // Home Assistant's CoverEntityFeature flags.
    private const int CoverOpen = 1;
    private const int CoverClose = 2;
    private const int CoverStop = 8;

    public const int MaximumActions = 8;
    public const string Toggle = "toggle";
    public const string TurnOn = "turn_on";
    public const string TurnOff = "turn_off";
    public const string Run = "run";
    public const string OpenCover = "open_cover";
    public const string CloseCover = "close_cover";
    public const string StopCover = "stop_cover";

    private static readonly IReadOnlyList<string> ToggleOperations =
        Array.AsReadOnly([Toggle, TurnOn, TurnOff]);
    private static readonly IReadOnlyList<string> RunOperations = Array.AsReadOnly([Run]);
    private static readonly IReadOnlyList<string> CoverOperations =
        Array.AsReadOnly([Toggle, OpenCover, CloseCover, StopCover]);
    private static readonly IReadOnlyList<string> NoOperations = Array.Empty<string>();

    public static QuickActionDefinition Create(string? entityId, string? label, string? icon, string? operation)
    {
        var id = entityId?.Trim() ?? "";
        var operations = AllowedOperations(id);
        if (!EntityIdPattern().IsMatch(id) || operations.Count == 0)
            throw new ArgumentException("Choose a supported Home Assistant entity.", nameof(entityId));

        var name = label?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a label from 1 to 40 characters.", nameof(label));

        var selectedIcon = string.IsNullOrWhiteSpace(icon) ? DefaultIcon(id) : icon.Trim();
        if (!IconPattern().IsMatch(selectedIcon))
            throw new ArgumentException("Choose an icon.", nameof(icon));

        var selectedOperation = operation?.Trim() ?? "";
        if (!operations.Contains(selectedOperation, StringComparer.Ordinal))
            throw new ArgumentException("Choose an action supported by this entity.", nameof(operation));

        return new(id, name, selectedIcon, selectedOperation);
    }

    public static IReadOnlyList<QuickActionDefinition> ValidateCollection(IEnumerable<QuickActionDefinition>? actions)
    {
        var source = actions?.ToList() ?? [];
        if (source.Count > MaximumActions)
            throw new InvalidDataException($"No more than {MaximumActions} quick actions can be configured.");

        var result = new List<QuickActionDefinition>(source.Count);
        var keys = new HashSet<(string EntityId, string Operation)>();
        foreach (var action in source)
        {
            if (action is null)
                throw new InvalidDataException("Quick action configuration contains an empty entry.");
            QuickActionDefinition validated;
            try { validated = Create(action.EntityId, action.Label, action.Icon, action.Operation); }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Quick action configuration is invalid: {exception.Message}", exception);
            }
            if (!keys.Add((validated.EntityId, validated.Operation)))
                throw new InvalidDataException($"The {validated.Operation} action for {validated.EntityId} is configured more than once.");
            result.Add(validated);
        }
        return result;
    }

    public static IReadOnlyList<string> AllowedOperations(string? entityId, int? supportedFeatures = null)
    {
        var domain = Domain(entityId);
        if (domain == "cover")
        {
            if (supportedFeatures is null) return CoverOperations;
            if (supportedFeatures < 0) return NoOperations;
            var operations = new List<string>(4);
            if ((supportedFeatures.Value & (CoverOpen | CoverClose)) == (CoverOpen | CoverClose))
                operations.Add(Toggle);
            if ((supportedFeatures.Value & CoverOpen) != 0) operations.Add(OpenCover);
            if ((supportedFeatures.Value & CoverClose) != 0) operations.Add(CloseCover);
            if ((supportedFeatures.Value & CoverStop) != 0) operations.Add(StopCover);
            return operations;
        }
        return domain switch
        {
            "light" or "switch" or "input_boolean" or "fan" => ToggleOperations,
            "script" or "scene" or "button" => RunOperations,
            _ => NoOperations
        };
    }

    public static string DefaultIcon(string? entityId) => Domain(entityId) switch
    {
        "light" => "mdi:lightbulb",
        "switch" or "input_boolean" => "mdi:toggle-switch",
        "fan" => "mdi:fan",
        "cover" => "mdi:blinds",
        "script" => "mdi:script-text",
        "scene" => "mdi:palette",
        "button" => "mdi:gesture-tap-button",
        _ => "mdi:home-assistant"
    };

    private static string Domain(string? entityId)
    {
        var dot = entityId?.IndexOf('.') ?? -1;
        return dot > 0 ? entityId![..dot] : "";
    }

    [GeneratedRegex(@"\A[a-z_]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdPattern();

    [GeneratedRegex(@"\Amdi:[a-z0-9-]{1,60}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();
}
