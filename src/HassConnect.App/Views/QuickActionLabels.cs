using HassConnect.Core;

namespace HassConnect.App.Views;

internal static class QuickActionLabels
{
    public static string Status(QuickActionDefinition action, QuickActionState? reading) =>
        action.Operation is QuickActionPolicy.Run or QuickActionPolicy.ViewCamera ? Operation(action.Operation) : reading?.State switch
        {
            null => "Status unavailable",
            "on" => "On",
            "off" => "Off",
            "open" => "Open",
            "closed" => "Closed",
            "opening" => "Opening",
            "closing" => "Closing",
            "unavailable" => "Unavailable",
            _ => "Unknown"
        };

    public static string Operation(string operation) => operation switch
    {
        QuickActionPolicy.Toggle => "Toggle",
        QuickActionPolicy.TurnOn => "Turn on",
        QuickActionPolicy.TurnOff => "Turn off",
        QuickActionPolicy.Run => "Run",
        QuickActionPolicy.OpenCover => "Open",
        QuickActionPolicy.CloseCover => "Close",
        QuickActionPolicy.StopCover => "Stop",
        QuickActionPolicy.Brightness => "Brightness slider",
        QuickActionPolicy.Volume => "Volume slider",
        QuickActionPolicy.ViewCamera => "View live",
        _ => operation
    };
}
