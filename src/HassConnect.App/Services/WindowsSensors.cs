using System.ComponentModel;
using System.Runtime.InteropServices;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal static class WindowsSensors
{
    private static readonly object CpuGate = new();
    private static CpuUsageSample? _previousCpu;
    private static long _previousCpuTime;
    private static object _cpuUsage = "unknown";

    public static IReadOnlyList<SensorDefinition> Available { get; } = SensorDefinition.Available
        .Where(sensor => sensor.Id != "battery_level" || HasBattery()).ToArray();

    public static SensorReading Read(string id) => id switch
    {
        "uptime" => new(id, Environment.TickCount64 / 1000),
        "idle_time" => new(id, IdleSeconds()),
        "cpu_usage" => new(id, CpuUsage()),
        "memory_usage" => new(id, MemoryUsage()),
        "session_locked" => new(id, SessionLocked()),
        "battery_level" => new(id, BatteryLevel()),
        "ip_address" or "network_adapter" or "download_speed" or "upload_speed" => new(id, NetworkSensors.Read(id)),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static void Reset(string id)
    {
        if (NetworkSensors.Supports(id)) { NetworkSensors.Reset(id); return; }
        if (id != "cpu_usage") return;
        lock (CpuGate)
        {
            _previousCpu = null;
            _cpuUsage = "unknown";
        }
    }

    private static object CpuUsage()
    {
        lock (CpuGate)
        {
            long now = Environment.TickCount64;
            // Registration and the first update may occur back-to-back. Do not
            // report a noisy percentage from an interval shorter than a second.
            if (_previousCpu is not null && now - _previousCpuTime < 1000) return _cpuUsage;
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var sample = new CpuUsageSample(idle, kernel, user);
            _cpuUsage = _previousCpu is { } previous && sample.UsageSince(previous) is { } percent
                ? percent : "unknown";
            _previousCpu = sample;
            _previousCpuTime = now;
            return _cpuUsage;
        }
    }

    private static uint MemoryUsage()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return memory.MemoryLoad;
    }

    private static bool SessionLocked()
    {
        const int currentSession = -1;
        const int sessionInfoEx = 25;
        if (!WTSQuerySessionInformation(nint.Zero, currentSession, sessionInfoEx, out var buffer, out var length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (length < Marshal.SizeOf<SessionInfoPrefix>())
                throw new InvalidDataException("Windows returned incomplete session information.");
            var session = Marshal.PtrToStructure<SessionInfoPrefix>(buffer);
            if (session.Level != 1) throw new InvalidDataException("Windows returned an unsupported session information level.");
            return session.Flags switch
            {
                0 => true,
                1 => false,
                _ => throw new InvalidDataException("Windows could not determine the session lock state.")
            };
        }
        finally { WTSFreeMemory(buffer); }
    }

    private static bool HasBattery() => GetSystemPowerStatus(out var power) &&
        power.BatteryFlag != 255 && (power.BatteryFlag & 128) == 0;

    private static object BatteryLevel()
    {
        if (!GetSystemPowerStatus(out var power)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return power.BatteryFlag != 255 && (power.BatteryFlag & 128) == 0 && power.BatteryPercent <= 100
            ? power.BatteryPercent : "unknown";
    }

    private static long IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Both counters are DWORDs; unsigned subtraction handles their 49-day wrap.
        return unchecked((uint)Environment.TickCount - info.Tick) / 1000;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Tick; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    // The native WTSINFOEX union has 8-byte alignment. Only read its header;
    // user names and domain details in the remainder are not collected.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct SessionInfoPrefix
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(16)] public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryPercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus power);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass,
        out nint buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint buffer);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
