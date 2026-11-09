namespace PaperTodo;

// One queue-level input transaction. Per-paper gesture/placement/presentation remain in the
// existing reducer and presenter; data is not modified until a successful release.
internal sealed class NativeEdgeQueueDrag
{
    internal required string OwnerId { get; init; }
    internal required string SourceKey { get; init; }
    internal required string[] SourceIds { get; init; }
    internal required string[] CarriedIds { get; init; }
    internal required bool WholeQueue { get; init; }
    internal required bool FreeQueues { get; init; }
    internal required DeviceScreenPoint PointerDown { get; init; }
    internal required double SourceTopDip { get; init; }
    internal required double OwnerOffsetDip { get; init; }
    internal required double SourceDpiY { get; init; }
    internal required bool PreviewOrigin { get; init; }
    internal string NewQueueId { get; } = EdgeCapsuleQueueIds.New();
    internal bool Detached { get; set; }
    internal EdgeQueueExitSnapState ExitSnap { get; } = new();
    internal required bool CarriesMaster { get; init; }
    internal bool FloatingMasterHidden { get; set; }
    internal string HeldTargetKey { get; set; } = "";
    internal FreeQueueSlotPreviewState SlotPreview { get; } = new();
    internal bool FloatingCoverReady { get; set; }
    internal NativeEdgeQueueDrop? Drop { get; set; }
    internal NativeQueueDragLayout? Layout { get; set; }
}

// Capture values, not just PaperData references: side, queue and anchor can change mid-drag.
internal readonly record struct NativeQueueLayoutMember(EdgeCapsuleQueueMember Member, double Margin, bool Folded);
internal sealed record NativeQueueDragLayout(NativeQueueLayoutMember[] Members,
    IReadOnlyList<MonitorGeometry> Monitors, double Gap, bool HasMaster, EdgeCapsuleQueuePlan Resting);

internal sealed record NativeEdgeQueueDrop(
    EdgeFreeQueueDrop Decision, string DestinationKey,
    IReadOnlyDictionary<string, double> Anchors, EdgeCapsuleQueuePlan Preview,
    IReadOnlyList<PaperData> TargetMembers);

public sealed partial class AppController
{
    private NativeEdgeQueueDrag? _nativeEdgeQueueDrag;

    internal bool BeginNativeEdgeQueueDrag(PaperData owner, bool wholeQueue,
        DeviceScreenPoint pointerDown, bool previewOrigin)
    {
        if (!EdgeCapsuleFreeQueuesEnabled || !State.UseCapsuleMode ||
            !State.UseDeepCapsuleMode || _nativeEdgeQueueDrag != null) return false;
        var source = NativeQueueGroups().FirstOrDefault(g => g.Key == QueueKey(owner));
        if (source == null) return false;
        var ownerIndex = source.Papers.FindIndex(p => p.Id == owner.Id);
        if (ownerIndex < 0) return false;
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var carriesMaster = wholeQueue && source.Box.HasMaster;
        _nativeEdgeQueueDrag = new()
        {
            OwnerId = owner.Id, SourceKey = source.Key,
            SourceIds = source.Papers.Select(p => p.Id).ToArray(),
            CarriedIds = wholeQueue ? source.Papers.Select(p => p.Id).ToArray() : [owner.Id],
            WholeQueue = wholeQueue, FreeQueues = EdgeCapsuleFreeQueuesEnabled,
            CarriesMaster = wholeQueue && source.Box.HasMaster,
            PointerDown = pointerDown,
            SourceTopDip = carriesMaster ? source.Box.TopDip : source.Box.FirstMemberTop(pitch),
            OwnerOffsetDip = (ownerIndex + (carriesMaster ? 1 : 0)) * pitch,
            SourceDpiY = source.Geometry.DpiScaleY, PreviewOrigin = previewOrigin
        };
        _nativeQueueDropGhost ??= new EdgeCapsuleQueueDropGhost();
        return true;
    }

    internal double NativeEdgeQueueOwnerTop(string ownerId, DeviceScreenPoint pointer, double fallback)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || drag.OwnerId != ownerId || !drag.WholeQueue && !drag.PreviewOrigin) return fallback;
        var top = drag.SourceTopDip + (pointer.Y - drag.PointerDown.Y) / drag.SourceDpiY;
        if (drag.WholeQueue && EdgeCapsuleQueueIds.TryGetEdge(drag.SourceKey, out var monitor, out _))
        {
            var slots = drag.CarriedIds.Length + (!drag.FreeQueues && State.UseCapsuleCollapseAll ? 1 : 0);
            if (!drag.FreeQueues)
                top = EdgeCapsuleLayout.NormalizeStartTopMargin(top, EdgeCapsuleLayout.LocalWorkAreaForQueue(monitor), slots, DeepCapsuleGap);
        }
        if (drag.FreeQueues && drag.WholeQueue && EdgeCapsuleQueueIds.TryGetEdge(drag.SourceKey, out var freeMonitor, out _))
        {
            var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(freeMonitor);
            var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
            var totalSlots = drag.SourceIds.Length + (drag.CarriesMaster ? 1 : 0);
            var visibleHeight = IsCapsuleCollapseAllActiveForQueue(drag.SourceKey)
                ? PaperLayoutDefaults.CapsuleHeight : totalSlots * pitch - DeepCapsuleGap;
            top = EdgeCapsuleFreeQueuePolicy.ClampVisibleTop(top, area.Height, visibleHeight);
        }
        return top + drag.OwnerOffsetDip;
    }

    internal bool PreviewNativeEdgeQueueDrag(PaperData owner, DeviceScreenPoint pointer, double ownerTopDip, bool floating)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || drag.OwnerId != owner.Id) return false;
        if (!drag.WholeQueue && !drag.Detached)
        {
            drag.Detached = EdgeCapsuleFreeQueuePolicy.ShouldDetachSingle(floating, ownerTopDip,
                drag.SourceTopDip, drag.SourceIds.Length, EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap));
            if (!drag.Detached)
            {
                _nativeQueueDropGhost?.Hide();
                return false; // Ordinary sorting uses the unchanged legacy gesture.
            }
        }
        var previousLayout = drag.Layout;
        var resting = NativeDragRestingPlan(drag);
        var groups = NativeQueueGroups(resting, measureWidths: !floating);
        var sameLayout = drag.Layout != null && ReferenceEquals(drag.Layout, previousLayout);
        if (previousLayout != null && !sameLayout)
            drag.SlotPreview.Decision = null; // Reapply to current members, restoring any prior displacement.
        var previousDrop = sameLayout ? drag.Drop : null;
        drag.Drop = EvaluateNativeEdgeQueueDrop(drag, owner, pointer, ownerTopDip, floating, resting, groups, previousDrop);
        ApplyNativeEdgeQueuePreview(drag, ownerTopDip, floating, resting, groups);
        if (!drag.WholeQueue && drag.SourceIds.Length == 1 &&
            _masterCapsules.TryGetValue(drag.SourceKey, out var sourceMaster) && sourceMaster.IsVisible)
        {
            CompleteEdgeCapsuleQueueCompositionProxyFor(sourceMaster);
            sourceMaster.Hide();
            drag.FloatingMasterHidden = true;
        }
        UpdateNativeQueueDropGhost(drag, pointer, groups);
        return true;
    }

    private EdgeCapsuleQueuePlan NativeDragRestingPlan(NativeEdgeQueueDrag drag)
    {
        if (!EdgeCapsuleFreeQueuesEnabled || _freeMasterDragPreview != null)
        {
            drag.Layout = null;
            return BuildRestingEdgeQueuePlan();
        }

        var members = DeepCapsulePapersInOrder().Select(paper =>
        {
            var key = QueueKey(paper);
            return new NativeQueueLayoutMember(new(paper, key),
                DeepCapsuleStartTopMarginForQueueKey(key), IsCapsuleCollapseAllActiveForQueue(key));
        }).ToArray();
        var monitors = WindowWorkAreaHelper.ConnectedMonitorGeometries();
        var gap = DeepCapsuleGap;
        var hasMaster = State.UseCapsuleCollapseAll;
        if (drag.Layout is { } layout && layout.Gap == gap && layout.HasMaster == hasMaster &&
            members.AsSpan().SequenceEqual(layout.Members) && monitors.SequenceEqual(layout.Monitors))
            return layout.Resting;

        var resting = ApplyFreeQueueRestingLayout(
            BuildEdgeQueuePlan(members.Select(m => m.Member), hasMaster), includePreview: false);
        drag.Layout = new(members, monitors, gap, hasMaster, resting);
        return resting;
    }

    private NativeEdgeQueueDrop EvaluateNativeEdgeQueueDrop(NativeEdgeQueueDrag drag, PaperData owner,
        DeviceScreenPoint pointer, double ownerTopDip, bool floating,
        EdgeCapsuleQueuePlan? resting = null, IReadOnlyList<NativeQueueGroup>? groups = null,
        NativeEdgeQueueDrop? previousDrop = null)
    {
        resting ??= BuildRestingEdgeQueuePlan();
        groups ??= NativeQueueGroups(resting, measureWidths: !floating);
        var live = groups.SelectMany(g => g.Papers).ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (!live.TryGetValue(owner.Id, out var current) || QueueKey(current) != drag.SourceKey ||
            drag.CarriedIds.Any(id => !live.ContainsKey(id)) ||
            drag.WholeQueue && !groups.Where(g => g.Key == drag.SourceKey).SelectMany(g => g.Papers)
                .Select(p => p.Id).SequenceEqual(drag.SourceIds) ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var geometry))
            return RejectedNativeDrop(resting);
        var carriedIds = drag.CarriedIds.ToHashSet(StringComparer.Ordinal);
        var carried = drag.CarriedIds.Select(id => live[id]).ToArray();
        var monitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
        var side = floating ? pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
            ? DeepCapsuleSides.Left : DeepCapsuleSides.Right : DeepCapsuleSides.Normalize(owner.CapsuleSide);
        var edge = EdgeKey(monitor, side);
        var gap = DeepCapsuleGap;
        var pitch = EdgeCapsuleLayout.SlotHeight(gap);
        var top = floating ? geometry.DeviceYToLocalDip(pointer.Y) - PaperLayoutDefaults.CapsuleHeight / 2
            : drag.WholeQueue ? ownerTopDip - drag.OwnerOffsetDip : ownerTopDip;
        var newMaster = drag.FreeQueues && State.UseCapsuleCollapseAll;
        var queueTop = top - (!drag.WholeQueue && newMaster ? pitch : 0);
        var boxes = new List<EdgeFreeQueueBox>();
        foreach (var group in groups.Where(g => LiveEdgeOfQueueKey(g.Key) == edge))
        {
            var count = group.Papers.Count(p => !carriedIds.Contains(p.Id));
            var master = group.Box.HasMaster && count > 0; // A vacated queue's master leaves too.
            if (count > 0 || master) boxes.Add(group.Box with { MemberCount = count, HasMaster = master });
        }
        var bodySlots = carried.Length + (State.UseCapsuleCollapseAll && (drag.WholeQueue || drag.FreeQueues) ? 1 : 0);
        // The capsule held by the pointer selects a merge, in both drag phases.
        // A whole group's tail must not turn an independent gap into a merge
        // before that capsule reaches the target. Full height is for placement only.
        var visibleHeight = floating || drag.FreeQueues
            ? PaperLayoutDefaults.CapsuleHeight : bodySlots * pitch - gap;
        var wallDistance = (side == DeepCapsuleSides.Left ? pointer.X - geometry.WorkArea.Left : geometry.WorkArea.Right - pointer.X) / geometry.DpiScaleX;
        // A collapsed free queue is still a merge target, but its hit area is
        // the visible master alone. Legacy single-edge collapse retains its old rule.
        var candidates = drag.FreeQueues ? boxes.ToArray() :
            IsCapsuleCollapseAllActiveForQueue(edge) ? [] : boxes.ToArray();
        var target = EdgeCapsuleFreeQueuePolicy.FindInsertion(candidates,
            floating ? top : drag.FreeQueues ? ownerTopDip : top,
            visibleHeight, wallDistance, pitch, gap, drag.HeldTargetKey,
            geometry.DeviceYToLocalDip(pointer.Y), floating: floating);
        // With the optional split mode off, cross-edge Ctrl movement retains legacy one-queue semantics.
        if (!drag.FreeQueues && edge != LiveEdgeOfQueueKey(drag.SourceKey) && boxes.Count > 0)
        {
            var box = boxes[0];
            target = (box.Key, Math.Clamp((int)Math.Floor((top - box.FirstMemberTop(pitch)) / pitch + .5), 0, box.MemberCount));
        }
        // The pointer still selects its slot on every move. With the same geometry and
        // insertion index, the prospective membership and neighbour positions are identical.
        // Release always evaluates fresh, even if the last preview hit this slot.
        if (target is { } slot && previousDrop?.Decision is { Kind: EdgeFreeQueueDropKind.Insert } last &&
            last.TargetKey == slot.Key && last.Index == slot.Index)
            return previousDrop;

        var decision = target is { } insert
            ? EdgeCapsuleFreeQueuePolicy.Insert(boxes, insert.Key, insert.Index, carried.Length,
                geometry.LocalWorkAreaDip.Height, pitch, gap)
            : EdgeCapsuleFreeQueuePolicy.Place(boxes, queueTop, bodySlots, geometry.LocalWorkAreaDip.Height, pitch, gap,
                stableNeighbours: drag.FreeQueues);
        drag.HeldTargetKey = decision.Kind == EdgeFreeQueueDropKind.Insert ? decision.TargetKey : "";
        var targetMembers = target.HasValue
            ? groups.FirstOrDefault(g => g.Key == target.Value.Key)?.Papers.Where(p => !carriedIds.Contains(p.Id)).ToList() ?? []
            : new List<PaperData>();
        var sourceVacated = !State.Papers.Any(p => !carriedIds.Contains(p.Id) && InstanceQueueKey(p) == drag.SourceKey);
        var reuse = sourceVacated && LiveEdgeOfQueueKey(drag.SourceKey) == edge;
        var destination = decision.Kind == EdgeFreeQueueDropKind.Insert ? decision.TargetKey
            : !drag.FreeQueues ? edge : reuse ? drag.SourceKey : EdgeCapsuleQueueIds.Compose(edge, drag.NewQueueId);
        // Surviving source members retain their saved anchor; a passive display offset
        // must never become a persisted position merely because one member detached.
        var anchors = new Dictionary<string, double>(StringComparer.Ordinal);
        if (decision.Accepted)
        {
            if (drag.FreeQueues)
                anchors = BuildFreeQueueDropAnchors(boxes, decision, destination,
                    carried.Length, State.UseCapsuleCollapseAll);
            else
            {
                foreach (var pair in decision.Tops) anchors[pair.Key] = pair.Value;
                anchors[destination] = decision.TopDip;
            }
        }
        // Rebased preferences reproduce the complete chosen layout, including
        // unchanged neighbours whose saved tops differ from their displayed tops.
        var virtualMembers = new List<EdgeCapsuleQueueMember>();
        foreach (var group in groups)
        {
            var members = group.Papers.Where(p => !carriedIds.Contains(p.Id)).ToList();
            if (decision.Accepted && group.Key == destination)
                members.InsertRange(decision.Kind == EdgeFreeQueueDropKind.Insert ? decision.Index : 0, carried);
            virtualMembers.AddRange(members.Select(p => new EdgeCapsuleQueueMember(p, group.Key)));
        }
        if (decision.Accepted && !groups.Any(g => g.Key == destination))
            virtualMembers.AddRange(carried.Select(p => new EdgeCapsuleQueueMember(p, destination)));
        var preview = ApplyFreeQueueRestingLayout(BuildEdgeQueuePlan(virtualMembers, State.UseCapsuleCollapseAll), anchors);
        if (decision.Accepted && preview.Placements.TryGetValue(owner.Id, out var placement))
        {
            var firstTop = drag.FreeQueues
                ? EdgeCapsuleLayout.FreeQueueTopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(destination),
                    geometry.LocalWorkAreaDip, gap) + placement.TopOffsetDip
                : EdgeCapsuleLayout.TopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(destination),
                    geometry.LocalWorkAreaDip, placement.SlotCount, gap) + placement.TopOffsetDip;
            // Passive recovery must not turn an accepted exact drop into another chosen location.
            if (Math.Abs(firstTop - decision.TopDip) > .1) return RejectedNativeDrop(resting);
        }
        return new(decision, destination, anchors, preview, targetMembers);
    }

    private static NativeEdgeQueueDrop RejectedNativeDrop(EdgeCapsuleQueuePlan resting) => new(EdgeFreeQueueDrop.Rejected, "",
        new Dictionary<string, double>(), resting, []);

    private void ApplyNativeEdgeQueuePreview(NativeEdgeQueueDrag drag, double ownerTopDip, bool floating,
        EdgeCapsuleQueuePlan resting, IReadOnlyList<NativeQueueGroup> groups)
    {
        if (drag.Drop is not { } drop) return;
        // The prospective commit plan and the stable visual gap share this
        // pointer sample's resting geometry, without rebuilding it for the ghost.
        if (!drag.FreeQueues)
        {
            var carried = drag.CarriedIds.ToHashSet(StringComparer.Ordinal);
            foreach (var (id, placement) in drop.Preview.Placements)
                if (!carried.Contains(id) && _windows.TryGetValue(id, out var window))
                    window.PreviewDeepCapsulePlacement(placement);
        }
        else
        {
            PreviewFreeQueueDropSlot(drag.SlotPreview, drop.Decision,
                drag.CarriedIds, resting, groups, membershipPreview: drop.Preview);
        }
        if (drag.WholeQueue && !floating)
        {
            var members = State.Papers.Where(p => drag.SourceIds.Contains(p.Id)).ToList();
            if (members.Count != drag.SourceIds.Length) return;
            var sourcePlan = BuildEdgeQueuePlan(members.Select(p => new EdgeCapsuleQueueMember(p, drag.SourceKey)), State.UseCapsuleCollapseAll);
            var ownerIndex = Array.IndexOf(drag.CarriedIds, drag.OwnerId);
            foreach (var id in drag.CarriedIds)
            {
                if (id == drag.OwnerId || !_windows.TryGetValue(id, out var window)) continue;
                var paper = members.First(p => p.Id == id);
                var placement = sourcePlan.Placements[id];
                var desired = ownerTopDip + (Array.IndexOf(drag.CarriedIds, id) - ownerIndex) * EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
                var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(paper.CapsuleMonitorDeviceName);
                var start = DeepCapsuleStartTopMarginForQueueKey(drag.SourceKey);
                var baseTop = drag.FreeQueues
                    ? EdgeCapsuleLayout.FreeQueueTopForIndex(placement.VisualIndex, start, area, DeepCapsuleGap)
                    : EdgeCapsuleLayout.TopForIndex(placement.VisualIndex, start, area, placement.SlotCount, DeepCapsuleGap);
                window.PreviewDeepCapsulePlacement(placement with { TopOffsetDip = desired - baseTop }, animate: false);
            }
        }
        if (drag.FreeQueues)
        {
            // Ctrl still carries its own group (including its master) as one
            // rigid block, without previewing movements of unrelated masters.
            if (drag.WholeQueue && !floating &&
                _masterCapsules.TryGetValue(drag.SourceKey, out var sourceMaster))
            {
                sourceMaster.PreviewQueueTop(ownerTopDip - drag.OwnerOffsetDip);
            }
        }
        else
        {
            RefreshNativeQueueMasters(drop.Preview.MasterTopsDip);
        }
    }

    internal void PrepareNativeEdgeQueueFloating(string ownerId)
    {
        if (_nativeEdgeQueueDrag is not { WholeQueue: true } drag || drag.OwnerId != ownerId) return;
        drag.FloatingCoverReady = true; // The pooled floating HWND already owns visible pixels.
        if (drag.CarriesMaster && _masterCapsules.TryGetValue(drag.SourceKey, out var sourceMaster))
        {
            // The master is a separate HWND, not a PaperData member. Withdraw it with its
            // members so it does not remain stranded at the old edge during Ctrl transfer.
            sourceMaster.Hide();
            drag.FloatingMasterHidden = true;
        }
        var plan = BuildRestingEdgeQueuePlan();
        if (_windows.TryGetValue(ownerId, out var owner)) BeginEdgeCapsuleVisualTransaction(owner);
        foreach (var id in drag.CarriedIds)
            if (id != ownerId && _windows.TryGetValue(id, out var window) && plan.Placements.TryGetValue(id, out var placement))
                window.RetractIntoMaster(placement, animate: true);
    }

    internal int NativeEdgeQueueCarriedCount(string ownerId) =>
        _nativeEdgeQueueDrag is { WholeQueue: true } drag && drag.OwnerId == ownerId ? drag.CarriedIds.Length : 0;

    internal bool NativeEdgeQueueCarriesMaster(string ownerId) =>
        _nativeEdgeQueueDrag is { CarriesMaster: true } drag && drag.OwnerId == ownerId;

    internal bool CommitNativeEdgeQueueDrag(PaperData owner, DeviceScreenPoint pointer, double ownerTopDip, bool floating)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || drag.OwnerId != owner.Id) return false;
        if (!drag.WholeQueue && !drag.Detached && !floating) return false;
        if (drag.FreeQueues != EdgeCapsuleFreeQueuesEnabled || !State.UseCapsuleMode || !State.UseDeepCapsuleMode) return true;
        drag.Drop = EvaluateNativeEdgeQueueDrop(drag, owner, pointer, ownerTopDip, floating);
        var drop = drag.Drop;
        if (!drop.Decision.Accepted) return true;
        var carried = drag.CarriedIds.Select(id => State.Papers.First(p => p.Id == id)).ToList();
        var identities = carried.Select(p => (Paper: p, Monitor: p.CapsuleMonitorDeviceName, Side: p.CapsuleSide, Id: p.CapsuleQueueId)).ToArray();
        var previousOrder = State.Papers.ToList();
        var previousMargins = new Dictionary<string, double>(State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        try
        {
            EdgeCapsuleQueueIds.TryGetEdge(drop.DestinationKey, out var monitor, out var side);
            EdgeCapsuleQueueIds.TrySplit(drop.DestinationKey, out _, out var id);
            foreach (var paper in carried)
            {
                // Same-queue sorting must not normalize away a legitimately retained monitor tag.
                if (QueueKey(paper) == drop.DestinationKey) continue;
                paper.CapsuleMonitorDeviceName = monitor;
                paper.CapsuleSide = side;
                if (drag.FreeQueues) paper.CapsuleQueueId = id;
            }
            if (drop.Decision.Kind == EdgeFreeQueueDropKind.Insert)
                InsertNativeQueueBlock(carried, drop.TargetMembers, drop.Decision.Index);
            foreach (var (key, top) in drop.Anchors) State.DeepCapsuleQueueStartTopMargins[key] = top;
            // A free-queue master is retired with its final member. It must not pin a
            // vanished queue's saved anchor; hidden members still retain ownership.
            if (drop.DestinationKey != drag.SourceKey &&
                (drag.FreeQueues || !QueueHasMaster(drag.SourceKey)) &&
                !State.Papers.Any(p => InstanceQueueKey(p) == drag.SourceKey))
                State.DeepCapsuleQueueStartTopMargins.Remove(drag.SourceKey);
        }
        catch
        {
            State.Papers = previousOrder;
            State.DeepCapsuleQueueStartTopMargins = previousMargins;
            foreach (var row in identities)
            {
                row.Paper.CapsuleMonitorDeviceName = row.Monitor;
                row.Paper.CapsuleSide = row.Side;
                row.Paper.CapsuleQueueId = row.Id;
            }
            throw;
        }
        MarkDirty();
        RefreshTrayMenu();
        return true;
    }

    // Adapted from #354's block insertion: keep internal block order and unrelated relative order.
    private void InsertNativeQueueBlock(IReadOnlyList<PaperData> carried, IReadOnlyList<PaperData> target, int index)
    {
        var moving = carried.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var remaining = State.Papers.Where(p => !moving.Contains(p.Id)).ToList();
        var anchor = index < target.Count ? target[index] : null;
        var at = anchor != null ? remaining.FindIndex(p => p.Id == anchor.Id)
            : target.Count > 0 ? remaining.FindLastIndex(p => p.Id == target[^1].Id) + 1 : remaining.Count;
        remaining.InsertRange(at < 0 ? remaining.Count : at, carried);
        State.Papers = remaining;
    }

    internal void EndNativeEdgeQueueDrag(string ownerId)
    {
        var drag = _nativeEdgeQueueDrag;
        if (drag == null || drag.OwnerId != ownerId) return;
        _nativeQueueDropGhost?.Dispose();
        _nativeQueueDropGhost = null;
        _nativeEdgeQueueDrag = null; // Consume before publishing any window callbacks.
        if (drag.FloatingCoverReady)
        {
            var plan = BuildRestingEdgeQueuePlan();
            var owner = State.Papers.FirstOrDefault(p => p.Id == ownerId);
            if (owner != null && plan.Placements.TryGetValue(ownerId, out var ownerPlacement))
            {
                var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(owner.CapsuleMonitorDeviceName);
                var start = DeepCapsuleStartTopMarginFor(owner);
                var top = drag.FreeQueues
                    ? EdgeCapsuleLayout.FreeQueueTopForIndex(ownerPlacement.VisualIndex, start, area, DeepCapsuleGap) + ownerPlacement.TopOffsetDip
                    : EdgeCapsuleLayout.TopForIndex(ownerPlacement.VisualIndex, start, area, ownerPlacement.SlotCount, DeepCapsuleGap) + ownerPlacement.TopOffsetDip;
                foreach (var id in drag.CarriedIds)
                    if (id != ownerId && _windows.TryGetValue(id, out var window) && plan.Placements.TryGetValue(id, out var placement))
                        window.PrepareNativeQueueMemberReturn(placement, top);
            }
        }
        RefreshNativeQueueMasters();
        if (drag.FloatingMasterHidden && _masterCapsules.TryGetValue(drag.SourceKey, out var master))
        {
            var count = State.Papers.Count(p => QueueKey(p) == drag.SourceKey);
            if (count > 0)
                master.ShowPlaced(count, IsCapsuleCollapseAllActiveForQueue(drag.SourceKey),
                    animate: false);
            // If merged into another queue, the now-vacant source master stays hidden and
            // the next ordinary arrange retires it alongside its vacated queue.
        }
        ArrangeDeepCapsules(animate: true);
    }

    internal double? NativeEdgeQueueRetractionTop(PaperData paper)
    {
        if (_nativeEdgeQueueDrag is { FloatingCoverReady: true } drag &&
            paper.Id != drag.OwnerId && drag.CarriedIds.Contains(paper.Id)) return drag.SourceTopDip;
        return State.UseCapsuleFreeQueues && State.UseCapsuleCollapseAll
            ? _masterCapsules.GetValueOrDefault(QueueKey(paper))?.AnchorTop : null;
    }

    private double FreeQueueMasterTop(string monitor, string side)
    {
        var key = EdgeKey(monitor, side);
        var plan = BuildRestingEdgeQueuePlan();
        return plan.MasterTopsDip.TryGetValue(key, out var top) ? top
            : EdgeCapsuleLayout.TopForIndex(0, DeepCapsuleStartTopMarginForQueueKey(key),
                EdgeCapsuleLayout.LocalWorkAreaForQueue(monitor), 1, DeepCapsuleGap);
    }

    private void RefreshNativeQueueMasters(IReadOnlyDictionary<string, double>? tops = null)
    {
        if (!State.UseCapsuleCollapseAll) return;
        tops ??= BuildRestingEdgeQueuePlan().MasterTopsDip;
        foreach (var (key, master) in _masterCapsules.ToArray())
        {
            master.SetQueueTopPreview(tops.TryGetValue(key, out var top) ? top : null);
            var count = DeepCapsulePapersInOrder().Count(p => QueueKey(p) == key);
            master.UpdateState(count, IsCapsuleCollapseAllActiveForQueue(key), animate: true);
        }
    }
}
