using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace PaperTodo;

public sealed partial class PaperWindow
{
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
