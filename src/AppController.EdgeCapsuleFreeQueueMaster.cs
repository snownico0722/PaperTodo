namespace PaperTodo;

internal sealed record FreeMasterQueueGroup(string Key, string[] PaperIds, double TopDip, bool HasMaster);
internal sealed record FreeMasterDragLayout(string EdgeKey, string QueueKey, string Monitor, string Side, double MasterTopDip,
    IReadOnlyList<FreeMasterQueueGroup> Groups, IReadOnlyDictionary<string, string> MemberKeys);

public sealed partial class AppController
{
    private (FreeMasterDragLayout Layout, IReadOnlyDictionary<string, double> Tops)? _freeMasterDragPreview;
    private FreeQueueSlotPreviewState _freeMasterSlotPreview = new();

    internal FreeMasterDragLayout? CaptureFreeMasterDragLayout(string monitor, EdgeCapsuleEdge edge,
        string? sourceQueueKey = null)
    {
        if (!EdgeCapsuleFreeQueuesEnabled) return null;
        var side = edge == EdgeCapsuleEdge.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var edgeKey = EdgeKey(monitor, side);
        var queueKey = sourceQueueKey ?? edgeKey;
        if (LiveEdgeOfQueueKey(queueKey) != edgeKey) return null;
        var group = NativeQueueGroups().FirstOrDefault(g => g.Key == queueKey);
        if (group == null) return null;
        var ids = group.Papers.Select(p => p.Id).ToArray();
        return new(edgeKey, queueKey, monitor, side, group.Box.TopDip,
            [new FreeMasterQueueGroup(queueKey, ids, group.Box.TopDip, group.Box.HasMaster)],
            group.Papers.ToDictionary(p => p.Id, InstanceQueueKey, StringComparer.Ordinal));
    }

    internal void PreviewFreeMasterQueueTop(FreeMasterDragLayout layout, double requestedTop,
        DeviceScreenPoint? pointer = null)
    {
        if (!EdgeCapsuleFreeQueuesEnabled || !FreeMasterMembersMatch(layout)) return;
        if (!double.IsFinite(requestedTop)) return;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(layout.Monitor, out var geometry))
            return;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var visibleHeight = IsCapsuleCollapseAllActiveForQueue(layout.QueueKey)
            ? PaperLayoutDefaults.CapsuleHeight
            : (layout.Groups[0].PaperIds.Length + (layout.Groups[0].HasMaster ? 1 : 0)) * pitch - DeepCapsuleGap;
        var boundedTop = EdgeCapsuleFreeQueuePolicy.ClampVisibleTop(
            requestedTop, geometry.LocalWorkAreaDip.Height, visibleHeight);
        var anchorChanged = _freeMasterDragPreview is not { } previous ||
            !ReferenceEquals(previous.Layout, layout) || previous.Tops[layout.QueueKey] != boundedTop;
        _freeMasterDragPreview = (layout,
            new Dictionary<string, double>(StringComparer.Ordinal) { [layout.QueueKey] = boundedTop });
        var resting = BuildRestingEdgeQueuePlan();
        var groups = NativeQueueGroups(resting);
        if (pointer is { } screenPoint)
            UpdateFreeMasterDropPreview(layout, screenPoint, docked: true, resting, groups);
        else
            PreviewFreeQueueDropSlot(_freeMasterSlotPreview, null,
                layout.Groups[0].PaperIds, resting, groups, includeResting: true);

        // Peers have already received their final resting/merge placement once.
        // Only the carried queue needs a new anchor measurement while following
        // the pointer; its unchanged slot indices must not suppress that refresh.
        if (anchorChanged)
            foreach (var id in layout.Groups[0].PaperIds)
                if (_windows.TryGetValue(id, out var window) &&
                    resting.Placements.TryGetValue(id, out var placement))
                    window.PreviewDeepCapsulePlacement(placement, animate: false, anchorChanged: true);
    }

    internal void FinishFreeMasterQueueDrag(FreeMasterDragLayout layout, bool commit)
    {
        if (_freeMasterDragPreview is not { } preview || !ReferenceEquals(preview.Layout, layout)) return;
        if (commit && EdgeCapsuleFreeQueuesEnabled && FreeMasterMembersMatch(layout))
        {
            var boxes = NativeQueueGroups()
                .Where(g => g.Key != layout.QueueKey && LiveEdgeOfQueueKey(g.Key) == layout.EdgeKey)
                .Select(g => g.Box).ToList();
            var collapsed = IsCapsuleCollapseAllActiveForQueue(layout.QueueKey);
            var count = layout.Groups[0].PaperIds.Length;
            var decision = EdgeCapsuleFreeQueuePolicy.Place(boxes, preview.Tops[layout.QueueKey],
                collapsed ? 1 : count + 1, EdgeCapsuleLayout.LocalWorkAreaForQueue(layout.Monitor).Height,
                EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap), DeepCapsuleGap);
            foreach (var (key, top) in BuildFreeQueueDropAnchors(
                         boxes, decision, layout.QueueKey, count, hasMaster: true, collapsed: collapsed))
                State.DeepCapsuleQueueStartTopMargins[key] = top;
            MarkDirty();
        }
        _freeMasterDragPreview = null;
        EndFreeMasterQueueDropGhost();
        ArrangeDeepCapsules(animate: true);
    }

    private bool FreeMasterMembersMatch(FreeMasterDragLayout layout)
    {
        var papers = DeepCapsulePapersInOrder().Where(p => QueueKey(p) == layout.QueueKey).ToList();
        return layout.Groups.Count == 1 && papers.Count == layout.MemberKeys.Count && papers.All(p =>
            layout.MemberKeys.TryGetValue(p.Id, out var key) && InstanceQueueKey(p) == key);
    }

    // The visible master itself is a merge target even when its queue is expanded.
    // The ordinary insertion selector deliberately starts at the first paper, not
    // at the master: direct master-to-master joins must check that master first.
    private static (string Key, int Index)? FindFreeMasterTarget(
        IReadOnlyList<EdgeFreeQueueBox> boxes, double topDip, double pointerTopDip,
        double pitch, double gap)
    {
        EdgeFreeQueueBox? closestMaster = null;
        var shortestDistance = double.PositiveInfinity;
        foreach (var box in boxes)
        {
            if (!box.HasMaster) continue;
            var distance = Math.Max(0, Math.Max(box.TopDip - pointerTopDip,
                pointerTopDip - box.TopDip - PaperLayoutDefaults.CapsuleHeight));
            if (distance > EdgeCapsuleFreeQueuePolicy.SnapDistance ||
                distance >= shortestDistance) continue;
            closestMaster = box;
            shortestDistance = distance;
        }
        if (closestMaster is { } target)
            return (target.Key, target.MemberCount); // Join the visible master, append its members.

        return EdgeCapsuleFreeQueuePolicy.FindInsertion(boxes, topDip,
            PaperLayoutDefaults.CapsuleHeight, 0, pitch, gap,
            pointerTopDip: pointerTopDip, floating: true);
    }

    private bool TryGetFreeMasterDrop(FreeMasterDragLayout layout, DeviceScreenPoint pointer,
        bool docked, out MonitorGeometry geometry, out string side,
        out EdgeFreeQueueBox targetBox, out EdgeFreeQueueDrop decision,
        IReadOnlyList<NativeQueueGroup>? groups = null)
    {
        geometry = default;
        side = "";
        targetBox = default;
        decision = EdgeFreeQueueDrop.Rejected;
        if (!EdgeCapsuleFreeQueuesEnabled || !FreeMasterMembersMatch(layout) ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out geometry))
            return false;

        var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
        side = pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
            ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var edge = EdgeKey(monitor, side);
        if (docked && edge != layout.EdgeKey) return false;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var boxes = (groups ?? NativeQueueGroups())
            .Where(g => g.Key != layout.QueueKey && LiveEdgeOfQueueKey(g.Key) == edge)
            .Select(g => g.Box).ToList();
        var pointerTop = geometry.DeviceYToLocalDip(pointer.Y);
        var target = FindFreeMasterTarget(boxes,
            pointerTop - PaperLayoutDefaults.CapsuleHeight / 2, pointerTop,
            pitch, DeepCapsuleGap);
        var count = layout.Groups[0].PaperIds.Length;
        // Floating transfer temporarily retracts every source queue. Reserve
        // its eventual landing footprint, not that transient one-master shell.
        var collapsed = _masterCapsuleQueueTransfer is { } transfer && transfer.FreeLayout == layout
            ? transfer.SourceWasCollapsed : IsCapsuleCollapseAllActiveForQueue(layout.QueueKey);
        // Docked movement preserves the point where the master was grabbed;
        // the floating shell is centred on the pointer, as on final release.
        var requestedTop = docked && _freeMasterDragPreview is { } preview && preview.Layout == layout
            ? preview.Tops[layout.QueueKey] : pointerTop - PaperLayoutDefaults.CapsuleHeight / 2;
        decision = target is { } insert
            ? EdgeCapsuleFreeQueuePolicy.Insert(boxes, insert.Key, insert.Index, count,
                geometry.LocalWorkAreaDip.Height, pitch, DeepCapsuleGap)
            : EdgeCapsuleFreeQueuePolicy.Place(boxes, requestedTop, collapsed ? 1 : count + 1,
                geometry.LocalWorkAreaDip.Height, pitch, DeepCapsuleGap);
        if (!decision.Accepted) return false;
        targetBox = target is { } selected ? boxes.First(b => b.Key == selected.Key)
            : new(layout.QueueKey, decision.TopDip, count, true,
                _masterCapsules.TryGetValue(layout.QueueKey, out var master)
                    ? master.DesiredDockedWidth : PaperLayoutDefaults.CapsuleWidth, collapsed);
        return true;
    }

    private void UpdateFreeMasterDropPreview(FreeMasterDragLayout layout,
        DeviceScreenPoint pointer, bool docked, EdgeCapsuleQueuePlan? resting = null,
        IReadOnlyList<NativeQueueGroup>? groups = null)
    {
        resting ??= BuildRestingEdgeQueuePlan();
        groups ??= NativeQueueGroups(resting);
        if (!TryGetFreeMasterDrop(layout, pointer, docked, out var geometry,
                out var side, out var targetBox, out var decision, groups))
        {
            PreviewFreeQueueDropSlot(_freeMasterSlotPreview, null,
                layout.Groups[0].PaperIds, resting, groups, includeResting: docked);
            _nativeQueueDropGhost?.Hide();
            return;
        }

        PreviewFreeQueueDropSlot(_freeMasterSlotPreview, decision,
            layout.Groups[0].PaperIds, resting, groups, includeResting: docked);
        if (docked && decision.Kind != EdgeFreeQueueDropKind.Insert)
        {
            // The real master and its members already follow the pointer.
            _nativeQueueDropGhost?.Hide();
            return;
        }
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var top = EdgeCapsuleFreeQueuePolicy.GhostSlotTop(
            decision, targetBox.HasMaster, pitch, targetBox.Collapsed);
        _nativeQueueDropGhost ??= new EdgeCapsuleQueueDropGhost();
        // The independent ghost includes its master; a merged ghost includes
        // only the members, each with the width it will have on this edge.
        var widths = new List<double>(layout.Groups[0].PaperIds.Length + 1);
        if (decision.Kind == EdgeFreeQueueDropKind.Move)
            widths.Add(_masterCapsules.TryGetValue(layout.QueueKey, out var master)
                ? master.DesiredDockedWidthFor(geometry) : targetBox.WidthDip);
        if (decision.Kind != EdgeFreeQueueDropKind.Move || !targetBox.Collapsed)
            foreach (var id in layout.Groups[0].PaperIds)
                widths.Add(NativeDropGhostMemberWidth(id, geometry));
        Action? raiseDrag = _masterCapsules.TryGetValue(layout.QueueKey, out var draggingMaster)
            ? draggingMaster.BringDragWindowAboveGhost : null;
        var topmost = draggingMaster?.DragWindowIsTopmost ?? true;
        if (decision.Kind == EdgeFreeQueueDropKind.Insert && targetBox.Collapsed &&
            _masterCapsules.TryGetValue(decision.TargetKey, out var targetMaster))
            _nativeQueueDropGhost.ShowMergeHaloAt(geometry, side, targetMaster.AnchorTop,
                targetMaster.DesiredDockedWidthFor(geometry), raiseDrag, topmost);
        else
            _nativeQueueDropGhost.ShowAt(geometry, side, top, widths, DeepCapsuleGap,
                raiseDrag, topmost);
    }

    internal void PreviewFreeMasterQueueDropGhost(
        MasterCapsuleQueueTransferSnapshot snapshot, DeviceScreenPoint pointer)
    {
        if (snapshot.FreeLayout is not { } layout)
        {
            PreviewLegacyMasterTransferDropGhost(snapshot, pointer);
            return;
        }
        if (_masterCapsuleQueueTransfer != snapshot || !EdgeCapsuleFreeQueuesEnabled ||
            !FreeMasterMembersMatch(layout))
        {
            _nativeQueueDropGhost?.Hide();
            return;
        }
        UpdateFreeMasterDropPreview(layout, pointer, docked: false);
    }

    internal void EndFreeMasterQueueDropGhost()
    {
        _nativeQueueDropGhost?.Dispose();
        _nativeQueueDropGhost = null;
        _freeMasterSlotPreview = new();
    }

    // A vertical master drag can merge without a horizontal pull or a pooled
    // floating HWND. Reuse the existing master transfer transaction on release;
    // it also owns rollback, collapse choice, and retirement of the old master.
    internal bool TryCommitDockedFreeMasterQueueMerge(FreeMasterDragLayout layout,
        DeviceScreenPoint pointer, double originalMargin, bool originalHadMargin)
    {
        if (_freeMasterDragPreview?.Layout != layout ||
            !TryGetFreeMasterDrop(layout, pointer, docked: true,
                out _, out _, out _, out var decision) ||
            decision.Kind != EdgeFreeQueueDropKind.Insert) return false;
        var edge = layout.Side == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;
        if (!TryBeginMasterCapsuleQueueTransfer(layout.Monitor, edge, originalMargin,
                out var snapshot, originalHadMargin, layout, layout.QueueKey))
            return false;
        var committed = CommitMasterCapsuleQueueTransfer(snapshot, pointer);
        EndFreeMasterQueueDropGhost();
        return committed;
    }

    private bool CommitFreeMasterQueueTransfer(MasterCapsuleQueueTransferSnapshot snapshot,
        DeviceScreenPoint pointer, out MasterCapsuleWindow? handoffTarget)
    {
        handoffTarget = null;
        var layout = snapshot.FreeLayout!;
        var live = DeepCapsulePapersInOrder();
        if (!EdgeCapsuleFreeQueuesEnabled || !FreeMasterMembersMatch(layout) ||
            !live.Where(p => BelongsToMasterTransferSource(p, snapshot))
                .Select(p => p.Id).SequenceEqual(snapshot.PaperIds) ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry))
        {
            CancelMasterCapsuleQueueTransfer(snapshot);
            return false;
        }

        var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
        var side = pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
            ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var edge = EdgeKey(monitor, side);
        var gap = DeepCapsuleGap;
        var pitch = EdgeCapsuleLayout.SlotHeight(gap);
        var group = layout.Groups[0];
        var count = group.PaperIds.Length;
        var carried = group.PaperIds.Select(id => live.First(p => p.Id == id)).ToList();
        var moving = carried.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var boxes = NativeQueueGroups()
            .Where(g => g.Key != layout.QueueKey && LiveEdgeOfQueueKey(g.Key) == edge)
            .Select(g => g.Box).ToList();
        var requestedTop = geometry.DeviceYToLocalDip(pointer.Y) - PaperLayoutDefaults.CapsuleHeight / 2;
        // Preview and commit must agree even when the master is dragged from far
        // inside the screen. Only its visible floating shell selects a target slot.
        var target = FindFreeMasterTarget(boxes, requestedTop,
            geometry.DeviceYToLocalDip(pointer.Y), pitch, gap);
        var decision = target is { } insert
            ? EdgeCapsuleFreeQueuePolicy.Insert(boxes, insert.Key, insert.Index, count,
                geometry.LocalWorkAreaDip.Height, pitch, gap)
            : EdgeCapsuleFreeQueuePolicy.Place(boxes, requestedTop,
                snapshot.SourceWasCollapsed ? 1 : count + 1,
                geometry.LocalWorkAreaDip.Height, pitch, gap);
        if (!decision.Accepted)
        {
            CancelMasterCapsuleQueueTransfer(snapshot);
            return false;
        }

        var destination = layout.QueueKey;
        if (target.HasValue) destination = target.Value.Key;
        else if (edge != layout.EdgeKey)
        {
            EdgeCapsuleQueueIds.TrySplit(layout.QueueKey, out _, out var id);
            destination = EdgeCapsuleQueueIds.Compose(edge, id);
            if (State.Papers.Any(p => !moving.Contains(p.Id) && InstanceQueueKey(p) == destination))
                destination = EdgeCapsuleQueueIds.Compose(edge, EdgeCapsuleQueueIds.New());
        }

        var targetMembers = target is { } merge
            ? NativeQueueGroups().First(g => g.Key == merge.Key).Papers.ToList() : [];
        var targetWasCollapsed = target.HasValue && IsCapsuleCollapseAllActiveForQueue(destination);
        // Both independent placement and merging reproduce the preview, even
        // when a neighbour's saved preference differs from its displayed top.
        var anchors = BuildFreeQueueDropAnchors(boxes, decision, destination, count,
            hasMaster: true, collapsed: snapshot.SourceWasCollapsed);
        var oldOrder = State.Papers.ToList();
        var oldMargins = new Dictionary<string, double>(State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var oldCollapsed = new Dictionary<string, bool>(State.CapsuleCollapseAllActiveQueues, StringComparer.Ordinal);
        var oldIdentities = carried.Select(p => (Paper: p, Monitor: p.CapsuleMonitorDeviceName,
            Side: p.CapsuleSide, Id: p.CapsuleQueueId)).ToArray();
        try
        {
            // The destination can retain a disconnected monitor identity while
            // being displayed on the primary. Copy its WHOLE identity, as the
            // ordinary capsule path does; pointer geometry is not membership.
            EdgeCapsuleQueueIds.TryGetEdge(destination, out var destinationMonitor, out var destinationSide);
            EdgeCapsuleQueueIds.TrySplit(destination, out _, out var queueId);
            foreach (var paper in carried)
            {
                paper.CapsuleMonitorDeviceName = destinationMonitor;
                paper.CapsuleSide = destinationSide;
                paper.CapsuleQueueId = queueId;
            }
            if (target is { } t)
                InsertNativeQueueBlock(carried, targetMembers, t.Index);

            foreach (var (key, top) in anchors)
                State.DeepCapsuleQueueStartTopMargins[key] = top;
            if (destination != layout.QueueKey &&
                !State.Papers.Any(p => InstanceQueueKey(p) == layout.QueueKey))
            {
                State.DeepCapsuleQueueStartTopMargins.Remove(layout.QueueKey);
                State.CapsuleCollapseAllActiveQueues.Remove(layout.QueueKey);
            }
            if (target.HasValue ? targetWasCollapsed : snapshot.SourceWasCollapsed)
                State.CapsuleCollapseAllActiveQueues[destination] = true;
            else
                State.CapsuleCollapseAllActiveQueues.Remove(destination);
        }
        catch
        {
            State.Papers = oldOrder;
            State.DeepCapsuleQueueStartTopMargins = oldMargins;
            State.CapsuleCollapseAllActiveQueues = oldCollapsed;
            foreach (var row in oldIdentities)
            {
                row.Paper.CapsuleMonitorDeviceName = row.Monitor;
                row.Paper.CapsuleSide = row.Side;
                row.Paper.CapsuleQueueId = row.Id;
            }
            throw;
        }

        _freeMasterDragPreview = null;
        CompleteMasterCapsuleQueueTransfer(snapshot, destination, out handoffTarget);
        return true;
    }
}

