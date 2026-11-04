namespace PaperTodo;

internal sealed record FreeMasterQueueGroup(string Key, string[] PaperIds, double TopDip, bool HasMaster);
internal sealed record FreeMasterDragLayout(string EdgeKey, string Monitor, string Side, double MasterTopDip,
    IReadOnlyList<FreeMasterQueueGroup> Groups, IReadOnlyDictionary<string, string> MemberKeys);

public sealed partial class AppController
{
    private (FreeMasterDragLayout Layout, IReadOnlyDictionary<string, double> Tops)? _freeMasterDragPreview;

    internal FreeMasterDragLayout? CaptureFreeMasterDragLayout(string monitor, EdgeCapsuleEdge edge)
    {
        if (!EdgeCapsuleFreeQueuesEnabled) return null;
        var side = edge == EdgeCapsuleEdge.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var key = EdgeKey(monitor, side);
        var groups = NativeQueueGroups().Where(g => LiveEdgeOfQueueKey(g.Key) == key).ToList();
        return new(key, monitor, side, FreeQueueMasterTop(monitor, side),
            groups.Select(g => new FreeMasterQueueGroup(g.Key, g.Papers.Select(p => p.Id).ToArray(), g.Box.TopDip, g.Box.HasMaster)).ToArray(),
            groups.SelectMany(g => g.Papers).ToDictionary(p => p.Id, InstanceQueueKey, StringComparer.Ordinal));
    }

    internal void PreviewFreeMasterQueueTop(FreeMasterDragLayout layout, double requestedTop)
    {
        if (!EdgeCapsuleFreeQueuesEnabled || !FreeMasterMembersMatch(layout)) return;
        var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(layout.Monitor);
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var low = Math.Min(layout.MasterTopDip, layout.Groups.Select(g => g.TopDip).DefaultIfEmpty(layout.MasterTopDip).Min());
        var high = Math.Max(layout.MasterTopDip + PaperLayoutDefaults.CapsuleHeight,
            layout.Groups.Select(g => g.TopDip + (g.PaperIds.Length + (g.HasMaster ? 1 : 0)) * pitch - DeepCapsuleGap)
                .DefaultIfEmpty(layout.MasterTopDip + PaperLayoutDefaults.CapsuleHeight).Max());
        var minDelta = EdgeCapsuleLayout.TopMargin - low;
        var maxDelta = Math.Max(minDelta, area.Height - EdgeCapsuleLayout.TopMargin - high);
        var delta = Math.Clamp(requestedTop - layout.MasterTopDip, minDelta, maxDelta);
        var tops = layout.Groups.ToDictionary(g => g.Key, g => g.TopDip + delta, StringComparer.Ordinal);
        tops[layout.EdgeKey] = layout.MasterTopDip + delta;
        _freeMasterDragPreview = (layout, tops);
        ArrangeDeepCapsules(animate: false);
    }

    internal void FinishFreeMasterQueueDrag(FreeMasterDragLayout layout, bool commit)
    {
        if (_freeMasterDragPreview is not { } preview || !ReferenceEquals(preview.Layout, layout)) return;
        _freeMasterDragPreview = null;
        if (commit && EdgeCapsuleFreeQueuesEnabled && FreeMasterMembersMatch(layout))
        {
            foreach (var (key, top) in preview.Tops) State.DeepCapsuleQueueStartTopMargins[key] = top;
            MarkDirty();
        }
        ArrangeDeepCapsules(animate: !commit);
    }

    private bool FreeMasterMembersMatch(FreeMasterDragLayout layout)
    {
        var papers = DeepCapsulePapersInOrder().Where(p => EdgeKey(p.CapsuleMonitorDeviceName, p.CapsuleSide) == layout.EdgeKey).ToList();
        return papers.Count == layout.MemberKeys.Count && papers.All(p =>
            layout.MemberKeys.TryGetValue(p.Id, out var key) && InstanceQueueKey(p) == key);
    }

    private bool CommitFreeMasterQueueTransfer(MasterCapsuleQueueTransferSnapshot snapshot,
        DeviceScreenPoint pointer, out MasterCapsuleWindow? handoffTarget)
    {
        handoffTarget = null;
        var layout = snapshot.FreeLayout!;
        var live = DeepCapsulePapersInOrder();
        var moving = snapshot.PaperIds.ToHashSet(StringComparer.Ordinal);
        if (!State.UseCapsuleFreeQueues || !live.Where(p => BelongsToMasterTransferSource(p, snapshot))
                .Select(p => p.Id).SequenceEqual(snapshot.PaperIds) ||
            live.Where(p => moving.Contains(p.Id)).Any(p => !layout.MemberKeys.TryGetValue(p.Id, out var old) || InstanceQueueKey(p) != old) ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry))
        {
            CancelMasterCapsuleQueueTransfer(snapshot);
            return false;
        }
        var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
        var side = pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0 ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        var destinationEdge = EdgeKey(monitor, side);
        var sameEdge = destinationEdge == layout.EdgeKey;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var destinationPapers = live.Where(p => !moving.Contains(p.Id) && EdgeKey(p.CapsuleMonitorDeviceName, p.CapsuleSide) == destinationEdge).ToList();
        var targetHasMembers = destinationPapers.Count > 0;
        var targetWasCollapsed = IsCapsuleCollapseAllActiveForQueue(destinationEdge);
        var groups = layout.Groups.Select(g => g with
        {
            TopDip = g.TopDip + (!sameEdge && g.HasMaster ? pitch : 0), HasMaster = sameEdge && g.HasMaster
        }).ToList();
        var low = Math.Min(layout.MasterTopDip, groups.Select(g => g.TopDip).DefaultIfEmpty(layout.MasterTopDip).Min());
        var high = Math.Max(layout.MasterTopDip + PaperLayoutDefaults.CapsuleHeight,
            groups.Select(g => g.TopDip + (g.PaperIds.Length + (g.HasMaster ? 1 : 0)) * pitch - DeepCapsuleGap)
                .DefaultIfEmpty(layout.MasterTopDip + PaperLayoutDefaults.CapsuleHeight).Max());
        var minDelta = EdgeCapsuleLayout.TopMargin - low;
        var maxDelta = Math.Max(minDelta, geometry.LocalWorkAreaDip.Height - EdgeCapsuleLayout.TopMargin - high);
        var desiredMasterTop = geometry.DeviceYToLocalDip(pointer.Y) - PaperLayoutDefaults.CapsuleHeight / 2;
        var delta = Math.Clamp(desiredMasterTop - layout.MasterTopDip, minDelta, maxDelta);
        var obstacles = NativeQueueGroups().Where(g => LiveEdgeOfQueueKey(g.Key) == destinationEdge &&
            g.Papers.Any(p => !moving.Contains(p.Id))).Select(g => g.Box).ToList();
        if (!sameEdge && targetHasMembers && !obstacles.Any(b => b.HasMaster))
            obstacles.Add(new(destinationEdge, FreeQueueMasterTop(monitor, side), 0, true, PaperLayoutDefaults.CapsuleWidth));
        foreach (var group in groups)
        {
            var requested = group.TopDip + delta;
            var result = EdgeCapsuleFreeQueuePolicy.Place(obstacles, requested,
                group.PaperIds.Length + (group.HasMaster ? 1 : 0), geometry.LocalWorkAreaDip.Height, pitch, DeepCapsuleGap);
            if (!result.Accepted || Math.Abs(result.TopDip - requested) > .1)
            {
                CancelMasterCapsuleQueueTransfer(snapshot);
                return false;
            }
        }
        var oldMargins = new Dictionary<string, double>(State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var oldCollapsed = new Dictionary<string, bool>(State.CapsuleCollapseAllActiveQueues, StringComparer.Ordinal);
        var oldIdentities = live.Where(p => moving.Contains(p.Id)).Select(p =>
            (Paper: p, Monitor: p.CapsuleMonitorDeviceName, Side: p.CapsuleSide, Id: p.CapsuleQueueId)).ToArray();
        try
        {
            foreach (var group in groups)
            {
                var papers = group.PaperIds.Select(id => live.First(p => p.Id == id)).ToArray();
                var id = papers[0].CapsuleQueueId;
                var destinationKey = EdgeCapsuleQueueIds.Compose(destinationEdge, id);
                if (!sameEdge && (id.Length == 0 || State.Papers.Any(p => !moving.Contains(p.Id) && InstanceQueueKey(p) == destinationKey)))
                {
                    id = EdgeCapsuleQueueIds.New();
                    destinationKey = EdgeCapsuleQueueIds.Compose(destinationEdge, id);
                }
                if (!sameEdge)
                    foreach (var paper in papers)
                    {
                        paper.CapsuleMonitorDeviceName = monitor;
                        paper.CapsuleSide = side;
                        paper.CapsuleQueueId = id;
                    }
                else destinationKey = group.Key;
                State.DeepCapsuleQueueStartTopMargins[destinationKey] = group.TopDip + delta;
                if (destinationKey != group.Key && !State.Papers.Any(p => InstanceQueueKey(p) == group.Key))
                    State.DeepCapsuleQueueStartTopMargins.Remove(group.Key);
            }
            if (sameEdge || !targetHasMembers) State.DeepCapsuleQueueStartTopMargins[destinationEdge] = layout.MasterTopDip + delta;
            if (!sameEdge && !State.Papers.Any(p => BelongsToMasterTransferSource(p, snapshot)))
            {
                State.DeepCapsuleQueueStartTopMargins.Remove(layout.EdgeKey);
                State.CapsuleCollapseAllActiveQueues.Remove(layout.EdgeKey);
            }
            if (targetHasMembers ? targetWasCollapsed : snapshot.SourceWasCollapsed)
                State.CapsuleCollapseAllActiveQueues[destinationEdge] = true;
            else State.CapsuleCollapseAllActiveQueues.Remove(destinationEdge);
        }
        catch
        {
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
        CompleteMasterCapsuleQueueTransfer(snapshot, destinationEdge, out handoffTarget);
        return true;
    }
}
