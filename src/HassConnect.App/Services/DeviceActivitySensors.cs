using Microsoft.Win32;

namespace HassConnect.App.Services;

internal static class DeviceActivitySensors
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static bool Read(string id) => id switch
    {
        "microphone_in_use" => CapabilityIsInUse("microphone"),
        "webcam_in_use" => CapabilityIsInUse("webcam"),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static bool CapabilityIsInUse(string capability)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
        return key is not null && IsInUse(key, 2);
    }

    private static bool IsInUse(RegistryKey key, int remainingDepth)
    {
        long started = ReadFileTime(key, "LastUsedTimeStart");
        long stopped = ReadFileTime(key, "LastUsedTimeStop");
        if (started > 0 && started > stopped) return true;
        if (remainingDepth == 0) return false;

        foreach (string name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is not null && IsInUse(child, remainingDepth - 1)) return true;
        }
        return false;
    }

    private static long ReadFileTime(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long value => value,
        int value => value,
        _ => 0
    };

}
