using HassConnect.Core;

namespace HassConnect.App.Views;

internal static class QuickActionLabels
{
    public static string Operation(string operation) => operation switch
    {
        QuickActionPolicy.Toggle => "Toggle",
        QuickActionPolicy.TurnOn => "Turn on",
        QuickActionPolicy.TurnOff => "Turn off",
        QuickActionPolicy.Run => "Run",
        QuickActionPolicy.OpenCover => "Open",
        QuickActionPolicy.CloseCover => "Close",
        QuickActionPolicy.StopCover => "Stop",
        _ => operation
    };
}
