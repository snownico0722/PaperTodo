using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PaperTodo;

// Shared Win32 window-style / z-order helpers for the app's borderless top-level windows
// (paper windows, the deep-capsule slot host, the master capsule). Previously duplicated
// verbatim across PaperWindow.Native and MasterCapsuleWindow.
internal static class WindowNative
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;

    // A tiny off-screen TOOLWINDOW that serves as the native owner for papers hidden from
    // Alt+Tab. Owned top-level windows do not appear in the window switcher, so setting
    // this as owner hides the paper without applying WS_EX_TOOLWINDOW to the paper itself
    // (which would cause Windows to skip it when choosing the next window to activate).
    private static IntPtr _hiddenOwner;

    private static IntPtr GetOrCreateHiddenOwner()
    {
        if (_hiddenOwner != IntPtr.Zero && IsWindow(_hiddenOwner))
        {
            return _hiddenOwner;
        }

        _hiddenOwner = CreateWindowEx(
            WsExToolWindow,
            "Static",
            "",
            0, // WS_OVERLAPPED (no visible chrome)
            -100, -100, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        return _hiddenOwner;
    }

    // WS_EX_NOACTIVATE: the window can never become foreground, so clicking it never steals
    // focus from (and forces a repaint of) whatever app was in front — the click "flash".
    public static void ApplyNoActivateStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate);
    }

    public static void ApplyWindowSwitcherVisibility(Window window, bool visible)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (visible)
        {
            // Remove the hidden owner — the window re-appears in Alt+Tab.
            SetWindowLongPtr(handle, GwlpHwndParent, IntPtr.Zero);
        }
        else
        {
            // Set the hidden TOOLWINDOW as owner — owned windows are excluded from
            // Alt+Tab without needing WS_EX_TOOLWINDOW on the paper itself, so Windows
            // won't skip the paper when choosing the next window to activate.
            SetWindowLongPtr(handle, GwlpHwndParent, GetOrCreateHiddenOwner());
        }

        // Ensure WS_EX_TOOLWINDOW is cleared from the paper in both cases. This undoes the
        // style that older versions may have left behind.
        var exStyle = GetWindowLong(handle, GwlExStyle);
        var cleaned = (exStyle & ~WsExToolWindow) & ~WsExAppWindow;
        if (visible)
        {
            // No special ex-style needed when visible in switcher.
            cleaned = exStyle & ~WsExToolWindow;
        }
        if (cleaned != exStyle)
        {
            SetWindowLong(handle, GwlExStyle, cleaned);
        }

        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);

        if (visible && window.IsVisible)
        {
            RefreshShellWindowListEntry(handle);
        }
    }

    private static void RefreshShellWindowListEntry(IntPtr handle)
    {
        // The shell may keep Alt+Tab / Task View membership cached after WS_EX_TOOLWINDOW
        // changes. A no-activate hide/show makes it rebuild the entry without stealing focus.
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpHideWindow);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }

    // Set topmost / no-topmost without moving, sizing, or activating the window. When dropping
    // out of topmost, optionally re-insert directly above a specific window (fullscreen avoidance).
    public static void ApplyTopmostZOrder(Window window, bool topmost, IntPtr insertAfter)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            topmost ? HwndTopmost : HwndNoTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

        if (!topmost && insertAfter != IntPtr.Zero)
        {
            SetWindowPos(
                handle,
                insertAfter,
                0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    public static bool IsTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;
    }

    public static void TrySetForegroundWindow(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(handle);
        }
    }

    public static void ClearCurrentThreadKeyboardFocus()
    {
        _ = SetFocus(IntPtr.Zero);
    }

    public static bool TryGetCursorScreenPosition(out Point point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    public static void BeginWindowCaptionDrag(Window window)
    {
        _ = TryBeginWindowCaptionDrag(window);
    }

    public static bool TryBeginWindowCaptionDrag(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = ReleaseCapture();
        _ = SendMessage(handle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }
}
