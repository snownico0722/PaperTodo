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
        var top = EdgeCapsuleFreeQueuePolicy.GhostSlotTop(drop.Decision,
            target?.Box.HasMaster == true, pitch);
        if (State.UseCapsuleCollapseAll && drop.Decision.Kind == EdgeFreeQueueDropKind.Move)
            top += pitch; // The new queue's master owns slot zero; the silhouette shows papers.
        var width = target?.Box.WidthDip ??
            drag.CarriedIds.Select(id => _windows.TryGetValue(id, out var window)
                ? window.DeepCapsuleRestingVisibleWidth : PaperLayoutDefaults.CapsuleWidth).DefaultIfEmpty(PaperLayoutDefaults.CapsuleWidth).Max();
        _nativeQueueDropGhost?.ShowAt(geometry, side, top, drag.CarriedIds.Length,
            Math.Max(PaperLayoutDefaults.CapsuleWidth, width), DeepCapsuleGap);
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
            target?.Box.HasMaster == true, pitch);
        var offset = drag.WholeQueue
            ? drag.OwnerOffsetDip - (State.UseCapsuleCollapseAll ? pitch : 0) : 0;
        var ownerTop = slotTop + offset;
        return EdgeCapsuleFreeQueuePolicy.NudgeToward(requestedTop, ownerTop,
            EdgeCapsuleFreeQueuePolicy.JoinNudge);
    }
}
