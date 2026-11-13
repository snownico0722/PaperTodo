using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PaperTodo;

/// <summary>
/// Best-effort DWM capture exclusion for our own top-level HWNDs.
/// This is not a security boundary: external capture backends may ignore it.
/// </summary>
internal static class WindowCaptureExclusion
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    internal static void Apply(Window window, bool excluded)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            Apply(hwnd, excluded);
    }

    internal static void Apply(IntPtr hwnd, bool excluded)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (!SetWindowDisplayAffinity(hwnd, excluded ? WdaExcludeFromCapture : WdaNone))
        {
            // A layered/translucent window or unsupported capture path can reject affinity.
            // The feature is optional: do not modify HWND styles or presentation to force it.
            if (excluded)
                Trace.TraceWarning(
                    "Capture exclusion failed. HWND={0}; Win32Error={1}",
                    hwnd, Marshal.GetLastWin32Error());
        }
    }
}
