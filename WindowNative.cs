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
    private static readonly IntPtr HwndTop = IntPtr.Zero;
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
    private const int DwmWaExtendedFrameBounds = 9;

    // A tiny off-screen TOOLWINDOW serves as the native owner for a paper hidden from
    // Alt+Tab. Each paper must keep its own owner: papers sharing one owner become one
    // native window group, so activating any member can raise the other papers as well.
    private static IntPtr GetOrCreateHiddenOwner(IntPtr hiddenOwner)
    {
        if (hiddenOwner != IntPtr.Zero && IsWindow(hiddenOwner))
        {
            return hiddenOwner;
        }

        return CreateWindowEx(
            WsExToolWindow,
            "Static",
            "",
            0, // WS_OVERLAPPED (no visible chrome)
            -100, -100, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
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

    public static void ApplyWindowSwitcherVisibility(
        Window window,
        bool visible,
        ref IntPtr hiddenOwner)
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
            // Set this paper's hidden TOOLWINDOW as owner — owned windows are excluded from
            // Alt+Tab without needing WS_EX_TOOLWINDOW on the paper itself, so Windows
            // won't skip the paper when choosing the next window to activate.
            hiddenOwner = GetOrCreateHiddenOwner(hiddenOwner);
            SetWindowLongPtr(handle, GwlpHwndParent, hiddenOwner);
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

        if (visible)
        {
            ReleaseWindowSwitcherOwner(ref hiddenOwner);
        }
    }

    public static void DetachAndReleaseWindowSwitcherOwner(
        Window window,
        ref IntPtr hiddenOwner)
    {
        if (hiddenOwner == IntPtr.Zero)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowLongPtr(handle, GwlpHwndParent, IntPtr.Zero);
        }

        ReleaseWindowSwitcherOwner(ref hiddenOwner);
    }

    public static void ReleaseWindowSwitcherOwner(ref IntPtr hiddenOwner)
    {
        if (hiddenOwner != IntPtr.Zero && IsWindow(hiddenOwner))
        {
            _ = DestroyWindow(hiddenOwner);
        }

        hiddenOwner = IntPtr.Zero;
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
        ApplyTopmostZOrder(new WindowInteropHelper(window).Handle, topmost, insertAfter);
    }

    public static void ApplyTopmostZOrder(IntPtr handle, bool topmost, IntPtr insertAfter)
    {
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

    public static void BringToFrontNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            IsTopmost(window) ? HwndTopmost : HwndTop,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
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

    public static IntPtr ForegroundWindow => GetForegroundWindow();
    public static IntPtr ActiveWindow => GetActiveWindow();
    public static IntPtr KeyboardFocusWindow => GetFocus();

    public static void ClearCurrentThreadInputActivation(IntPtr externalForegroundWindow)
    {
        _ = SetFocus(IntPtr.Zero);
        // Passing a window owned by another input thread clears this thread's active HWND.
        _ = SetActiveWindow(externalForegroundWindow);
    }

    public static bool TryGetCursorScreenPosition(out DeviceScreenPoint point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new DeviceScreenPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    // Commit position and size as one native operation. Edge surfaces use physical screen pixels
    // as their source of truth; assigning WPF Left/Top/Width separately creates observable
    // intermediate HWND rectangles and was the direct cause of one-frame edge clipping.
    public static bool TrySetWindowDeviceBounds(Window window, DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
        return handle != IntPtr.Zero && SetWindowPos(
            handle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public static bool TryGetWindowDeviceBounds(Window window, out DeviceScreenRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect))
        {
            bounds = new DeviceScreenRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return !bounds.IsEmpty;
        }

        bounds = default;
        return false;
    }

    public static bool TryGetWindowScreenBounds(Window window, out Rect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect))
        {
            var topLeft = WindowWorkAreaHelper.DeviceScreenPointToDip(new DeviceScreenPoint(nativeRect.Left, nativeRect.Top));
            var bottomRight = WindowWorkAreaHelper.DeviceScreenPointToDip(new DeviceScreenPoint(nativeRect.Right, nativeRect.Bottom));
            bounds = new Rect(topLeft.ToPoint(), bottomRight.ToPoint());
            return true;
        }

        bounds = Rect.Empty;
        return false;
    }

    public static bool TryGetVisibleFrameScreenBounds(Window window, out Rect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero &&
            DwmGetWindowAttribute(handle, DwmWaExtendedFrameBounds, out var nativeRect, Marshal.SizeOf<NativeRect>()) == 0)
        {
            var topLeft = DevicePointToWindowDip(window, new Point(nativeRect.Left, nativeRect.Top));
            var bottomRight = DevicePointToWindowDip(window, new Point(nativeRect.Right, nativeRect.Bottom));
            bounds = new Rect(topLeft, bottomRight);
            return true;
        }

        bounds = Rect.Empty;
        return false;
    }

    // Presenter settle runs after WPF's Render work, but a transparent top-level window's new
    // surface can still be waiting for the desktop compositor. Use this only at a cross-HWND
    // hand-off boundary, never on an animation frame or ordinary presentation update.
    public static void FlushDesktopComposition() => _ = DwmFlush();

    private static Point DevicePointToWindowDip(Window window, Point point)
    {
        if (PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
        {
            return target.TransformFromDevice.Transform(point);
        }

        return WindowWorkAreaHelper.DeviceScreenPointToDip(DeviceScreenPoint.FromPoint(point)).ToPoint();
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

    // Restore a natively maximized or snapped window at the Win32 level (SW_RESTORE) so the hwnd
    // leaves that state even when WPF's WindowState no longer agrees. Used while collapsing so a
    // capsule dragged afterward isn't "restored to full size" by the shell mid-drag.
    public static void RestoreNativeWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, SwRestore);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out NativeRect pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
