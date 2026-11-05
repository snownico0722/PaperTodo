namespace PaperTodo;

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropGhost? _nativeQueueDropGhost;

    private double NativeDropGhostMemberWidth(string paperId, MonitorGeometry geometry) =>
        _windows.TryGetValue(paperId, out var window)
            ? window.DeepCapsuleRestingVisibleWidthFor(geometry)
            : PaperLayoutDefaults.CapsuleWidth;

    private void UpdateNativeQueueDropGhost(NativeEdgeQueueDrag drag, DeviceScreenPoint pointer,
        IReadOnlyList<NativeQueueGroup> groups)
    {
        var drop = drag.Drop;
        if (!drag.FreeQueues || drop == null || !drop.Decision.Accepted ||
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
        // Preview every carried member at its own docked width. Independent
        // placements prepend their short master; merges only carry members.
        var hasMaster = drop.Decision.Kind == EdgeFreeQueueDropKind.Move && State.UseCapsuleCollapseAll;
        var widths = new List<double>(drag.CarriedIds.Length + (hasMaster ? 1 : 0));
        if (hasMaster)
            widths.Add(_masterCapsules.TryGetValue(drag.SourceKey, out var master)
                ? master.DesiredDockedWidth : PaperLayoutDefaults.CapsuleWidth);
        foreach (var id in drag.CarriedIds)
            widths.Add(NativeDropGhostMemberWidth(id, geometry));
        _nativeQueueDropGhost?.ShowAt(geometry, side, top, widths, DeepCapsuleGap);
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

