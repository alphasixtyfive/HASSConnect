using System.ComponentModel;
using System.Runtime.InteropServices;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal sealed class DisplayStateMonitor : IDisposable
{
    private static readonly Guid DisplaySetting = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private const nuint SubclassId = 2;
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private nint _registration;
    private string _state = "unknown";

    public DisplayStateMonitor(nint window)
    {
        _window = window;
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, SubclassId, 0))
            throw new InvalidOperationException("Could not attach the display state sensor.");
    }

    public string Read()
    {
        if (_registration == 0)
        {
            var setting = DisplaySetting;
            _registration = RegisterPowerSettingNotification(_window, ref setting, 0);
            if (_registration == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return _state;
    }

    public void Reset()
    {
        if (_registration != 0) UnregisterPowerSettingNotification(_registration);
        _registration = 0;
        _state = "unknown";
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint reference)
    {
        const uint powerBroadcast = 0x0218;
        const int settingChanged = 0x8013;
        if (_registration != 0 && message == powerBroadcast && wParam == settingChanged && lParam != 0 &&
            Marshal.PtrToStructure<Guid>(lParam) == DisplaySetting && Marshal.ReadInt32(lParam, 16) == sizeof(int))
            _state = DisplayPowerState.FromWindowsValue(Marshal.ReadInt32(lParam, 20));
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        Reset();
        RemoveWindowSubclass(_window, _callback, SubclassId);
    }

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint registration);
}
