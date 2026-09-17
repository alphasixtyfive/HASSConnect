using System.Runtime.InteropServices;

namespace HassConnect.App.Services;

internal static partial class WindowActivation
{
    private const int ShowRestore = 9;

    public static void RestoreAndActivate(nint window)
    {
        if (window == nint.Zero) return;
        _ = ShowWindow(window, ShowRestore);
        _ = SetForegroundWindow(window);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool SetForegroundWindow(nint window);
}
