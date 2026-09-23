using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

/// <summary>
/// Shared lifecycle for menus launched from NOACTIVATE deep-capsule surfaces
/// (edge slot hosts and the master collapse-all pill). The owner remains NOACTIVATE; once the
/// real WPF popup HWND exists, the menu itself receives foreground/focus so WPF can own normal
/// menu capture and outside-click dismissal. A foreground-change hook remains only as a bounded
/// fallback, alongside owner-set bookkeeping and stale-activation cleanup for the tray menu.
/// </summary>
internal sealed class DeepCapsuleContextMenuSession
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    private readonly AppController _controller;
    private readonly string _ownerId;
    private readonly Dispatcher _dispatcher;
    private readonly Action<bool>? _onOpenChanged;

    private ContextMenu? _activeMenu;
    private long _openVersion;

    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundProc;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    public DeepCapsuleContextMenuSession(
        AppController controller,
        string ownerId,
        Dispatcher dispatcher,
        Action<bool>? onOpenChanged = null)
    {
        _controller = controller;
        _ownerId = ownerId;
        _dispatcher = dispatcher;
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

        // Keep the NOACTIVATE owner below the menu, then let the real popup HWND own the active
        // menu session. This is the same lifecycle used by the local wpf-notifyicon fork.
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, true);
        _onOpenChanged?.Invoke(true);
        StartForegroundGuard();
        Promote(menu);
        QueuePopupActivation(menu, attemptsRemaining: 3);
    }

    public void HandleClosed(ContextMenu menu)
    {
        if (ReferenceEquals(_activeMenu, menu))
        {
            System.Threading.Volatile.Write(ref _activeMenu, null);
            _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
            _onOpenChanged?.Invoke(false);
            StopForegroundGuard();
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
            _ = _dispatcher.BeginInvoke(
                new Action(() => ExecuteRequestedClose(menu, version)),
                DispatcherPriority.Input);
            return;
        }

        ExecuteRequestedClose(menu, version);
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
            StopForegroundGuard();
        }
    }

    public void Dispose()
    {
        Close();
        StopForegroundGuard();
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
    }

    private void QueuePopupActivation(ContextMenu menu, int attemptsRemaining)
    {
        _ = menu.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(System.Threading.Volatile.Read(ref _activeMenu), menu) ||
                    !menu.IsOpen)
                {
                    return;
                }

                if (PresentationSource.FromVisual(menu) is HwndSource source &&
                    source.Handle != IntPtr.Zero)
                {
                    WindowNative.ApplyTopmostZOrder(
                        source.Handle,
                        topmost: true,
                        insertAfter: IntPtr.Zero);
                    WindowNative.TrySetForegroundWindow(source.Handle);
                    menu.Focus();
                    return;
                }

                if (attemptsRemaining > 1)
                {
                    QueuePopupActivation(menu, attemptsRemaining - 1);
                }
            }));
    }

    private static void Promote(ContextMenu menu)
    {
        if (menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowNative.ApplyTopmostZOrder(
                source.Handle,
                topmost: true,
                insertAfter: IntPtr.Zero);
        }
    }

    private void ExecuteRequestedClose(ContextMenu menu, long version)
    {
        if (ReferenceEquals(_activeMenu, menu) &&
            _openVersion == version &&
            menu.IsOpen)
        {
            Close();
        }
    }

    private void ClearStaleActivationIfNeeded()
    {
        if (_activeMenu?.IsOpen == true)
        {
            return;
        }

        ClearStaleApplicationActivationIfNeeded();
    }

    internal static void ClearStaleApplicationActivationIfNeeded()
    {
        if (InputManager.Current.IsInMenuMode)
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

        // A WPF popup or a previously active paper can leave this UI thread with an
        // application-owned active/focus HWND after foreground moved to another process.
        // Hardcodet's next tray menu then opens and immediately closes, so clear only
        // this stale cross-app handoff.
        Keyboard.ClearFocus();
        WindowNative.ClearCurrentThreadInputActivation(foreground);
    }

    private void StartForegroundGuard()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            return;
        }

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

    private void StopForegroundGuard()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _foregroundProc = null;
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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
