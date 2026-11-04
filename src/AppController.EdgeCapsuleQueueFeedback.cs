namespace PaperTodo;

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropGhost? _nativeQueueDropGhost;

    private void UpdateNativeQueueDropGhost(NativeEdgeQueueDrag drag, DeviceScreenPoint pointer)
    {
        var drop = drag.Drop;
        if (!drag.FreeQueues || drop == null || !drop.Decision.Accepted ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry) ||
            !EdgeCapsuleQueueIds.TryGetEdge(drop.DestinationKey, out _, out var side))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }

        var target = NativeQueueGroups().FirstOrDefault(g => g.Key == drop.DestinationKey);
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var collapsedTarget = target?.Box.Collapsed == true;
        var top = EdgeCapsuleFreeQueuePolicy.GhostSlotTop(drop.Decision,
            target?.Box.HasMaster == true, pitch, collapsedTarget);
        if (State.UseCapsuleCollapseAll && drop.Decision.Kind == EdgeFreeQueueDropKind.Move)
            top += pitch; // The new queue's master owns slot zero; the silhouette shows papers.
        // This is a one-capsule landing marker, even when Ctrl carries a whole
        // group. Use the actual dragged capsule width rather than the widest
        // member/target; for folded queues the visible target is the master itself.
        var ownerWidth = _windows.TryGetValue(drag.OwnerId, out var ownerWindow)
            ? ownerWindow.DeepCapsuleRestingVisibleWidth : PaperLayoutDefaults.CapsuleWidth;
        var width = collapsedTarget ? target!.Box.WidthDip : ownerWidth;
        _nativeQueueDropGhost?.ShowAt(geometry, side, top, width);
    }

    // Visual-only adhesion. The solver and final release always use the physical pointer,
    // so a nudge cannot silently select an adjacent slot or move a persisted anchor.
    internal double NativeEdgeQueueMagnetizedOwnerTop(string ownerId, double requestedTop)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || !drag.FreeQueues || drag.OwnerId != ownerId) return requestedTop;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        if (!drag.WholeQueue && !drag.Detached)
            return EdgeCapsuleFreeQueuePolicy.ResistLeavingSource(requestedTop, drag.SourceTopDip,
                drag.SourceTopDip + Math.Max(0, drag.SourceIds.Length - 1) * pitch);

        if (drag.Drop is not { Decision.Kind: EdgeFreeQueueDropKind.Insert } drop) return requestedTop;
        var target = NativeQueueGroups().FirstOrDefault(g => g.Key == drop.DestinationKey);
        var slotTop = EdgeCapsuleFreeQueuePolicy.GhostSlotTop(drop.Decision,
            target?.Box.HasMaster == true, pitch, target?.Box.Collapsed == true);
        var offset = drag.WholeQueue
            ? drag.OwnerOffsetDip - (State.UseCapsuleCollapseAll ? pitch : 0) : 0;
        var ownerTop = slotTop + offset;
        return EdgeCapsuleFreeQueuePolicy.NudgeToward(requestedTop, ownerTop,
            EdgeCapsuleFreeQueuePolicy.JoinNudge);
    }
}
