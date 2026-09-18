param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing
if (-not ('HassConnectNativeCapture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class HassConnectNativeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr window, IntPtr target, uint flags);
}
'@
}

$windows = @(Get-Process -Name 'HassConnect.App' -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 })
if ($windows.Count -ne 1) {
    throw "Expected one visible HASS Connect window; found $($windows.Count)."
}

$destination = [System.IO.Path]::GetFullPath($OutputPath)
$directory = [System.IO.Path]::GetDirectoryName($destination)
if (-not [System.IO.Directory]::Exists($directory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Without this, Windows can return the
# 800 x 840 logical rectangle while PrintWindow renders physical pixels into it.
$oldContext = [HassConnectNativeCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
try {
    $rect = New-Object HassConnectNativeCapture+RECT
    if (-not [HassConnectNativeCapture]::GetWindowRect(
            $windows[0].MainWindowHandle,
            [ref] $rect)) {
        throw 'Could not read the HASS Connect window bounds.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 720 -or $height -lt 440) {
        throw "Unexpected capture size: ${width} x ${height}."
    }

    $bitmap = New-Object System.Drawing.Bitmap(
        $width,
        $height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $target = $graphics.GetHdc()
            try {
                # PW_RENDERFULLCONTENT captures the WinUI window without desktop content.
                if (-not [HassConnectNativeCapture]::PrintWindow(
                        $windows[0].MainWindowHandle,
                        $target,
                        2)) {
                    throw 'Native window capture failed.'
                }
            }
            finally {
                $graphics.ReleaseHdc($target)
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}
finally {
    if ($oldContext -ne [IntPtr]::Zero) {
        [void] [HassConnectNativeCapture]::SetThreadDpiAwarenessContext($oldContext)
    }
}

$file = Get-Item -LiteralPath $destination
[pscustomobject]@{
    Path = $file.FullName
    Width = $width
    Height = $height
    Bytes = $file.Length
}
