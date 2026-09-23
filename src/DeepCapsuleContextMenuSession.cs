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
/// (edge slot hosts and the master collapse-all pill). The owner stays passive; WPF owns normal
/// menu capture/outside-click dismissal, while the real popup HWND is promoted and receives the
/// same foreground/focus handoff used by the tray popup path.
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

        _activeMenu = menu;
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, true);
        _onOpenChanged?.Invoke(true);
        QueuePopupActivation(menu);
    }

    public void HandleClosed(ContextMenu menu)
    {
        if (ReferenceEquals(_activeMenu, menu))
        {
            _activeMenu = null;
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
            _activeMenu = null;
            _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
            _onOpenChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        Close();
        _controller.SetDeepCapsuleContextMenuOpen(_ownerId, false);
    }

    private static void QueuePopupActivation(ContextMenu menu)
    {
        // ContextMenu.Opened runs after WPF entered menu mode. One Input turn is only to resolve
        // the real popup HWND; if it is still unavailable, leave the lifecycle to WPF.
        _ = menu.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!menu.IsOpen ||
                    PresentationSource.FromVisual(menu) is not HwndSource source ||
                    source.Handle == IntPtr.Zero)
                {
                    return;
                }

                WindowNative.ApplyTopmostZOrder(
                    source.Handle,
                    topmost: true,
                    insertAfter: IntPtr.Zero);
                WindowNative.TrySetForegroundWindow(source.Handle);
                menu.Focus();
            }));
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
        // Hardcodet's next tray menu can then restore focus to the stale paper.
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
