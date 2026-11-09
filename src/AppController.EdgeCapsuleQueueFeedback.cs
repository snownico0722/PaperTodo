namespace PaperTodo;

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropGhost? _nativeQueueDropGhost;
    private string? _singleCapsuleGhostOwnerId;
    private (string OwnerId, string TargetKey, int Index)? _legacyLandingPreview;
    private (string OwnerId, string QueueKey)? _vacatedLegacyMaster;

    private double NativeDropGhostMasterWidth(string queueKey, MonitorGeometry geometry) =>
        _masterCapsules.TryGetValue(queueKey, out var master)
            ? master.DesiredDockedWidthFor(geometry)
            : PaperLayoutDefaults.CapsuleWidth;

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
        Action? raiseDrag = _windows.TryGetValue(drag.OwnerId, out var dragWindow)
            ? dragWindow.BringEdgeCapsuleDragAboveGhost : null;
        var topmost = dragWindow?.EdgeCapsuleDragIsTopmost ?? true;
        if (collapsedTarget && drop.Decision.Kind == EdgeFreeQueueDropKind.Insert)
        {
            _nativeQueueDropGhost?.ShowMergeHaloAt(geometry, side, drop.Decision.TopDip,
                NativeDropGhostMasterWidth(drop.DestinationKey, geometry), raiseDrag, topmost);
            return;
        }

        // Preview every carried member at its own docked width. Independent
        // placements prepend their short master; merges only carry members.
        var hasMaster = drop.Decision.Kind == EdgeFreeQueueDropKind.Move && State.UseCapsuleCollapseAll;
        var widths = new List<double>(drag.CarriedIds.Length + (hasMaster ? 1 : 0));
        if (hasMaster)
            widths.Add(NativeDropGhostMasterWidth(drag.SourceKey, geometry));
        foreach (var id in drag.CarriedIds)
            widths.Add(NativeDropGhostMemberWidth(id, geometry));
        _nativeQueueDropGhost?.ShowAt(geometry, side, top, widths, DeepCapsuleGap, raiseDrag, topmost);
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

    private void PreviewVacatedLegacyMaster(PaperData owner)
    {
        if (!State.UseCapsuleCollapseAll || _vacatedLegacyMaster != null) return;
        var sourceKey = QueueKey(owner);
        if (DeepCapsulePapersInOrder().Count(p => QueueKey(p) == sourceKey) != 1 ||
            !_masterCapsules.TryGetValue(sourceKey, out var master) || !master.IsVisible)
            return;
        _vacatedLegacyMaster = (owner.Id, sourceKey);
        CompleteEdgeCapsuleQueueCompositionProxyFor(master);
        master.Hide();
    }

    private void RestoreVacatedLegacyMaster(string ownerId)
    {
        if (_vacatedLegacyMaster is not { } previous || previous.OwnerId != ownerId) return;
        _vacatedLegacyMaster = null;
        if (_masterCapsules.TryGetValue(previous.QueueKey, out var master))
        {
            var remaining = DeepCapsulePapersInOrder()
                .Count(p => QueueKey(p) == previous.QueueKey);
            // The withdrawn master already exists; restoring it is not a new entrance.
            if (remaining > 0 && !master.IsVisible)
                master.ShowPlaced(remaining, IsCapsuleCollapseAllActiveForQueue(previous.QueueKey),
                    animate: false);
        }
    }

    // Project ordinary cross-edge membership without mutating State.Papers.
    // The existing capsule placement animator moves the source gap shut and the
    // target suffix apart. Repeated pointer samples in one slot do no work.
    private void PreviewLegacyCrossEdgeLanding(PaperData owner, string targetKey, int index)
    {
        if (_legacyLandingPreview is { } current && current.OwnerId == owner.Id &&
            current.TargetKey == targetKey && current.Index == index) return;
        if (_legacyLandingPreview is { } prior && prior.OwnerId == owner.Id &&
            prior.TargetKey != targetKey &&
            _masterCapsules.TryGetValue(prior.TargetKey, out var priorMaster))
            priorMaster.PreviewQueueTop(DeepCapsuleStartTopMarginForQueueKey(prior.TargetKey),
                animate: true);

        var members = DeepCapsulePapersInOrder()
            .Where(p => p.Id != owner.Id)
            .Select(p => new EdgeCapsuleQueueMember(p, QueueKey(p))).ToList();
        var targetPositions = members.Select((member, position) => (member, position))
            .Where(row => row.member.QueueKey == targetKey)
            .Select(row => row.position).ToArray();
        var at = index < targetPositions.Length ? targetPositions[index]
            : targetPositions.Length > 0 ? targetPositions[^1] + 1 : members.Count;
        members.Insert(at, new EdgeCapsuleQueueMember(owner, targetKey));
        var preview = BuildEdgeQueuePlan(members, State.UseCapsuleCollapseAll);
        foreach (var (id, placement) in preview.Placements)
            if (id != owner.Id && _windows.TryGetValue(id, out var window))
                window.PreviewDeepCapsulePlacement(placement);

        if (State.UseCapsuleCollapseAll && _masterCapsules.TryGetValue(targetKey, out var master) &&
            EdgeCapsuleQueueIds.TryGetEdge(targetKey, out var monitor, out _) &&
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry))
        {
            var count = preview.Queues.First(q => q.Key == targetKey).Papers.Count + 1;
            var margin = EdgeCapsuleLayout.NormalizeStartTopMargin(
                DeepCapsuleStartTopMarginForQueueKey(targetKey),
                geometry.LocalWorkAreaDip, count, DeepCapsuleGap);
            master.PreviewQueueTop(margin, animate: true);
        }
        _legacyLandingPreview = (owner.Id, targetKey, index);
    }

    private void RestoreLegacyCrossEdgeLanding(string ownerId)
    {
        if (_legacyLandingPreview is not { } previous || previous.OwnerId != ownerId) return;
        _legacyLandingPreview = null;
        var resting = BuildRestingEdgeQueuePlan();
        foreach (var (id, placement) in resting.Placements)
            if (id != ownerId && _windows.TryGetValue(id, out var window))
                window.PreviewDeepCapsulePlacement(placement);
        if (_masterCapsules.TryGetValue(previous.TargetKey, out var master))
            master.PreviewQueueTop(DeepCapsuleStartTopMarginForQueueKey(previous.TargetKey),
                animate: true);
    }

    // Ordinary sorting keeps its existing index and commit paths, while
    // cross-edge insertions reuse the current capsule movement animation.
    internal void PreviewSingleCapsuleDropGhost(PaperData owner, int targetIndex,
        DeviceScreenPoint pointer, bool floating)
    {
        if (!State.UseCapsuleMode || !State.UseDeepCapsuleMode ||
            !_windows.ContainsKey(owner.Id))
        {
            RestoreLegacyCrossEdgeLanding(owner.Id);
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
                RestoreLegacyCrossEdgeLanding(owner.Id);
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
                RestoreLegacyCrossEdgeLanding(owner.Id);
                _nativeQueueDropGhost?.Hide();
                return;
            }
            side = DeepCapsuleSides.Normalize(owner.CapsuleSide);
        }

        var vacatesSource = floating && State.UseCapsuleCollapseAll &&
            DeepCapsulePapersInOrder().Count(p => QueueKey(p) == sourceKey) == 1;
        if (!floating) RestoreVacatedLegacyMaster(owner.Id);
        var foldedTarget = IsCapsuleCollapseAllActiveForQueue(targetKey);
        if (foldedTarget && (!floating || targetKey == sourceKey) && !vacatesSource)
        {
            // A folded source does not expose any editable member slots. A
            // cross-edge destination can still accept the member on release.
            RestoreLegacyCrossEdgeLanding(owner.Id);
            _nativeQueueDropGhost?.Hide();
            return;
        }

        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var widths = new List<double>(2);
        double top;
        if (floating && targetKey != sourceKey)
        {
            var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
            var existing = DeepCapsulePapersInOrder().Count(p =>
                p.Id != owner.Id && QueueKey(p) == targetKey);
            var projection = LegacyCrossEdgeDrop(geometry, monitor, side, pointer, existing);
            if (State.UseCapsuleCollapseAll && (existing == 0 || foldedTarget))
            {
                // The destination has no visible members (or is completely
                // folded): show its actual short master, not a phantom member.
                top = projection.FirstTopDip - pitch;
                widths.Add(_masterCapsules.TryGetValue(targetKey, out var targetMaster)
                    ? targetMaster.DesiredDockedWidthFor(geometry)
                    : NativeDropGhostMasterWidth(sourceKey, geometry));
                if (!foldedTarget)
                    widths.Add(NativeDropGhostMemberWidth(owner.Id, geometry));
            }
            else
            {
                top = projection.FirstTopDip + projection.Index * pitch;
                widths.Add(NativeDropGhostMemberWidth(owner.Id, geometry));
            }
            if (!EdgeCapsuleFreeQueuesEnabled)
            {
                PreviewLegacyCrossEdgeLanding(owner, targetKey, projection.Index);
            }
        }
        else
        {
            RestoreLegacyCrossEdgeLanding(owner.Id);
            // Floating back into the source queue needs the same neighbour
            // displacement that docked sorting already uses. A ghost alone
            // otherwise promises a gap which is not on screen until release.
            if (floating && !EdgeCapsuleFreeQueuesEnabled)
                PreviewDeepCapsuleReorder(owner, targetIndex);

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
            if (vacatesSource)
            {
                top -= pitch;
                widths.Add(NativeDropGhostMasterWidth(sourceKey, geometry));
            }
            if (!vacatesSource || !foldedTarget)
                widths.Add(NativeDropGhostMemberWidth(owner.Id, geometry));
        }

        // A lifted last member leaves no real source queue, even while its
        // landing remains on the same edge. Keep only the projected stack.
        if (vacatesSource && !EdgeCapsuleFreeQueuesEnabled)
            PreviewVacatedLegacyMaster(owner);
        _nativeQueueDropGhost ??= new EdgeCapsuleQueueDropGhost();
        _singleCapsuleGhostOwnerId = owner.Id;
        var dragWindow = _windows[owner.Id];
        var raiseDrag = dragWindow.BringEdgeCapsuleDragAboveGhost;
        if (foldedTarget && floating && targetKey != sourceKey &&
            _masterCapsules.TryGetValue(targetKey, out var foldedMaster))
            _nativeQueueDropGhost.ShowMergeHaloAt(geometry, side, foldedMaster.AnchorTop,
                foldedMaster.DesiredDockedWidthFor(geometry),
                raiseDrag, dragWindow.EdgeCapsuleDragIsTopmost);
        else
            _nativeQueueDropGhost.ShowAt(geometry, side, top, widths,
                DeepCapsuleGap, raiseDrag, dragWindow.EdgeCapsuleDragIsTopmost);
    }

    internal void EndSingleCapsuleDropGhost(string ownerId)
    {
        if (_singleCapsuleGhostOwnerId != ownerId) return;
        _singleCapsuleGhostOwnerId = null;
        // Committed empty queues have already retired their master; canceled
        // drags still own their original source and restore its visible master.
        RestoreVacatedLegacyMaster(ownerId);
        _legacyLandingPreview = null;
        if (_nativeEdgeQueueDrag != null)
            _nativeQueueDropGhost?.Hide();
        else
        {
            _nativeQueueDropGhost?.Dispose();
            _nativeQueueDropGhost = null;
        }
    }

    // Legacy master transfer is floating only: its anchored queue already owns
    // all the visible pixels before unlocking from the wall.
    private void PreviewLegacyMasterTransferDropGhost(
        MasterCapsuleQueueTransferSnapshot snapshot, DeviceScreenPoint pointer)
    {
        if (_masterCapsuleQueueTransfer != snapshot ||
            !State.UseCapsuleMode || !State.UseDeepCapsuleMode ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry) ||
            !MasterCapsuleQueueTransferPolicy.TryResolveTarget(pointer,
                snapshot.SourceMonitorDeviceName, snapshot.SourceEdge,
                snapshot.PaperIds.Length + 1, DeepCapsuleGap, out var initialTarget))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }

        var sourceIds = snapshot.PaperIds.ToHashSet(StringComparer.Ordinal);
        string Side(EdgeCapsuleEdge edge) => edge == EdgeCapsuleEdge.Left
            ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var initialKey = EdgeKey(initialTarget.MonitorDeviceName, Side(initialTarget.Edge));
        var others = DeepCapsulePapersInOrder()
            .Where(p => !sourceIds.Contains(p.Id) && QueueKey(p) == initialKey).ToList();
        if (!MasterCapsuleQueueTransferPolicy.TryResolveTarget(pointer,
                snapshot.SourceMonitorDeviceName, snapshot.SourceEdge,
                snapshot.PaperIds.Length + others.Count + 1, DeepCapsuleGap, out var target))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }

        var side = Side(target.Edge);
        var targetKey = EdgeKey(target.MonitorDeviceName, side);
        var sourceMaster = _masterCapsules.GetValueOrDefault(snapshot.SourceQueueKey);
        Action? raiseDrag = sourceMaster is { } draggingMaster
            ? draggingMaster.BringDragWindowAboveGhost : null;
        var topmost = sourceMaster?.DragWindowIsTopmost ?? true;
        _nativeQueueDropGhost ??= new EdgeCapsuleQueueDropGhost();
        if (others.Count > 0 && IsCapsuleCollapseAllActiveForQueue(targetKey) &&
            _masterCapsules.TryGetValue(targetKey, out var foldedMaster))
        {
            _nativeQueueDropGhost.ShowMergeHaloAt(geometry, side,
                target.StartTopMargin, foldedMaster.DesiredDockedWidthFor(geometry),
                raiseDrag, topmost);
            return;
        }

        var hasDestinationMaster = others.Count > 0;
        var widths = new List<double>(snapshot.PaperIds.Length + 1);
        if (!hasDestinationMaster)
            widths.Add(NativeDropGhostMasterWidth(snapshot.SourceQueueKey, geometry));
        if (hasDestinationMaster || !snapshot.SourceWasCollapsed)
            foreach (var id in snapshot.PaperIds)
                widths.Add(NativeDropGhostMemberWidth(id, geometry));

        var visualIndex = hasDestinationMaster ? others.Count + 1 : 0;
        var totalSlots = snapshot.PaperIds.Length + others.Count + 1;
        var top = EdgeCapsuleLayout.TopForIndex(visualIndex, target.StartTopMargin,
            geometry.LocalWorkAreaDip, totalSlots, DeepCapsuleGap);
        _nativeQueueDropGhost.ShowAt(geometry, side, top, widths, DeepCapsuleGap,
            raiseDrag, topmost);
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
