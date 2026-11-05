namespace PaperTodo;

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropGhost? _nativeQueueDropGhost;

    private void UpdateNativeQueueDropGhost(NativeEdgeQueueDrag drag, DeviceScreenPoint pointer,
        IReadOnlyList<NativeQueueGroup> groups)
    {
        var drop = drag.Drop;
        if (!drag.FreeQueues || drop == null || drop.Decision.Kind != EdgeFreeQueueDropKind.Insert ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry) ||
            !EdgeCapsuleQueueIds.TryGetEdge(drop.DestinationKey, out _, out var side))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }

        var target = groups.FirstOrDefault(g => g.Key == drop.DestinationKey);
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var collapsedTarget = target?.Box.Collapsed == true;
        var top = EdgeCapsuleFreeQueuePolicy.GhostSlotTop(drop.Decision,
            target?.Box.HasMaster == true, pitch, collapsedTarget);
        // Each carried member gets a normal-sized marker. Use the actual dragged
        // capsule width; for folded queues the visible target is the master itself.
        var ownerWidth = _windows.TryGetValue(drag.OwnerId, out var ownerWindow)
            ? ownerWindow.DeepCapsuleRestingVisibleWidth : PaperLayoutDefaults.CapsuleWidth;
        var width = collapsedTarget ? target!.Box.WidthDip : ownerWidth;
        _nativeQueueDropGhost?.ShowAt(geometry, side, top, width,
            drag.CarriedIds.Length, DeepCapsuleGap);
    }

    // The only magnetic movement is a repeatable snap to the source's OUTER
    // slot when dragging one paper away. There is no join magnet or dampening.
    // Snap state deliberately survives drag.Detached: the user may return to the
    // source and pull out again within the same continuous pointer gesture.
    internal double NativeEdgeQueueMagnetizedOwnerTop(string ownerId, double requestedTop)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || !drag.FreeQueues || drag.OwnerId != ownerId ||
            drag.WholeQueue) return requestedTop;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        return EdgeCapsuleFreeQueuePolicy.SnapLeavingSource(requestedTop, drag.SourceTopDip,
            drag.SourceTopDip + Math.Max(0, drag.SourceIds.Length - 1) * pitch,
            drag.ExitSnap);
    }
}
