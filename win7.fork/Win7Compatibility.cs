using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// Win7-only replacements for APIs introduced after Windows 7.
/// This file is only used by build-win7.ps1's staged build.
/// Windows 7 has a system-DPI model, so all per-monitor/window DPI requests
/// intentionally fall back to the current system DPI.
/// </summary>
internal static class Win7Compatibility
{
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private static readonly IntPtr NoOpDpiContext = new(1);

    public static int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY)
    {
        (dpiX, dpiY) = GetSystemDpi();
        return 0; // S_OK: callers can continue with a system-DPI geometry model.
    }

    public static uint GetDpiForWindow(IntPtr hwnd)
    {
        var (dpiX, _) = GetSystemDpi();
        return dpiX;
    }

    public static IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext)
    {
        // Windows 7 has no SetThreadDpiAwarenessContext. The Win7 build is intentionally
        // system-DPI aware, so entering/restoring a temporary PMv2 context is a no-op.
        // Return a non-zero sentinel because existing callers treat zero as failure.
        return NoOpDpiContext;
    }

    private static (uint DpiX, uint DpiY) GetSystemDpi()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return (96, 96);
        }

        try
        {
            var x = GetDeviceCaps(dc, LogPixelsX);
            var y = GetDeviceCaps(dc, LogPixelsY);
            return (
                x > 0 ? (uint)x : 96u,
                y > 0 ? (uint)y : 96u);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
