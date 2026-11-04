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

    private bool QueueHasMaster(string key) => State.UseCapsuleCollapseAll &&
        (!State.UseCapsuleFreeQueues || key == LiveEdgeOfQueueKey(key));

    private EdgeCapsuleQueuePlan BuildEdgeQueuePlan(IEnumerable<EdgeCapsuleQueueMember> members, bool showMaster) =>
        EdgeCapsuleQueueCoordinator.Build(members, showMaster,
            State.UseCapsuleFreeQueues ? key => key == LiveEdgeOfQueueKey(key) : null);

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
        var edgesWithMaster = new HashSet<string>(StringComparer.Ordinal);
        var liveEdges = new HashSet<string>(StringComparer.Ordinal);
        var masterBases = new Dictionary<string, double>(StringComparer.Ordinal);
        var anchorDeltas = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var queue in plan.Queues)
        {
            var liveEdge = LiveEdgeOfQueueKey(queue.Key);
            liveEdges.Add(liveEdge);
            if (queue.Papers.Count == 0 || !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry)) continue;
            var slots = queue.Papers.Count + (queue.HasMaster ? 1 : 0);
            var savedMargin = DeepCapsuleStartTopMarginForQueueKey(queue.Key);
            var margin = anchors != null && anchors.TryGetValue(queue.Key, out var requested) ? requested : savedMargin;
            var restTop = EdgeCapsuleLayout.FreeQueueTopForIndex(0, margin, geometry.LocalWorkAreaDip, gap);
            var anchorDelta = restTop - EdgeCapsuleLayout.FreeQueueTopForIndex(0, savedMargin, geometry.LocalWorkAreaDip, gap);
            var top = queue.HasMaster ? restTop : double.PositiveInfinity;
            var bottom = queue.HasMaster ? restTop + PaperLayoutDefaults.CapsuleHeight : double.NegativeInfinity;
            if (queue.HasMaster) { edgesWithMaster.Add(liveEdge); masterBases[liveEdge] = restTop; }
            var members = new List<EdgeCapsuleRestingQueueMember>();
            foreach (var paper in queue.Papers)
            {
                var placement = plan.Placements[paper.Id];
                var memberTop = restTop + placement.VisualIndex * pitch + placement.TopOffsetDip;
                var height = includePreview && anchors == null && _edgeCapsulePreviewSession is { } preview && preview.OwnerPaperId == paper.Id
                    ? Math.Max(preview.Size.HeightDip, PaperLayoutDefaults.CapsuleHeight) : PaperLayoutDefaults.CapsuleHeight;
                members.Add(new(paper.Id, memberTop));
                anchorDeltas[paper.Id] = anchorDelta;
                top = Math.Min(top, memberTop);
                bottom = Math.Max(bottom, memberTop + height);
            }
            queues.Add(new(liveEdge, queue.Key, members, top, bottom, connected.Contains(monitor) ? 0 : 1, restTop));
        }
        // The collection control still exists when no members remain in the default queue.
        foreach (var edge in liveEdges.Where(e => State.UseCapsuleCollapseAll && !edgesWithMaster.Contains(e)))
        {
            if (!EdgeCapsuleQueueIds.TryGetEdge(edge, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry)) continue;
            var margin = anchors != null && anchors.TryGetValue(edge, out var requested) ? requested : DeepCapsuleStartTopMarginForQueueKey(edge);
            var top = EdgeCapsuleLayout.FreeQueueTopForIndex(0, margin, geometry.LocalWorkAreaDip, gap);
            masterBases[edge] = top;
            queues.Add(new(edge, edge, [new("master:" + edge, top)], top, top + PaperLayoutDefaults.CapsuleHeight, -1, top));
        }
        var offsets = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, gap);
        var masterTops = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var queue in queues.Where(q => q.QueueKey == q.LiveEdgeKey))
            if (masterBases.TryGetValue(queue.LiveEdgeKey, out var masterBase))
                masterTops[queue.LiveEdgeKey] = masterBase + offsets.GetValueOrDefault(queue.Members[0].PaperId);
        var placements = plan.Placements.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (id, placement) in plan.Placements)
            placements[id] = placement with
            {
                TopOffsetDip = placement.TopOffsetDip + offsets.GetValueOrDefault(id) + anchorDeltas.GetValueOrDefault(id)
            };
        return new EdgeCapsuleQueuePlan(plan.Queues, placements, masterTops);
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

    private List<NativeQueueGroup> NativeQueueGroups()
    {
        var plan = BuildRestingEdgeQueuePlan();
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
            var width = queue.Papers.Max(p => _windows.TryGetValue(p.Id, out var w) ? w.DeepCapsuleRestingVisibleWidth : 0);
            groups.Add(new(queue.Key, queue.Papers.ToList(), new(queue.Key, top, queue.Papers.Count, queue.HasMaster, width), geometry));
        }
        return groups;
    }

    private void ToggleCapsuleFreeQueues() => SetSettingFromUi("capsule.free_queues", !State.UseCapsuleFreeQueues);
}
