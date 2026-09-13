using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _deepCapsuleContextMenuInitializationQueued;
    private bool _deepCapsuleContextMenuInitialized;

    private void ScheduleDeepCapsuleSlotContextMenuInitialization()
    {
        if (_deepCapsuleContextMenuInitializationQueued ||
            _deepCapsuleContextMenuInitialized ||
            _edgeCapsuleHost == null)
        {
            return;
        }

        var host = _edgeCapsuleHost;
        _deepCapsuleContextMenuInitializationQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _deepCapsuleContextMenuInitializationQueued = false;
                if (IsClosed ||
                    _deepCapsuleContextMenuInitialized ||
                    !ReferenceEquals(host, _edgeCapsuleHost))
                {
                    return;
                }

                host.SetContextMenu(BuildDeepCapsuleSlotContextMenu());
                _deepCapsuleContextMenuInitialized = true;
            }),
            System.Windows.Threading.DispatcherPriority.SystemIdle);
    }

    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);

        menu.Opened += (_, _) => _deepCapsuleContextMenuSession.HandleOpened(menu);
        menu.Closed += (_, _) => _deepCapsuleContextMenuSession.HandleClosed(menu);

        return menu;
    }

    private void QueueCloseDeepCapsuleSlotContextMenu() =>
        _deepCapsuleContextMenuSession.RequestClose();

    private void CloseDeepCapsuleSlotContextMenu() =>
        _deepCapsuleContextMenuSession.Close();

    private void OnDeepCapsuleContextMenuOpenChanged(bool open)
    {
        if (_edgeCapsule.ContextMenuOpen != open)
        {
            SetEdgeCapsuleContextMenuOpen(open);
        }

        // The reducer can reset ContextMenuOpen while detaching; still refresh local topmost so
        // slot hosts mirror the shared owner-set immediately.
        RefreshDeepCapsuleSlotTopmost();
        if (!open)
        {
            InvalidateEdgeCapsulePointer();
        }
    }

    private bool IsPointInsideDeepCapsuleOwnerSurface(Point screenPoint) =>
        _edgeCapsuleHost?.ContainsWindowScreenPoint(screenPoint) == true;
}
