namespace PaperTodo;

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropGhost? _nativeQueueDropGhost;
    private string? _singleCapsuleGhostOwnerId;

    private double NativeDropGhostMemberWidth(string paperId, MonitorGeometry geometry) =>
        _windows.TryGetValue(paperId, out var window)
            ? window.DeepCapsuleRestingVisibleWidthFor(geometry)
            : PaperLayoutDefaults.CapsuleWidth;

    private void UpdateNativeQueueDropGhost(NativeEdgeQueueDrag drag, DeviceScreenPoint pointer,
        IReadOnlyList<NativeQueueGroup> groups)
    {
        var drop = drag.Drop;
        if (drop == null || !drop.Decision.Accepted ||
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

    // Same projection as the ordinary cross-edge commit: the preview is never
    // allowed to choose a different target index from the eventual release.
    private (int Index, double FirstTopDip) LegacyCrossEdgeDrop(
        MonitorGeometry geometry, string monitor, string side,
        DeviceScreenPoint pointer, int existingMembers)
    {
        var offset = State.UseCapsuleCollapseAll ? 1 : 0;
        var firstTop = EdgeCapsuleLayout.TopForIndex(offset,
            DeepCapsuleStartTopMarginForQueue(monitor,
                side == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right),
            geometry.LocalWorkAreaDip, existingMembers + 1 + offset, DeepCapsuleGap);
        var index = Math.Clamp((int)Math.Floor(
            (geometry.DeviceYToLocalDip(pointer.Y) - firstTop) /
            EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap) + .5), 0, existingMembers);
        return (index, firstTop);
    }

    // Ordinary sorting keeps its existing index, animation and commit paths.
    // Only the landing silhouette is shared with free-queue dragging.
    internal void PreviewSingleCapsuleDropGhost(PaperData owner, int targetIndex,
        DeviceScreenPoint pointer, bool floating)
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode ||
            !_windows.ContainsKey(owner.Id))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }

        MonitorGeometry geometry;
        string side;
        var sourceKey = QueueKey(owner);
        var targetKey = sourceKey;
        if (floating)
        {
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out geometry))
            {
                _nativeQueueDropGhost?.Hide();
                return;
            }
            var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
            side = pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
                ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
            targetKey = EdgeKey(monitor, side);
        }
        else
        {
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    owner.CapsuleMonitorDeviceName, out geometry))
            {
                _nativeQueueDropGhost?.Hide();
                return;
            }
            side = DeepCapsuleSides.Normalize(owner.CapsuleSide);
        }

        if (IsCapsuleCollapseAllActiveForQueue(targetKey))
        {
            // An entirely folded destination has no visible member slots.
            _nativeQueueDropGhost?.Hide();
            return;
        }

        double top;
        if (floating && targetKey != sourceKey)
        {
            var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
            var existing = DeepCapsulePapersInOrder().Count(p =>
                p.Id != owner.Id && QueueKey(p) == targetKey);
            var projection = LegacyCrossEdgeDrop(geometry, monitor, side, pointer, existing);
            top = projection.FirstTopDip +
                projection.Index * EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        }
        else
        {
            // The source queue's original anchor and passive offset still apply,
            // even while its neighbours temporarily yield to the dragged capsule.
            var plan = BuildRestingEdgeQueuePlan();
            if (!plan.Placements.TryGetValue(owner.Id, out var placement))
            {
                _nativeQueueDropGhost?.Hide();
                return;
            }
            targetIndex = Math.Clamp(targetIndex, 0,
                placement.SlotCount - placement.VisualOffset - 1);
            var visualIndex = targetIndex + placement.VisualOffset;
            var margin = DeepCapsuleStartTopMarginFor(owner);
            top = (EdgeCapsuleFreeQueuesEnabled
                ? EdgeCapsuleLayout.FreeQueueTopForIndex(
                    visualIndex, margin, geometry.LocalWorkAreaDip, DeepCapsuleGap)
                : EdgeCapsuleLayout.TopForIndex(
                    visualIndex, margin, geometry.LocalWorkAreaDip,
                    placement.SlotCount, DeepCapsuleGap)) + placement.TopOffsetDip;
        }

        _nativeQueueDropGhost ??= new EdgeCapsuleQueueDropGhost();
        _singleCapsuleGhostOwnerId = owner.Id;
        _nativeQueueDropGhost.ShowAt(geometry, side, top,
            [NativeDropGhostMemberWidth(owner.Id, geometry)], DeepCapsuleGap);
    }

    internal void EndSingleCapsuleDropGhost(string ownerId)
    {
        if (_singleCapsuleGhostOwnerId != ownerId) return;
        _singleCapsuleGhostOwnerId = null;
        if (_nativeEdgeQueueDrag != null)
            _nativeQueueDropGhost?.Hide();
        else
        {
            _nativeQueueDropGhost?.Dispose();
            _nativeQueueDropGhost = null;
        }
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

