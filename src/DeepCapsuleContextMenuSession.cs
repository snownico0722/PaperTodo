using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Point = System.Windows.Point;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

/// <summary>
/// Shared open/close chrome for menus launched from NOACTIVATE deep-capsule surfaces
/// (edge slot hosts and the master collapse-all pill). Owns topmost promotion, outside-close
/// guards, owner-set bookkeeping, and the conditional stale-activation clear that protects
/// Hardcodet's tray menu from first-open auto-dismiss.
/// </summary>
internal sealed class DeepCapsuleContextMenuSession
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private readonly AppController _controller;
    private readonly string _ownerId;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Point, bool> _isPointInsideOwnerSurface;
    private readonly Action<bool>? _onOpenChanged;

    private ContextMenu? _activeMenu;
    private long _openVersion;
    private ContextMenu? _pendingCloseMenu;
    private long _pendingCloseVersion;
    private bool _closeScheduled;

    private IntPtr _foregroundHook;
    private IntPtr _mouseHook;
    private WinEventDelegate? _foregroundProc;
    private LowLevelMouseProc? _mouseProc;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookStruct
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    public DeepCapsuleContextMenuSession(
        AppController controller,
        string ownerId,
        Dispatcher dispatcher,
        Func<Point, bool> isPointInsideOwnerSurface,
        Action<bool>? onOpenChanged = null)
    {
        _controller = controller;
        _ownerId = ownerId;
        _dispatcher = dispatcher;
        _isPointInsideOwnerSurface = isPointInsideOwnerSurface;
        _onOpenChanged = onOpenChanged;
    }

    public ContextMenu? ActiveMenu => _activeMenu;

    public bool IsOpen => _activeMenu?.IsOpen == true;

    public void HandleOpened(ContextMenu menu)
    {
        if (_activeMenu != null && !ReferenceEquals(_activeMenu, menu))
        {
            _activeMenu.IsOpen = false;
        }

        System.Threading.Interlocked.Increment(ref _openVersion);
        System.Threading.Volatile.Write(ref _activeMenu, menu);

        // Suppress capsule topmost first, then promote the popup so z-order work cannot bury it.
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, true);
        _onOpenChanged?.Invoke(true);
        StartGuards();
        Promote(menu);
        _ = menu.Dispatcher.BeginInvoke(
            () => Promote(menu),
            DispatcherPriority.Input);
    }

    public void HandleClosed(ContextMenu menu)
    {
        if (ReferenceEquals(_activeMenu, menu))
        {
            System.Threading.Volatile.Write(ref _activeMenu, null);
            _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
            _onOpenChanged?.Invoke(false);
            StopGuards();
        }

        // Let WPF finish leaving menu mode before checking the UI thread's native focus state.
        _ = menu.Dispatcher.BeginInvoke(
            ClearStaleActivationIfNeeded,
            DispatcherPriority.ContextIdle);
    }

    public void RequestClose()
    {
        var menu = System.Threading.Volatile.Read(ref _activeMenu);
        var version = System.Threading.Interlocked.Read(ref _openVersion);
        if (menu == null)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => QueueClose(menu, version)));
            return;
        }

        QueueClose(menu, version);
    }

    public void Close()
    {
        var menu = _activeMenu;
        if (menu?.IsOpen == true)
        {
            menu.IsOpen = false;
        }

        // Closing a WPF ContextMenu normally raises Closed synchronously. Keep this fallback for
        // already-closed/disconnected popups, but never clear a replacement opened re-entrantly.
        if (menu == null || ReferenceEquals(_activeMenu, menu))
        {
            System.Threading.Volatile.Write(ref _activeMenu, null);
            _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
            _onOpenChanged?.Invoke(false);
            StopGuards();
        }
    }

    public void Dispose()
    {
        Close();
        StopGuards();
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
    }

    public static void Promote(ContextMenu menu)
    {
        if (menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowNative.ApplyTopmostZOrder(source.Handle, topmost: true, insertAfter: IntPtr.Zero);
        }
    }

    public static bool IsPointInsideElement(FrameworkElement? element, Point screenPoint)
    {
        if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localPoint = element.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= element.ActualWidth &&
                localPoint.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPointInsideOpenMenuItems(
        ItemsControl owner,
        Point screenPoint)
    {
        for (var index = 0; index < owner.Items.Count; index++)
        {
            var container =
                owner.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement ??
                owner.Items[index] as FrameworkElement;
            if (container == null || !container.IsVisible)
            {
                continue;
            }

            // WPF renders each submenu in a separate Popup HWND. PointFromScreen still works on
            // the generated child MenuItem/Separator even though it is no longer inside the root
            // ContextMenu's visual rectangle, so treat those live containers as part of the same
            // logical menu surface.
            if (IsPointInsideElement(container, screenPoint))
            {
                return true;
            }

            if (container is MenuItem menuItem &&
                menuItem.IsSubmenuOpen &&
                IsPointInsideOpenMenuItems(menuItem, screenPoint))
            {
                return true;
            }
        }

        return false;
    }

    private void QueueClose(ContextMenu menu, long version)
    {
        if (!ReferenceEquals(_activeMenu, menu) ||
            _openVersion != version ||
            !menu.IsOpen)
        {
            return;
        }

        _pendingCloseMenu = menu;
        _pendingCloseVersion = version;
        if (_closeScheduled)
        {
            return;
        }

        _closeScheduled = true;
        // The low-level hook observes mouse-down before WPF dispatches the same input. Keep an
        // asynchronous close as a final guard for real outside clicks; open submenu surfaces are
        // filtered explicitly in IsPointInsideContextSurface instead of relying on this delay.
        _ = _dispatcher.BeginInvoke(
            new Action(ExecuteQueuedClose),
            DispatcherPriority.Background);
    }

    private void ExecuteQueuedClose()
    {
        var menu = _pendingCloseMenu;
        var version = _pendingCloseVersion;
        _pendingCloseMenu = null;
        _pendingCloseVersion = 0;
        _closeScheduled = false;

        if (menu != null &&
            ReferenceEquals(_activeMenu, menu) &&
            _openVersion == version)
        {
            Close();
        }
    }

    private void ClearStaleActivationIfNeeded()
    {
        if (_activeMenu?.IsOpen == true || InputManager.Current.IsInMenuMode)
        {
            return;
        }

        var foreground = WindowNative.ForegroundWindow;
        if (foreground == IntPtr.Zero || IsWindowFromCurrentProcess(foreground))
        {
            return;
        }

        var active = WindowNative.ActiveWindow;
        var focus = WindowNative.KeyboardFocusWindow;
        if ((active == IntPtr.Zero || !IsWindowFromCurrentProcess(active)) &&
            (focus == IntPtr.Zero || !IsWindowFromCurrentProcess(focus)))
        {
            return;
        }

        // A promoted WPF Popup can leave its owner active after closing. Hardcodet's next tray
        // menu then opens and immediately closes, so clear only this stale cross-app handoff.
        Keyboard.ClearFocus();
        WindowNative.ClearCurrentThreadInputActivation(foreground);
    }

    private void StartGuards()
    {
        if (_foregroundHook == IntPtr.Zero)
        {
            _foregroundProc = OnForegroundChanged;
            _foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _foregroundProc,
                0,
                0,
                WineventOutOfContext);
        }

        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = OnMouseHook;
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        }
    }

    private void StopGuards()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _foregroundProc = null;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || IsWindowFromCurrentProcess(hwnd))
        {
            return;
        }

        RequestClose();
    }

    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam) && _activeMenu?.IsOpen == true)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var screenPoint = new Point(hook.Point.X, hook.Point.Y);
            if (!IsPointInsideContextSurface(screenPoint))
            {
                RequestClose();
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideContextSurface(Point screenPoint)
    {
        var menu = _activeMenu;
        if (IsPointInsideElement(menu, screenPoint) ||
            (menu != null && IsPointInsideOpenMenuItems(menu, screenPoint)))
        {
            return true;
        }

        return _isPointInsideOwnerSurface(screenPoint);
    }

    private static bool IsMouseButtonDownMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown ||
            value == WmRButtonDown ||
            value == WmMButtonDown ||
            value == WmXButtonDown;
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
