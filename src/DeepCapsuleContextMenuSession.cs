using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

/// <summary>
/// Shared lifecycle for menus launched from NOACTIVATE deep-capsule surfaces
/// (edge slot hosts and the master collapse-all pill). The owner stays passive; WPF owns normal
/// menu capture/outside-click dismissal, while the real popup HWND is promoted and, when Windows
/// grants it foreground, receives keyboard focus. The session also owns capsule topmost
/// suppression and the bounded stale-activation cleanup shared with the tray path.
/// </summary>
internal sealed class DeepCapsuleContextMenuSession
{
    private readonly AppController _controller;
    private readonly string _ownerId;
    private readonly Action<bool>? _onOpenChanged;

    private ContextMenu? _activeMenu;

    public DeepCapsuleContextMenuSession(
        AppController controller,
        string ownerId,
        Action<bool>? onOpenChanged = null)
    {
        _controller = controller;
        _ownerId = ownerId;
        _onOpenChanged = onOpenChanged;
    }

    public void HandleOpened(ContextMenu menu)
    {
        if (_activeMenu != null && !ReferenceEquals(_activeMenu, menu))
        {
            _activeMenu.IsOpen = false;
        }

        System.Threading.Volatile.Write(ref _activeMenu, menu);

        // The owner remains NOACTIVATE. Suppress capsule topmost first, then let the real WPF
        // popup participate in the ordinary menu lifecycle rather than simulating outside clicks.
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, true);
        _onOpenChanged?.Invoke(true);
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
        }

        // Let WPF finish leaving menu mode before checking the UI thread's native focus state.
        _ = menu.Dispatcher.BeginInvoke(
            ClearStaleActivationIfNeeded,
            DispatcherPriority.ContextIdle);
    }

    public void Close()
    {
        var menu = _activeMenu;
        if (menu?.IsOpen == true)
        {
            menu.IsOpen = false;
        }

        // ContextMenu normally raises Closed synchronously. Keep the disconnected/already-closed
        // fallback, but never clear a replacement menu opened re-entrantly.
        if (menu == null || ReferenceEquals(_activeMenu, menu))
        {
            System.Threading.Volatile.Write(ref _activeMenu, null);
            _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
            _onOpenChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        Close();
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

                    // SetForegroundWindow is explicitly best-effort. Only ask WPF to move keyboard
                    // focus after the OS confirms this popup really became foreground; otherwise
                    // keep mouse/menu capture behavior without inventing another activation fallback.
                    WindowNative.TrySetForegroundWindow(source.Handle);
                    if (WindowNative.ForegroundWindow == source.Handle)
                    {
                        menu.Focus();
                    }
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
        // The next tray/capsule menu can then restore focus to the stale paper.
        Keyboard.ClearFocus();
        WindowNative.ClearCurrentThreadInputActivation(foreground);
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
