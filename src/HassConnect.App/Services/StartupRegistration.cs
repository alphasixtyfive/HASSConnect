using Microsoft.Win32;

namespace HassConnect.App.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "HassConnect";

    public static bool IsSupported => !UserDataDirectory.IsIsolated;

    public static bool IsEnabled()
    {
        if (!IsSupported) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return OwnsEntry(key);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported) return;
        if (!enabled)
        {
            RemoveForCurrentExecutable();
            return;
        }

        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Windows could not identify the HASS Connect executable.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Entry, $"\"{path}\" --tray");
    }

    public static void RemoveForCurrentExecutable()
    {
        if (!IsSupported) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is not null && OwnsEntry(key)) key.DeleteValue(Entry, throwOnMissingValue: false);
    }

    private static bool OwnsEntry(RegistryKey? key) =>
        Environment.ProcessPath is { } path && key?.GetValue(Entry) is string command &&
        command.StartsWith($"\"{path}\" ", StringComparison.OrdinalIgnoreCase);
}
