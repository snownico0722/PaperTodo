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
    int ExtendFrame(IntPtr hwnd, int top);
    int SetDarkMode(IntPtr hwnd, bool dark);
    int SetBackdrop(IntPtr hwnd, int backdrop);
    int SetClearAcrylic(IntPtr hwnd, bool enabled, bool dark);
    int EnableAlpha(IntPtr hwnd);
    int DisableAlpha(IntPtr hwnd);
    void ConfigureFrame(IntPtr hwnd, bool rounded, int borderColor, bool clearAcrylic);
    void SetNonClientActive(IntPtr hwnd, bool active);
}

/// <summary>DWM integration, with an isolated legacy accent policy for Clear Acrylic.</summary>
internal sealed class DwmMicaApi : INativeMicaApi
{
    internal static readonly DwmMicaApi Instance = new();
    internal const int None = 1;
    internal const int MainWindow = 2;      // DWMSBT_MAINWINDOW: Windows 11 Mica
    internal const int TransientWindow = 3; // DWMSBT_TRANSIENTWINDOW: Acrylic
    internal const int NonClientActivateMessage = 0x0086; // WM_NCACTIVATE
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

    public int ExtendFrame(IntPtr hwnd, int top)
    {
        var margins = new Margins { Left = top < 0 ? -1 : 0, Right = top < 0 ? -1 : 0,
            Top = top, Bottom = top < 0 ? -1 : 0 };
        return DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }
    public int SetDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }
    public int SetBackdrop(IntPtr hwnd, int backdrop) =>
        DwmSetWindowAttribute(hwnd, SystemBackdropAttribute, ref backdrop, sizeof(int));

    public unsafe int SetClearAcrylic(IntPtr hwnd, bool enabled, bool dark)
    {
        // WCA_ACCENT_POLICY is undocumented. Keep it exclusive to Clear Acrylic and
        // report failure so the adapter keeps an opaque surface on unsupported systems.
        // GradientColor is AABBGGRR; nonzero alpha is required for Acrylic blur.
        var policy = new AccentPolicy
        {
            State = enabled ? 4 : 0, // ACCENT_ENABLE_ACRYLICBLURBEHIND / ACCENT_DISABLED
            Color = dark ? 0x30282120u : 0x28FFFFFFu
        };
        var data = new CompositionAttributeData
        {
            Attribute = 19, Data = (IntPtr)(&policy), Size = (nuint)sizeof(AccentPolicy)
        };
        try
        {
            return SetWindowCompositionAttribute(hwnd, ref data) ? 0 : unchecked((int)0x80004005);
        }
        catch (EntryPointNotFoundException) { return unchecked((int)0x80004001); }
    }

    public void SetNonClientActive(IntPtr hwnd, bool active) =>
        // Use the same -1 lParam as WindowChrome to avoid drawing over its custom frame.
        DefWindowProc(hwnd, NonClientActivateMessage, active ? new IntPtr(1) : IntPtr.Zero, new IntPtr(-1));

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

    public void ConfigureFrame(IntPtr hwnd, bool rounded, int borderColor, bool clearAcrylic)
    {
        var corners = rounded ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND
        // COLOR_NONE is supported for BORDER_COLOR, not CAPTION_COLOR. The accent path's
        // small glass strip otherwise inherits the system accent (a bright blue top line).
        var captionColor = clearAcrylic ? borderColor : unchecked((int)0xffffffff); // COLOR_DEFAULT
        DwmSetWindowAttribute(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* BORDER_COLOR */, ref borderColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* CAPTION_COLOR */, ref captionColor, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { internal int State, Flags; internal uint Color; internal int Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData { internal int Attribute; internal IntPtr Data; internal nuint Size; }
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
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
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
