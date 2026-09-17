namespace HassConnect.Core;

public static class DisplayPowerState
{
    public static string FromWindowsValue(int value) => value switch
    {
        0 => "off",
        1 => "on",
        2 => "dimmed",
        _ => "unknown"
    };
}
