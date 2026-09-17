using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HassConnect.App.Services;

internal static class PowerSensors
{
    private const byte NoBattery = 128;
    private const byte UnknownBattery = 255;
    private const byte Charging = 8;

    public static bool IsSupported(string id) => id is not ("battery_level" or "battery_charging") || HasBattery();

    public static object? Read(string id)
    {
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!IsBatteryPresent(status)) return id == "battery_charging" ? null : "unknown";

        return id switch
        {
            "battery_level" => status.BatteryPercent <= 100 ? status.BatteryPercent : "unknown",
            "battery_charging" => (status.BatteryFlag & Charging) != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
    }

    private static bool HasBattery() => GetSystemPowerStatus(out var status) && IsBatteryPresent(status);

    private static bool IsBatteryPresent(PowerStatus status) =>
        status.BatteryFlag != UnknownBattery && (status.BatteryFlag & NoBattery) == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryPercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
