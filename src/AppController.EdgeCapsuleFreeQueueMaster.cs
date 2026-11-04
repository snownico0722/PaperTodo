namespace PaperTodo;

internal sealed record FreeMasterQueueGroup(string Key, string[] PaperIds, double TopDip, bool HasMaster);
internal sealed record FreeMasterDragLayout(string EdgeKey, string QueueKey, string Monitor, string Side, double MasterTopDip,
    IReadOnlyList<FreeMasterQueueGroup> Groups, IReadOnlyDictionary<string, string> MemberKeys);

public sealed partial class AppController
{
    private (FreeMasterDragLayout Layout, IReadOnlyDictionary<string, double> Tops)? _freeMasterDragPreview;

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

    internal void PreviewFreeMasterQueueTop(FreeMasterDragLayout layout, double requestedTop)
    {
        if (!EdgeCapsuleFreeQueuesEnabled || !FreeMasterMembersMatch(layout)) return;
        if (!double.IsFinite(requestedTop)) return;
        // Free queues keep the requested anchor even if their tail extends past the work area.
        _freeMasterDragPreview = (layout,
            new Dictionary<string, double>(StringComparer.Ordinal) { [layout.QueueKey] = requestedTop });
        ArrangeDeepCapsules(animate: false);
    }

    internal void FinishFreeMasterQueueDrag(FreeMasterDragLayout layout, bool commit)
    {
        if (_freeMasterDragPreview is not { } preview || !ReferenceEquals(preview.Layout, layout)) return;
        _freeMasterDragPreview = null;
        if (commit && EdgeCapsuleFreeQueuesEnabled && FreeMasterMembersMatch(layout))
        {
            State.DeepCapsuleQueueStartTopMargins[layout.QueueKey] = preview.Tops[layout.QueueKey];
            MarkDirty();
        }
        ArrangeDeepCapsules(animate: !commit);
    }

    private bool FreeMasterMembersMatch(FreeMasterDragLayout layout)
    {
        var papers = DeepCapsulePapersInOrder().Where(p => QueueKey(p) == layout.QueueKey).ToList();
        return layout.Groups.Count == 1 && papers.Count == layout.MemberKeys.Count && papers.All(p =>
            layout.MemberKeys.TryGetValue(p.Id, out var key) && InstanceQueueKey(p) == key);
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
        var wallDistance = (side == DeepCapsuleSides.Left
            ? pointer.X - geometry.WorkArea.Left : geometry.WorkArea.Right - pointer.X) / geometry.DpiScaleX;
        // A master remains a valid merge target even when its queue is collapsed.
        var target = EdgeCapsuleFreeQueuePolicy.FindInsertion(
            boxes, requestedTop + pitch, count * pitch - gap, wallDistance, pitch, gap,
            pointerTopDip: geometry.DeviceYToLocalDip(pointer.Y));
        var decision = target is { } insert
            ? EdgeCapsuleFreeQueuePolicy.Insert(boxes, insert.Key, insert.Index, count,
                geometry.LocalWorkAreaDip.Height, pitch, gap)
            : EdgeCapsuleFreeQueuePolicy.Place(boxes, requestedTop, count + 1,
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
        var oldOrder = State.Papers.ToList();
        var oldMargins = new Dictionary<string, double>(State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var oldCollapsed = new Dictionary<string, bool>(State.CapsuleCollapseAllActiveQueues, StringComparer.Ordinal);
        var oldIdentities = carried.Select(p => (Paper: p, Monitor: p.CapsuleMonitorDeviceName,
            Side: p.CapsuleSide, Id: p.CapsuleQueueId)).ToArray();
        try
        {
            EdgeCapsuleQueueIds.TrySplit(destination, out _, out var queueId);
            foreach (var paper in carried)
            {
                paper.CapsuleMonitorDeviceName = monitor;
                paper.CapsuleSide = side;
                paper.CapsuleQueueId = queueId;
            }
            if (target is { } t)
                InsertNativeQueueBlock(carried, targetMembers, t.Index);

            // A simple master move changes only its own preference. Neighbours may
            // yield visually, but passive spacing must not become permanent layout memory.
            if (decision.Kind == EdgeFreeQueueDropKind.Insert)
                foreach (var (key, top) in decision.Tops)
                    State.DeepCapsuleQueueStartTopMargins[key] = top;
            State.DeepCapsuleQueueStartTopMargins[destination] = decision.TopDip;
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
