namespace PaperTodo;

public sealed partial class AppController
{
    internal bool EdgeCapsuleFreeQueuesEnabled =>
        State.UseCapsuleFreeQueues && State.UseCapsuleMode && State.UseDeepCapsuleMode;

    private static string EdgeKey(string monitor, string side) => QueueKey(monitor, side);

    private static string InstanceQueueKey(PaperData paper) => EdgeCapsuleQueueIds.Compose(
        $"{WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName)}|{DeepCapsuleSides.Normalize(paper.CapsuleSide)}",
        paper.CapsuleQueueId);

    private static string LiveEdgeOfQueueKey(string key) =>
        EdgeCapsuleQueueIds.TryGetEdge(key, out var monitor, out var side) ? EdgeKey(monitor, side) : "";

    internal string QueueKeyForPaper(PaperData paper) => QueueKey(paper);

    private bool QueueHasMaster(string key) => State.UseCapsuleCollapseAll;

    private EdgeCapsuleQueuePlan BuildEdgeQueuePlan(IEnumerable<EdgeCapsuleQueueMember> members, bool showMaster) =>
        EdgeCapsuleQueueCoordinator.Build(members, showMaster);

    private double DeepCapsuleStartTopMarginForQueueKey(string key)
    {
        if (_freeMasterDragPreview is { } preview && preview.Tops.TryGetValue(key, out var previewTop)) return previewTop;
        if (State.DeepCapsuleQueueStartTopMargins.TryGetValue(key, out var margin)) return margin;
        EdgeCapsuleQueueIds.TrySplit(key, out _, out var id);
        return id.Length == 0 && State.DeepCapsuleQueueStartTopMargins.TryGetValue(LiveEdgeOfQueueKey(key), out margin)
            ? margin : EdgeCapsuleLayout.StartTopMargin;
    }

    private EdgeCapsuleQueuePlan BuildRestingEdgeQueuePlan() => ApplyFreeQueueRestingLayout(
        BuildEdgeQueuePlan(DeepCapsulePapersInOrder().Select(p =>
            new EdgeCapsuleQueueMember(p, QueueKey(p))), State.UseCapsuleCollapseAll), includePreview: false);

    // Passive spacing adapts #354's live-edge grouping, without turning it into a drop-position
    // search. The same layout function also consumes a prospective membership/anchor set for
    // drag preview. Only explicit drop changes are persisted; hover/display offsets are not.
    private EdgeCapsuleQueuePlan ApplyFreeQueueRestingLayout(EdgeCapsuleQueuePlan plan,
        IReadOnlyDictionary<string, double>? anchors = null, bool includePreview = true)
    {
        if (!EdgeCapsuleFreeQueuesEnabled) return ApplyQueueAnchorOverrides(plan, anchors);
        var gap = DeepCapsuleGap;
        var pitch = EdgeCapsuleLayout.SlotHeight(gap);
        var connected = WindowWorkAreaHelper.ConnectedMonitorGeometries()
            .Select(m => WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(m.DeviceName))
            .ToHashSet(StringComparer.Ordinal);
        var queues = new List<EdgeCapsuleRestingQueue>();
        // Every independent queue owns one master and its own anchor.
        var masterBases = new Dictionary<string, double>(StringComparer.Ordinal);
        var anchorDeltas = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var queue in plan.Queues)
        {
            var liveEdge = LiveEdgeOfQueueKey(queue.Key);
            if (queue.Papers.Count == 0 || !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry)) continue;
            var collapsed = queue.HasMaster && IsCapsuleCollapseAllActiveForQueue(queue.Key);
            var savedMargin = DeepCapsuleStartTopMarginForQueueKey(queue.Key);
            var margin = anchors != null && anchors.TryGetValue(queue.Key, out var requested) ? requested : savedMargin;
            var restTop = EdgeCapsuleLayout.FreeQueueTopForIndex(0, margin, geometry.LocalWorkAreaDip, gap);
            var anchorDelta = restTop - EdgeCapsuleLayout.FreeQueueTopForIndex(0, savedMargin, geometry.LocalWorkAreaDip, gap);
            var top = queue.HasMaster ? restTop : double.PositiveInfinity;
            var bottom = queue.HasMaster ? restTop + PaperLayoutDefaults.CapsuleHeight : double.NegativeInfinity;
            if (queue.HasMaster) masterBases[queue.Key] = restTop;
            var members = new List<EdgeCapsuleRestingQueueMember>();
            foreach (var paper in queue.Papers)
            {
                var placement = plan.Placements[paper.Id];
                var memberTop = restTop + placement.VisualIndex * pitch + placement.TopOffsetDip;
                var height = includePreview && anchors == null && _edgeCapsulePreviewSession is { } preview && preview.OwnerPaperId == paper.Id
                    ? Math.Max(preview.Size.HeightDip, PaperLayoutDefaults.CapsuleHeight) : PaperLayoutDefaults.CapsuleHeight;
                members.Add(new(paper.Id, memberTop));
                anchorDeltas[paper.Id] = anchorDelta;
                // A collapsed queue occupies only its visible master: hidden member
                // slots must not reserve space or act as collision obstacles.
                if (!collapsed)
                {
                    top = Math.Min(top, memberTop);
                    bottom = Math.Max(bottom, memberTop + height);
                }
            }
            queues.Add(new(liveEdge, queue.Key, members, top, bottom, connected.Contains(monitor) ? 0 : 1, restTop));
        }
        // The master currently being dragged is not a passive collision obstacle.
        // Otherwise the resting-layout solver pushes it back BELOW the master the
        // user is trying to drag onto, making a vertical merge impossible.
        var draggedMasterKey = _freeMasterDragPreview?.Layout.QueueKey;
        var offsets = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(
            draggedMasterKey == null ? queues : queues.Where(q => q.QueueKey != draggedMasterKey).ToList(),
            gap);
        var masterTops = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var queue in queues)
            if (masterBases.TryGetValue(queue.QueueKey, out var masterBase))
                masterTops[queue.QueueKey] = masterBase + offsets.GetValueOrDefault(queue.Members[0].PaperId);
        var placements = plan.Placements.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (id, placement) in plan.Placements)
            placements[id] = placement with
            {
                TopOffsetDip = placement.TopOffsetDip + offsets.GetValueOrDefault(id) + anchorDeltas.GetValueOrDefault(id)
            };
        return new EdgeCapsuleQueuePlan(plan.Queues, placements, masterTops);
    }

    private Dictionary<string, double> BuildFreeQueueDropAnchors(
        IReadOnlyList<EdgeFreeQueueBox> boxes, EdgeFreeQueueDrop decision,
        string destination, int carriedCount, bool hasMaster, bool collapsed = false)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var queues = new List<EdgeQueueAnchor>();
        foreach (var box in boxes)
        {
            var target = decision.Kind == EdgeFreeQueueDropKind.Insert && box.Key == destination;
            var top = decision.Tops.TryGetValue(box.Key, out var moved) ? moved : box.TopDip;
            var saved = DeepCapsuleStartTopMarginForQueueKey(box.Key);
            // Upward movement cannot be reproduced by downward spacing. Apply
            // that displacement to the preference, not to an already displaced
            // screen top. Downstream passive displacement is never copied back.
            var preferred = saved + (target || top < box.TopDip ? top - box.TopDip : 0);
            var finalBox = target ? box with { MemberCount = box.MemberCount + carriedCount } : box;
            queues.Add(new(box.Key, top, finalBox.Height(pitch, DeepCapsuleGap), preferred));
        }
        if (decision.Kind == EdgeFreeQueueDropKind.Move)
        {
            var placed = new EdgeFreeQueueBox(destination, decision.TopDip,
                carriedCount, hasMaster, 0, collapsed);
            queues.Add(new(destination, decision.TopDip,
                placed.Height(pitch, DeepCapsuleGap), decision.TopDip));
        }

        var resolved = EdgeCapsuleQueueAnchorPlan.Build(queues, DeepCapsuleGap);
        // Complete geometry participates above, including queues that did not
        // move. Only changed preferences (and the explicit destination) persist.
        return resolved.Where(pair => pair.Key == destination ||
                Math.Abs(pair.Value - DeepCapsuleStartTopMarginForQueueKey(pair.Key)) > .01)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private EdgeCapsuleQueuePlan ApplyQueueAnchorOverrides(EdgeCapsuleQueuePlan plan, IReadOnlyDictionary<string, double>? anchors)
    {
        if (anchors == null) return plan;
        var placements = plan.Placements.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var masters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var queue in plan.Queues)
        {
            if (!anchors.TryGetValue(queue.Key, out var top) ||
                !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _)) continue;
            var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(monitor);
            var count = queue.Papers.Count + (queue.HasMaster ? 1 : 0);
            var saved = EdgeCapsuleLayout.TopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(queue.Key), area, count, DeepCapsuleGap);
            var target = EdgeCapsuleLayout.TopForIndex(0, top, area, count, DeepCapsuleGap);
            foreach (var paper in queue.Papers)
                placements[paper.Id] = placements[paper.Id] with { TopOffsetDip = target - saved };
            if (queue.HasMaster) masters[queue.Key] = target;
        }
        return new EdgeCapsuleQueuePlan(plan.Queues, placements, masters);
    }

    private IReadOnlyDictionary<string, double> BuildEffectiveQueueRestingDeltas()
    {
        var plan = BuildRestingEdgeQueuePlan();
        return plan.Queues.Where(q => q.Papers.Count > 0).ToDictionary(q => q.Key,
            q => plan.Placements[q.Papers[0].Id].TopOffsetDip, StringComparer.Ordinal);
    }

    private sealed record NativeQueueGroup(string Key, List<PaperData> Papers, EdgeFreeQueueBox Box, MonitorGeometry Geometry);

    private List<NativeQueueGroup> NativeQueueGroups(EdgeCapsuleQueuePlan? plan = null, bool measureWidths = true)
    {
        plan ??= BuildRestingEdgeQueuePlan();
        var groups = new List<NativeQueueGroup>();
        foreach (var queue in plan.Queues)
        {
            if (queue.Papers.Count == 0 || !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry)) continue;
            var placement = plan.Placements[queue.Papers[0].Id];
            var top = EdgeCapsuleFreeQueuesEnabled
                ? EdgeCapsuleLayout.FreeQueueTopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(queue.Key),
                    geometry.LocalWorkAreaDip, DeepCapsuleGap) + placement.TopOffsetDip
                : EdgeCapsuleLayout.TopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(queue.Key),
                    geometry.LocalWorkAreaDip, placement.SlotCount, DeepCapsuleGap) + placement.TopOffsetDip;
            var collapsed = queue.HasMaster && IsCapsuleCollapseAllActiveForQueue(queue.Key);
            // Floating hit testing uses monitor half and vertical slots. Only a
            // docked pointer needs the widths of the target queues.
            var width = 0.0;
            if (measureWidths)
            {
                width = collapsed
                    ? _masterCapsules.TryGetValue(queue.Key, out var master)
                        ? master.DesiredDockedWidth : PaperLayoutDefaults.CapsuleWidth
                    : queue.Papers.Max(p => _windows.TryGetValue(p.Id, out var w)
                        ? w.DeepCapsuleRestingVisibleWidth : 0);
            }
            groups.Add(new(queue.Key, queue.Papers.ToList(),
                new(queue.Key, top, queue.Papers.Count, queue.HasMaster, width, collapsed), geometry));
        }
        return groups;
    }

    private void ToggleCapsuleFreeQueues() => SetSettingFromUi("capsule.free_queues", !State.UseCapsuleFreeQueues);
}

// The drop policy has already chosen these screen positions. This is only the
// conversion back to saved preferences, not another collision/drop solver.
internal readonly record struct EdgeQueueAnchor(
    string Key, double TopDip, double HeightDip, double PreferredTopDip);

internal static class EdgeCapsuleQueueAnchorPlan
{
    internal static IReadOnlyDictionary<string, double> Build(
        IReadOnlyList<EdgeQueueAnchor> queues, double gap)
    {
        var anchors = new Dictionary<string, double>(StringComparer.Ordinal);
        double? previousBottom = null;
        double? previousAnchor = null;
        foreach (var queue in queues.OrderBy(q => q.TopDip)
                     .ThenBy(q => q.Key, StringComparer.Ordinal))
        {
            var top = Math.Round(queue.TopDip, 1);
            // A gap needs an explicit anchor. A touching queue can keep its
            // preference: the ordinary downward-spacing pass recreates its top.
            var anchor = previousBottom == null ||
                top > Math.Round(previousBottom.Value + gap, 1) + .01
                    ? top : Math.Min(Math.Round(queue.PreferredTopDip, 1), top);
            // Saved anchors also determine queue order. Keep the chosen spatial
            // order when an explicit move crosses another queue's old preference.
            // One existing 0.1-DIP quantum avoids dependence on key tie-breaking.
            if (previousAnchor is { } previous)
                anchor = Math.Max(anchor, Math.Round(previous + .1, 1));
            anchors[queue.Key] = anchor;
            previousAnchor = anchor;
            previousBottom = top + queue.HeightDip;
        }
        return anchors;
    }
}
