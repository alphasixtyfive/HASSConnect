using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HassConnect.App.Services;

internal static class WindowTitleBar
{
    internal static void Apply(Window window, ElementTheme theme)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dark = theme == ElementTheme.Dark;
        var enabled = dark ? 1 : 0;
        var background = dark ? 0x202020 : 0xF3F3F3;
        var foreground = dark ? 0xFFFFFF : 0x000000;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
