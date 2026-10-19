using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mandarine.Native;

internal enum Anchor
{
    AboveRightOfPoint,
    BelowRightOfPoint,
    TopCenterOfMonitor,
    BottomRightOfWorkArea
}

/// <summary>Acrylic backdrop, tool-window styles and DPI-correct placement for the floating windows.</summary>
internal static class WindowEffects
{
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE exists from Windows 11 22H2 (build 22621).</summary>
    public static bool SupportsAcrylic => Environment.OSVersion.Version.Build >= 22621;

    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Hides the window from Alt+Tab and optionally keeps it from stealing focus.</summary>
    public static void MakeToolWindow(Window window, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Turns on the Windows 11 acrylic material. The window must use WindowChrome with GlassFrameThickness=-1
    /// and a transparent background. Returns false on older systems so the caller can paint a solid surface.
    /// </summary>
    public static bool TryApplyAcrylic(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        int dark = IsDarkMode() ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        if (!SupportsAcrylic) return false;

        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int backdrop = Win32.DWMSBT_TRANSIENTWINDOW;
        return Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
    }

    public static Win32.POINT CursorPosition()
    {
        Win32.GetCursorPos(out var pt);
        return pt;
    }

    /// <summary>Positions an already shown window in physical pixels, clamped to the monitor's work area.</summary>
    public static void Place(Window window, Win32.POINT point, Anchor anchor, int offsetDip = 16)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;

        uint dpi = 96;
        try { if (Win32.GetDpiForMonitor(monitor, 0, out var dx, out _) == 0) dpi = dx; } catch { }
        double scale = dpi / 96.0;

        double widthDip = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double heightDip = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        int off = (int)Math.Round(offsetDip * scale);
        var wa = info.rcWork;

        int x, y;
        switch (anchor)
        {
            case Anchor.AboveRightOfPoint:
                x = point.X + off;
                y = point.Y - h - off;
                break;
            case Anchor.BelowRightOfPoint:
                x = point.X + off / 4;
                y = point.Y + off / 4;
                break;
            case Anchor.TopCenterOfMonitor:
                x = wa.Left + (wa.Width - w) / 2;
                y = wa.Top + off;
                break;
            default: // BottomRightOfWorkArea
                x = wa.Right - w - off;
                y = wa.Bottom - h - off;
                break;
        }

        int margin = (int)(6 * scale);
        x = Math.Max(wa.Left + margin, Math.Min(x, wa.Right - w - margin));
        y = Math.Max(wa.Top + margin, Math.Min(y, wa.Bottom - h - margin));

        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}
