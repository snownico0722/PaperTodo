namespace PaperTodo;

// One cached drop decision per gesture: moving the pointer within a chosen
// slot never accumulates displacement or advances a second collision simulation.
internal sealed class FreeQueueSlotPreviewState
{
    internal EdgeFreeQueueDrop? Decision { get; set; }
    internal string TargetKey => Decision is { Kind: EdgeFreeQueueDropKind.Insert } ? Decision.TargetKey : "";
    internal int Index => Decision is { Kind: EdgeFreeQueueDropKind.Insert } ? Decision.Index : -1;
    internal double Top => Decision is { Accepted: true } ? Decision.TopDip : double.NaN;
    internal HashSet<string> MemberIds { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> MasterKeys { get; } = new(StringComparer.Ordinal);
}

public sealed partial class AppController
{
    // Reuses the same exact slot geometry for Ctrl transfers and direct master
    // drags. The target suffix leaves precisely carriedIds.Count slots; only
    // actual colliding neighbour queues from the drop policy also give way.
    // A docked master includes resting placements in this single final pass.
    // An unchanged slot keeps its already-presented displacement; it is never
    // reset to the resting layout and then pushed away a second time.
    private void PreviewFreeQueueDropSlot(FreeQueueSlotPreviewState state,
        EdgeFreeQueueDrop? decision,
        IReadOnlyCollection<string> carriedIds, EdgeCapsuleQueuePlan resting,
        IReadOnlyList<NativeQueueGroup> groups, bool includeResting = false,
        EdgeCapsuleQueuePlan? membershipPreview = null)
    {
        var inserting = decision is { Kind: EdgeFreeQueueDropKind.Insert };
        var key = inserting ? decision!.TargetKey : "";
        var index = inserting ? decision!.Index : -1;
        var accepted = decision is { Accepted: true };
        var top = accepted ? decision!.TopDip : double.NaN;
        var sameSlot = key == state.TargetKey && index == state.Index &&
            (double.IsNaN(top) && double.IsNaN(state.Top) ||
             Math.Abs(top - state.Top) < .1) &&
            decision?.Kind == state.Decision?.Kind &&
            (decision == null || state.Decision != null &&
             decision.Tops.Count == state.Decision.Tops.Count &&
             decision.Tops.All(pair => state.Decision.Tops.TryGetValue(pair.Key, out var oldTop) &&
                 Math.Abs(pair.Value - oldTop) < .1));
        if (sameSlot && !includeResting) return;

        var moving = carriedIds.ToHashSet(StringComparer.Ordinal);
        var desired = new Dictionary<string, EdgeCapsulePlacement>(StringComparer.Ordinal);
        var masterTops = new Dictionary<string, double>(StringComparer.Ordinal);
        if (includeResting)
        {
            foreach (var (id, placement) in resting.Placements)
                if (!moving.Contains(id) && (!sameSlot || !state.MemberIds.Contains(id)))
                    desired[id] = placement;
            foreach (var (masterKey, masterTop) in resting.MasterTopsDip)
                if (!sameSlot || !state.MasterKeys.Contains(masterKey))
                    masterTops[masterKey] = masterTop;
        }
        else
        {
            foreach (var id in state.MemberIds)
                if (resting.Placements.TryGetValue(id, out var placement)) desired[id] = placement;
            foreach (var masterKey in state.MasterKeys)
                if (resting.MasterTopsDip.TryGetValue(masterKey, out var oldTop))
                    masterTops[masterKey] = oldTop;
        }

        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var changedMasters = new HashSet<string>(StringComparer.Ordinal);
        if (!sameSlot && accepted)
        {
            foreach (var group in groups)
            {
                if (group.Key == key) continue;
                // #354 (Just1ceCATT): close the source gap as soon as a member
                // leaves. Reuse this drop's coordinator plan and preview cache,
                // rather than adding a separate vacated-source state machine.
                var compacting = membershipPreview != null &&
                    group.Papers.Any(p => moving.Contains(p.Id)) &&
                    group.Papers.Any(p => !moving.Contains(p.Id));
                var movedTop = compacting
                    ? membershipPreview!.MasterTopsDip.GetValueOrDefault(group.Key, group.Box.TopDip)
                    : decision!.Tops.GetValueOrDefault(group.Key, group.Box.TopDip);
                if (!compacting && Math.Abs(movedTop - group.Box.TopDip) < .1) continue;

                // Shift only the neighbouring queue that the selected block actually
                // intersects. The source instead uses its remaining-member plan,
                // including any displacement already chosen by this same drop.
                var delta = movedTop - group.Box.TopDip;
                if (!group.Box.Collapsed)
                    foreach (var paper in group.Papers.Where(p => !moving.Contains(p.Id)))
                        if (resting.Placements.TryGetValue(paper.Id, out var placement))
                        {
                            desired[paper.Id] = compacting
                                ? membershipPreview!.Placements[paper.Id]
                                : placement with { TopOffsetDip = placement.TopOffsetDip + delta };
                            changedIds.Add(paper.Id);
                        }
                if (group.Box.HasMaster && Math.Abs(movedTop - group.Box.TopDip) >= .1)
                {
                    masterTops[group.Key] = movedTop;
                    changedMasters.Add(group.Key);
                }
            }
        }

        if (!sameSlot && inserting && groups.FirstOrDefault(g => g.Key == key) is { } target)
        {
            var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
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
                window.PreviewDeepCapsulePlacement(placement, animate: true);
        foreach (var (masterKey, desiredTop) in masterTops)
            if (_masterCapsules.TryGetValue(masterKey, out var master))
            {
                // Only a wholly carried group follows the pointer directly.
                // The remaining source of a single-member drag is passive too.
                var carriedGroup = groups.Any(group => group.Key == masterKey &&
                    group.Papers.All(paper => moving.Contains(paper.Id)));
                master.PreviewQueueTop(desiredTop, animate: !carriedGroup);
            }

        if (sameSlot) return;
        state.MemberIds.Clear();
        foreach (var id in changedIds) state.MemberIds.Add(id);
        state.MasterKeys.Clear();
        foreach (var masterKey in changedMasters) state.MasterKeys.Add(masterKey);
        state.Decision = decision;
    }
}

