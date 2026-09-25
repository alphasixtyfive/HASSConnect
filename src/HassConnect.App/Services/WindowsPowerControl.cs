using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HassConnect.App.Services;

internal static class WindowsPowerControl
{
    private const uint WindowMessageSystemCommand = 0x0112;
    private const int SystemCommandMonitorPower = 0xF170;
    private const int MonitorPowerOff = 2;
    private const uint DisplayRequired = 0x00000002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint PrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;
    private const uint ShutdownReasonPlanned = 0x80000000;
    private const string ShutdownPrivilegeName = "SeShutdownPrivilege";
    private static readonly nint BroadcastWindow = (nint)0xFFFF;

    public static void Lock()
    {
        if (!LockWorkStation()) throw LastError();
    }

    public static void TurnOffDisplays()
    {
        if (!PostMessage(BroadcastWindow, WindowMessageSystemCommand,
                (nint)SystemCommandMonitorPower, (nint)MonitorPowerOff))
            throw LastError();
    }

    public static void WakeDisplays()
    {
        // A one-shot request resets the display idle timer without keeping it awake.
        if (SetThreadExecutionState(DisplayRequired) == 0) throw LastError();
    }

    public static void Sleep()
    {
        using var privilege = EnableShutdownPrivilege();
        if (!SetSuspendState(false, false, false)) throw LastError();
    }

    public static void Shutdown() => EndWindowsSession(restart: false);

    public static void Restart() => EndWindowsSession(restart: true);

    private static void EndWindowsSession(bool restart)
    {
        using var privilege = EnableShutdownPrivilege();
        if (!InitiateSystemShutdownEx(null, null, 0, false, restart, ShutdownReasonPlanned))
            throw LastError();
    }

    private static PrivilegeScope EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
            throw LastError();
        try
        {
            if (!LookupPrivilegeValue(null, ShutdownPrivilegeName, out var luid)) throw LastError();
            var enabled = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new() { Luid = luid, Attributes = PrivilegeEnabled }
            };
            if (!AdjustTokenPrivileges(token, false, ref enabled, Marshal.SizeOf<TokenPrivileges>(),
                    out var previous, out _))
                throw LastError();
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                throw new Win32Exception(ErrorNotAllAssigned);
            return new PrivilegeScope(token, previous);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint parameter, nint value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState,
        int bufferLength, out TokenPrivileges previousState, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownEx(string? machineName, string? message,
        uint timeout, [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown, uint reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    private sealed class PrivilegeScope(SafeAccessTokenHandle token, TokenPrivileges previous) : IDisposable
    {
        public void Dispose()
        {
            if (token.IsClosed) return;
            _ = AdjustTokenPrivileges(token, false, ref previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _);
            token.Dispose();
        }
    }
}
