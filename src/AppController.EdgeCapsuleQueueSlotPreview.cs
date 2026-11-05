namespace PaperTodo;

// One cached insertion decision per gesture: moving the pointer within a chosen
// slot never accumulates displacement or advances a second collision simulation.
internal sealed class FreeQueueSlotPreviewState
{
    internal string TargetKey { get; set; } = "";
    internal int Index { get; set; } = -1;
    internal double Top { get; set; } = double.NaN;
    internal HashSet<string> MemberIds { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> MasterKeys { get; } = new(StringComparer.Ordinal);
}

public sealed partial class AppController
{
    // Reuses the same exact slot geometry for Ctrl transfers and direct master
    // drags. The target suffix leaves precisely carriedIds.Count slots; only
    // actual colliding neighbour queues from the drop policy also give way.
    // forceReapply is for docked master movement: its own queue position is
    // recomputed every pointer sample, while the insertion slot stays fixed.
    private void PreviewFreeQueueDropSlot(FreeQueueSlotPreviewState state,
        EdgeFreeQueueDrop? decision, string sourceKey,
        IReadOnlyCollection<string> carriedIds, bool forceReapply = false)
    {
        var inserting = decision is { Kind: EdgeFreeQueueDropKind.Insert };
        var key = inserting ? decision!.TargetKey : "";
        var index = inserting ? decision!.Index : -1;
        var top = inserting ? decision!.TopDip : double.NaN;
        if (!forceReapply && key == state.TargetKey && index == state.Index &&
            (double.IsNaN(top) && double.IsNaN(state.Top) ||
             Math.Abs(top - state.Top) < .1)) return;

        var resting = BuildRestingEdgeQueuePlan();
        var groups = NativeQueueGroups();
        var desired = new Dictionary<string, EdgeCapsulePlacement>(StringComparer.Ordinal);
        foreach (var id in state.MemberIds)
            if (resting.Placements.TryGetValue(id, out var placement)) desired[id] = placement;

        var masterTops = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var masterKey in state.MasterKeys)
            if (resting.MasterTopsDip.TryGetValue(masterKey, out var oldTop))
                masterTops[masterKey] = oldTop;

        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var changedMasters = new HashSet<string>(StringComparer.Ordinal);
        if (inserting && groups.FirstOrDefault(g => g.Key == key) is { } target)
        {
            var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
            var moving = carriedIds.ToHashSet(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                if (group.Key == sourceKey || group.Key == key ||
                    !decision!.Tops.TryGetValue(group.Key, out var movedTop) ||
                    Math.Abs(movedTop - group.Box.TopDip) < .1) continue;

                // Shift only the neighbouring queue that the selected block actually
                // intersects; its master and members use one rigid displacement.
                var delta = movedTop - group.Box.TopDip;
                if (!group.Box.Collapsed)
                    foreach (var paper in group.Papers)
                        if (resting.Placements.TryGetValue(paper.Id, out var placement))
                        {
                            desired[paper.Id] = placement with { TopOffsetDip = placement.TopOffsetDip + delta };
                            changedIds.Add(paper.Id);
                        }
                if (group.Box.HasMaster)
                {
                    masterTops[group.Key] = movedTop;
                    changedMasters.Add(group.Key);
                }
            }

            if (!target.Box.Collapsed)
            {
                var members = target.Papers.Where(p => !moving.Contains(p.Id)).ToList();
                var margin = DeepCapsuleStartTopMarginForQueueKey(key);
                for (var i = 0; i < members.Count; i++)
                {
                    var paper = members[i];
                    if (!resting.Placements.TryGetValue(paper.Id, out var placement)) continue;
                    var newSlot = i < index ? i : i + carriedIds.Count;
                    var requested = top + (target.Box.HasMaster ? pitch : 0) + newSlot * pitch;
                    var baseTop = EdgeCapsuleLayout.FreeQueueTopForIndex(
                        placement.VisualIndex, margin, target.Geometry.LocalWorkAreaDip, DeepCapsuleGap);
                    var next = placement with { TopOffsetDip = requested - baseTop };
                    if (next != placement)
                    {
                        desired[paper.Id] = next;
                        changedIds.Add(paper.Id);
                    }
                }
            }
            if (target.Box.HasMaster && Math.Abs(top - target.Box.TopDip) >= .1)
            {
                masterTops[key] = top;
                changedMasters.Add(key);
            }
        }

        foreach (var (id, placement) in desired)
            if (_windows.TryGetValue(id, out var window))
                window.PreviewDeepCapsulePlacement(placement, animate: false);
        foreach (var (masterKey, desiredTop) in masterTops)
            if (_masterCapsules.TryGetValue(masterKey, out var master))
            {
                master.SetQueueTopPreview(desiredTop);
                master.UpdateState(State.Papers.Count(p => QueueKey(p) == masterKey),
                    IsCapsuleCollapseAllActiveForQueue(masterKey), animate: false);
            }

        state.MemberIds.Clear();
        foreach (var id in changedIds) state.MemberIds.Add(id);
        state.MasterKeys.Clear();
        foreach (var masterKey in changedMasters) state.MasterKeys.Add(masterKey);
        state.TargetKey = key;
        state.Index = index;
        state.Top = top;
    }
}
