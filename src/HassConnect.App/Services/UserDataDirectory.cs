using System.Runtime.InteropServices;

namespace HassConnect.App.Services;

internal static class UserDataDirectory
{
    public static bool IsIsolated => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HASSCONNECT_TEST_PROFILE"));
    public static string Path { get; } = Resolve();

    private static string Resolve()
    {
        if (IsIsolated)
            return System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("HASSCONNECT_TEST_PROFILE")!);
        // Keep settings in the same per-user directory even when a packaged
        // launcher starts the app.
        var localAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        const uint noPackageRedirection = 0x00010000;
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref localAppData, noPackageRedirection, nint.Zero, out var path));
        try { return System.IO.Path.Combine(Marshal.PtrToStringUni(path)!, "HassConnect"); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
}
