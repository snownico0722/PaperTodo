using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace PaperTodo;

internal interface INativeMicaApi
{
    bool IsSupported { get; }
    bool CompositionEnabled { get; }
    bool TransparencyEnabled { get; }
    bool HighContrast { get; }
    bool IsLayered(IntPtr hwnd);
    int ExtendFrame(IntPtr hwnd, bool enabled);
    int SetDarkMode(IntPtr hwnd, bool dark);
    int SetBackdrop(IntPtr hwnd, int backdrop);
    int EnableAlpha(IntPtr hwnd);
    int DisableAlpha(IntPtr hwnd);
    void ConfigureFrame(IntPtr hwnd, bool rounded);
}

/// <summary>Documented DWM APIs; no wallpaper decoding, capture or undocumented Mica flag.</summary>
internal sealed class DwmMicaApi : INativeMicaApi
{
    internal static readonly DwmMicaApi Instance = new();
    internal const int None = 1;
    internal const int MainWindow = 2; // DWMSBT_MAINWINDOW: Windows 11 Mica, not Acrylic/Mica Alt.
    internal const int SystemBackdropAttribute = 38;
    public bool IsSupported => NativeMicaBackdrop.IsSupported;
    public bool HighContrast => System.Windows.SystemParameters.HighContrast;
    public bool CompositionEnabled => DwmIsCompositionEnabled(out var enabled) >= 0 && enabled;
    public bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int value || value != 0;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public bool IsLayered(IntPtr hwnd) => (GetWindowLong(hwnd, -20) & 0x00080000) != 0;

    public int ExtendFrame(IntPtr hwnd, bool enabled)
    {
        var margins = new Margins { Left = enabled ? -1 : 0, Right = enabled ? -1 : 0,
            Top = enabled ? -1 : 0, Bottom = enabled ? -1 : 0 };
        return DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }
    public int SetDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }
    public int SetBackdrop(IntPtr hwnd, int backdrop) =>
        DwmSetWindowAttribute(hwnd, SystemBackdropAttribute, ref backdrop, sizeof(int));

    public int EnableAlpha(IntPtr hwnd)
    {
        // Preserve the WPF alpha channel for rounded capsule/opacity animation fallback on
        // our non-layered HWND. On Windows 8+ this API does not add a blur effect. The empty
        // blur region enables alpha composition without Acrylic or a fake Mica material.
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return unchecked((int)0x80004005);
        try
        {
            var blur = new BlurBehind { Flags = 3 /* ENABLE | BLURREGION */, Enabled = true, Region = region };
            return DwmEnableBlurBehindWindow(hwnd, ref blur);
        }
        finally { DeleteObject(region); } // Unlike SetWindowRgn, DWM does not take ownership.
    }

    public int DisableAlpha(IntPtr hwnd)
    {
        var blur = new BlurBehind { Flags = 1 /* ENABLE */, Enabled = false };
        return DwmEnableBlurBehindWindow(hwnd, ref blur);
    }

    public void ConfigureFrame(IntPtr hwnd, bool rounded)
    {
        var corners = rounded ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND
        var noColor = unchecked((int)0xfffffffe); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* BORDER_COLOR */, ref noColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* CAPTION_COLOR */, ref noColor, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        internal uint Flags;
        [MarshalAs(UnmanagedType.Bool)] internal bool Enabled;
        internal IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] internal bool TransitionOnMaximized;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
