using System.Runtime.InteropServices;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;
    private const int FirstHotkeyId = 0x1001;
    private const int SecondHotkeyId = 0x1002;
    private readonly nint _window;
    private readonly Action _open;
    private readonly Action<bool> _showQuickAccess;
    private readonly Action _quit;
    private readonly Action _openHomeAssistant;
    public bool CanOpenHomeAssistant { get; set; }
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;
    private int _hotkeyId;
    private QuickAccessShortcut? _shortcut;

    public TrayIcon(nint window, Action open, Action<bool> showQuickAccess, Action quit, Action openHomeAssistant)
    {
        _window = window; _open = open; _quit = quit;
        _showQuickAccess = showQuickAccess;
        _openHomeAssistant = openHomeAssistant;
        _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (!SetWindowSubclass(window, _callback, 1, 0)) throw new InvalidOperationException("Unable to attach the tray icon.");
        try
        {
            var icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 1, 32, 32, 0x10);
            if (icon == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                Window = window,
                Id = 1,
                Flags = 1 | 2 | 4,
                CallbackMessage = CallbackMessage,
                Icon = icon,
                Tip = "HASS Connect",
                Info = "",
                InfoTitle = ""
            };
            if (!Shell_NotifyIcon(0, ref _data))
                throw new InvalidOperationException("Windows could not create the notification-area icon.");
        }
        catch
        {
            RemoveWindowSubclass(_window, _callback, 1);
            if (_data.Icon != 0) DestroyIcon(_data.Icon);
            throw;
        }
    }

    public void SetStatus(string status)
    {
        _data.Tip = ("HASS Connect · " + status)[..Math.Min(127, ("HASS Connect · " + status).Length)];
        Shell_NotifyIcon(1, ref _data);
    }

    public bool SetQuickAccessShortcut(QuickAccessShortcut? shortcut)
    {
        if (_disposed) return false;
        if (shortcut == _shortcut) return true;
        if (shortcut is null)
        {
            if (_hotkeyId != 0) UnregisterHotKey(_window, _hotkeyId);
            _hotkeyId = 0;
            _shortcut = null;
            return true;
        }

        var nextId = _hotkeyId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        var modifiers = NoRepeat
            | (shortcut.Alt ? 0x0001u : 0)
            | (shortcut.Control ? 0x0002u : 0)
            | (shortcut.Shift ? 0x0004u : 0);
        if (!RegisterHotKey(_window, nextId, modifiers, (uint)shortcut.VirtualKey)) return false;
        if (_hotkeyId != 0) UnregisterHotKey(_window, _hotkeyId);
        _hotkeyId = nextId;
        _shortcut = shortcut;
        return true;
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == _taskbarCreated) Shell_NotifyIcon(0, ref _data);
        if (message == HotkeyMessage && (int)wParam == _hotkeyId)
        {
            _showQuickAccess(true);
            return 0;
        }
        if (message == CallbackMessage)
        {
            if ((int)lParam == 0x0202) _showQuickAccess(false);
            if ((int)lParam == 0x0205)
            {
                GetCursorPos(out var point);
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open HASS Connect");
                    AppendMenu(menu, CanOpenHomeAssistant ? 0u : 1u, 3, "Open Home Assistant");
                    AppendMenu(menu, 0x800, 0, "");
                    AppendMenu(menu, 0, 2, "Quit");
                    SetForegroundWindow(hwnd);
                    var command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                    if (command == 1) _open();
                    else if (command == 2) _quit();
                    else if (command == 3 && CanOpenHomeAssistant) _openHomeAssistant();
                }
                finally { DestroyMenu(menu); }
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hotkeyId != 0) UnregisterHotKey(_window, _hotkeyId);
        Shell_NotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _callback, 1);
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
        _data.Icon = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string label);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
