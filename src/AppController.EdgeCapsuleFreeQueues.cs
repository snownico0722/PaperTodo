using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Free edge-capsule queues (D-058).
///
/// The default arrangement lets one edge hold exactly one queue, whose height comes from a single
/// remembered start margin. Free mode keeps that arrangement intact but lets several queues share
/// an edge: a capsule dropped far from every queue starts a new one, a capsule dropped near one
/// joins it, and dragging a member away takes it out on its own.
///
/// All geometry decisions live in <see cref="EdgeCapsuleQueueMagnet"/>, which is a pure function.
/// This file only collects live state into magnet inputs and applies the resulting plan.
/// </summary>
public sealed partial class AppController
{
    internal bool EdgeCapsuleFreeQueuesEnabled =>
        State.UseCapsuleFreeQueues && State.UseCapsuleMode && State.UseDeepCapsuleMode;

    /// <summary>
    /// Queue instance key of a paper, for callers outside the controller that need to tell a
    /// same-queue reorder apart from a cross-queue merge.
    /// </summary>
    internal static string QueueKeyForPaper(PaperData paper) => QueueKey(paper);

    // The queue this drag is currently snapped to. Fed back into the magnet as its held queue so
    // the Schmitt trigger's dead band keeps a queue attached while the pointer jitters near the
    // exit threshold, instead of releasing and re-acquiring every few frames.
    private string _deepCapsuleDragHeldQueueKey = "";

    /// <summary>
    /// Geometry of the monitor a queue lives on, falling back to the primary monitor when the
    /// remembered device has since been unplugged. Null when no monitor can be resolved at all.
    /// </summary>
    private static MonitorGeometry? MonitorGeometryForQueue(string? monitorDeviceName)
    {
        if (WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName),
                out var geometry))
        {
            return geometry;
        }

        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out geometry)
            ? geometry
            : null;
    }

    /// <summary>
    /// Split a capsule out of its current queue into a brand new one resting at
    /// <paramref name="startTopDip"/>. The new queue keeps its own instance id and remembered
    /// height, so it survives a restart independently of the queue it came from.
    /// </summary>
    private void SplitCapsuleIntoQueue(
        PaperData paper,
        string monitorDeviceName,
        string side,
        double startTopDip,
        string queueId)
    {
        var normalizedMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName);
        var normalizedSide = DeepCapsuleSides.Normalize(side);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(normalizedMonitor, out var geometry))
        {
            return;
        }

        paper.CapsuleMonitorDeviceName = normalizedMonitor;
        paper.CapsuleSide = normalizedSide;
        paper.CapsuleQueueId = EdgeCapsuleQueueIds.Normalize(queueId);

        // Clamp at commit time only: while dragging, the capsule tracks the pointer 1:1 so the
        // drop position never feels like it lags behind the cursor.
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            startTopDip,
            geometry.LocalWorkAreaDip,
            1,
            DeepCapsuleGap);
        State.DeepCapsuleQueueStartTopMargins[QueueKey(normalizedMonitor, normalizedSide, paper.CapsuleQueueId)] = startTop;

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>
    /// Queue snapshot the magnet reasons about: which queue each visible docked capsule belongs to,
    /// and where each queue's slot 0 sits on the target monitor.
    ///
    /// <paramref name="carried"/> is everything the gesture is taking away. A whole-queue transfer
    /// carries its entire source queue, so that queue must be dropped from the candidates — it is
    /// the thing being moved, not somewhere to merge into. A single capsule keeps its source queue
    /// as a candidate, because reordering within your own queue is a valid outcome.
    /// </summary>
    private List<EdgeCapsuleQueueMagnetCandidate> BuildEdgeCapsuleQueueMagnetCandidates(
        IReadOnlyList<PaperData> carried,
        MonitorGeometry targetGeometry,
        string targetMonitor,
        string targetSide,
        bool excludeEntireSourceQueue,
        out string sourceQueueKey)
    {
        var result = new List<EdgeCapsuleQueueMagnetCandidate>();
        sourceQueueKey = carried.Count == 0 ? "" : QueueKey(carried[0]);

        var carriedIds = new HashSet<string>(carried.Select(p => p.Id), StringComparer.Ordinal);
        var members = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (carriedIds.Contains(paper.Id) ||
                !string.Equals(WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
                    targetMonitor, StringComparison.Ordinal) ||
                DeepCapsuleSides.Normalize(paper.CapsuleSide) != targetSide)
            {
                continue;
            }

            var key = QueueKey(paper);
            if (!members.TryGetValue(key, out var list))
            {
                // Carrying the whole source queue: it must not offer itself as a merge target.
                if (excludeEntireSourceQueue && key == sourceQueueKey)
                {
                    continue;
                }

                list = new List<PaperData>();
                members[key] = list;
                order.Add(key);
            }

            list.Add(paper);
        }

        var area = targetGeometry.LocalWorkAreaDip;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        foreach (var key in order)
        {
            var papers = members[key];
            // Free mode has no master pill, so slot 0 belongs to a real capsule.
            var slotCount = papers.Count;
            var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
                DeepCapsuleStartTopMarginForQueue(targetMonitor,
                    targetSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right,
                    FirstQueueIdOf(papers)),
                area,
                slotCount,
                DeepCapsuleGap);
            result.Add(new EdgeCapsuleQueueMagnetCandidate(
                key,
                slotCount,
                EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap),
                slotHeight,
                -1));
        }

        return result;
    }

    private static string FirstQueueIdOf(List<PaperData> papers) =>
        papers.Count == 0 ? "" : EdgeCapsuleQueueIds.Normalize(papers[0].CapsuleQueueId);

    /// <summary>
    /// <summary>
    /// Commit a free-queue drag: decide with the magnet, then apply the resulting Reorder / Merge /
    /// NewQueue outcome. Falls back to the legacy arrangement when free mode is off.
    /// </summary>
    internal void CommitDeepCapsuleQueueDrop(PaperData paper, DeviceScreenPoint dropPoint)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            return;
        }

        if (!TryResolveQueueDropGeometry(paper, dropPoint, out var drop))
        {
            // No monitor to resolve against: leave the arrangement untouched rather than guess.
            ArrangeDeepCapsules(animate: true);
            return;
        }

        // The commit must not inherit a drag-time hold: the pointer is already released, so the
        // Schmitt trigger's exit threshold would no longer reflect anything meaningful.
        _deepCapsuleDragHeldQueueKey = "";
        var plan = EvaluateDeepCapsuleQueueDrop(paper, drop);

        switch (plan.Outcome)
        {
            case EdgeCapsuleQueueMagnetOutcome.None:
                ArrangeDeepCapsules(animate: true);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Reorder:
                ReorderDeepCapsuleInFreeQueue(paper, plan.TargetQueueKey, drop.Geometry, dropPoint);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Merge:
                MergeCapsuleIntoFreeQueue(paper, plan.TargetQueueKey, drop.Geometry, dropPoint);
                return;

            default:
                SplitCapsuleIntoQueue(
                    paper,
                    drop.MonitorDeviceName,
                    drop.Side,
                    plan.NewQueueStartTopDip,
                    EdgeCapsuleQueueIds.New());
                return;
        }
    }

    /// <summary>
    /// Live magnet evaluation for the drag in progress. Feeds the previous frame's target back in
    /// as the held queue so the Schmitt trigger's dead band can suppress flicker, then records the
    /// new target for the next frame.
    ///
    /// This is what turns free-queue dragging from "decide on release" into "decide continuously".
    /// </summary>
    internal EdgeCapsuleQueueMagnetPlan EvaluateDeepCapsuleQueueDrag(
        IReadOnlyList<PaperData> carried,
        DeviceScreenPoint cursor,
        bool excludeEntireSourceQueue = false)
    {
        if (carried.Count == 0 ||
            !EdgeCapsuleFreeQueuesEnabled ||
            !TryResolveQueueDropGeometry(carried[0], cursor, out var drop))
        {
            return EdgeCapsuleQueueMagnet.None;
        }

        var plan = EdgeCapsuleQueueMagnet.Evaluate(
            BuildQueueMagnetInput(carried, drop, excludeEntireSourceQueue, _deepCapsuleDragHeldQueueKey));
        _deepCapsuleDragHeldQueueKey = plan.Outcome is EdgeCapsuleQueueMagnetOutcome.Merge
            or EdgeCapsuleQueueMagnetOutcome.Reorder
            ? plan.TargetQueueKey
            : "";
        return plan;
    }

    /// <summary>
    /// Tear down everything the drag-time feedback built: the held-queue state and the gate.
    /// Called on every gesture end so a dropped outline can never outlive the drag that created it,
    /// and so the next drag starts from a clean slate rather than inheriting a stale hold.
    /// </summary>
    internal void EndDeepCapsuleQueueMagnetPreview()
    {
        _deepCapsuleDragHeldQueueKey = "";
        EndEdgeCapsuleQueueDropTargetOutline();
    }

    /// <summary>Drop the drag-time hold so the next gesture starts from a clean slate.</summary>
    internal void ForgetDeepCapsuleQueueDrag() => _deepCapsuleDragHeldQueueKey = "";

    private EdgeCapsuleQueueMagnetPlan EvaluateDeepCapsuleQueueDrop(
        PaperData paper,
        QueueDropGeometry drop) =>
        EdgeCapsuleQueueMagnet.Evaluate(BuildQueueMagnetInput([paper], drop, false, ""));

    private EdgeCapsuleQueueMagnetInput BuildQueueMagnetInput(
        IReadOnlyList<PaperData> carried,
        QueueDropGeometry drop,
        bool excludeEntireSourceQueue,
        string heldQueueKey)
    {
        var candidates = BuildEdgeCapsuleQueueMagnetCandidates(
            carried, drop.Geometry, drop.MonitorDeviceName, drop.Side, excludeEntireSourceQueue, out var sourceQueueKey);

        return new EdgeCapsuleQueueMagnetInput(
            drop.CenterTopDip,
            sourceQueueKey,
            candidates,
            EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap),
            PaperLayoutDefaults.CapsuleHeight,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            heldQueueKey);
    }

    /// <summary>
    /// Which monitor, which edge and which vertical centre a pointer position resolves to, in the
    /// local DIP space the magnet reasons about.
    /// </summary>
    private readonly record struct QueueDropGeometry(
        MonitorGeometry Geometry,
        string MonitorDeviceName,
        string Side,
        double CenterTopDip);

    private bool TryResolveQueueDropGeometry(
        PaperData paper,
        DeviceScreenPoint point,
        out QueueDropGeometry drop)
    {
        drop = default;
        MonitorGeometry? resolved = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            point,
            out var atPoint)
            ? atPoint
            : MonitorGeometryForQueue(paper.CapsuleMonitorDeviceName);
        if (!resolved.HasValue)
        {
            return false;
        }

        var geometry = resolved.GetValueOrDefault();
        drop = new QueueDropGeometry(
            geometry,
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName),
            // Choose the nearer physical wall of the target monitor.
            point.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
                ? DeepCapsuleSides.Left
                : DeepCapsuleSides.Right,
            // The dragged capsule's centre. Candidates on other monitors or the other edge are not
            // reachable by a vertical drop, so they never compete.
            geometry.DeviceYToLocalDip(point.Y) + PaperLayoutDefaults.CapsuleHeight / 2.0);
        return true;
    }

    /// <summary>
    /// Slot index a drop at <paramref name="dropTopDip"/> would occupy in
    /// <paramref name="queueKey"/>, excluding <paramref name="draggedPaper"/>.
    ///
    /// Shared by the drag preview and the commit so the gap the user was shown is exactly the gap
    /// they get. Rounding matches the pre-free-queue insertion rule (nearest slot, ties up).
    /// </summary>
    internal int DeepCapsuleQueueInsertIndexFor(
        string queueKey,
        string draggedPaperId,
        double dropTopDip,
        int carriedCount = 1)
    {
        var members = FreeQueueMembersExcluding(queueKey, draggedPaperId);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        var side = separator >= 0 && edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left
            : EdgeCapsuleEdge.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(edgeKey[..Math.Max(separator, 0)], out var geometry))
        {
            return 0;
        }

        var area = geometry.LocalWorkAreaDip;
        var realCount = members.Count + Math.Max(1, carriedCount);
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(
                edgeKey[..Math.Max(separator, 0)],
                side,
                FirstQueueIdOf(members)),
            area,
            realCount,
            DeepCapsuleGap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, realCount, DeepCapsuleGap);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        return Math.Clamp((int)Math.Floor((dropTopDip - firstTop) / slotHeight + 0.5), 0, members.Count);
    }

    /// <summary>Insert the dragged capsule into an existing queue at the slot nearest the drop.</summary>
    private void MergeCapsuleIntoFreeQueue(
        PaperData paper,
        string targetQueueKey,
        MonitorGeometry targetGeometry,
        DeviceScreenPoint dropPoint)
    {
        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, paper.Id);
        var queueId = FirstQueueIdOf(targetMembers);
        var targetSide = DeepCapsuleSides.Normalize(
            EdgeCapsuleQueueIds.StripInstance(targetQueueKey)[(EdgeCapsuleQueueIds.StripInstance(targetQueueKey).LastIndexOf('|') + 1)..]);
        var dropY = targetGeometry.DeviceYToLocalDip(dropPoint.Y);
        var insertAt = DeepCapsuleQueueInsertIndexFor(targetQueueKey, paper.Id, dropY);

        paper.CapsuleMonitorDeviceName =
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(targetGeometry.DeviceName);
        paper.CapsuleSide = targetSide;
        paper.CapsuleQueueId = queueId;
        InsertPaperBesideQueue(paper, targetMembers, insertAt);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>Move the dragged capsule to a new slot inside the queue it is already in.</summary>
    private void ReorderDeepCapsuleInFreeQueue(
        PaperData paper,
        string queueKey,
        MonitorGeometry targetGeometry,
        DeviceScreenPoint dropPoint)
    {
        var members = FreeQueueMembersExcluding(queueKey, paper.Id);
        var queueId = FirstQueueIdOf(members);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var targetSide = DeepCapsuleSides.Normalize(edgeKey[(edgeKey.LastIndexOf('|') + 1)..]);

        paper.CapsuleMonitorDeviceName =
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(targetGeometry.DeviceName);
        paper.CapsuleSide = targetSide;
        paper.CapsuleQueueId = queueId;

        var dropY = targetGeometry.DeviceYToLocalDip(dropPoint.Y);
        var insertAt = DeepCapsuleQueueInsertIndexFor(queueKey, paper.Id, dropY);

        InsertPaperBesideQueue(paper, members, insertAt);
        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    /// <summary>Diagnostics only: how many members a queue would have besides the dragged capsule.</summary>
    internal int FreeQueueMemberCount(string queueKey, string excludedPaperId) =>
        FreeQueueMembersExcluding(queueKey, excludedPaperId).Count;

    private List<PaperData> FreeQueueMembersExcluding(string queueKey, string excludedPaperId)
    {
        var members = new List<PaperData>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (paper.Id != excludedPaperId && QueueKey(paper) == queueKey)
            {
                members.Add(paper);
            }
        }

        return members;
    }

    /// <summary>Single-capsule form of <see cref="InsertPaperBesideQueues"/>.</summary>
    private void InsertPaperBesideQueue(PaperData paper, List<PaperData> targetMembers, int insertAt)
        => InsertPaperBesideQueues([paper], targetMembers, insertAt);

    /// <summary>
    /// Rebuild <see cref="AppState.Papers"/> so <paramref name="papers"/> land together at
    /// <paramref name="insertAt"/> within the run of papers that already hold the target queue.
    /// Every unrelated paper keeps its position, matching the legacy reorder rule.
    ///
    /// A whole-queue transfer inserts its members as one block, so the queue's internal order is
    /// preserved exactly as it was before the drag.
    /// </summary>
    private void InsertPaperBesideQueues(
        IReadOnlyList<PaperData> papers,
        List<PaperData> targetMembers,
        int insertAt)
    {
        if (papers.Count == 0)
        {
            return;
        }

        var movingIds = new HashSet<string>(papers.Select(p => p.Id), StringComparer.Ordinal);
        var rebuilt = new List<PaperData>(State.Papers.Count);
        var anchorPaper = insertAt < targetMembers.Count ? targetMembers[insertAt] : null;
        foreach (var existing in State.Papers)
        {
            if (movingIds.Contains(existing.Id))
            {
                continue;
            }

            if (anchorPaper != null && existing.Id == anchorPaper.Id)
            {
                rebuilt.AddRange(papers);
            }

            rebuilt.Add(existing);
        }

        if (anchorPaper == null)
        {
            if (targetMembers.Count > 0)
            {
                var lastId = targetMembers[^1].Id;
                var index = rebuilt.FindIndex(p => p.Id == lastId);
                rebuilt.InsertRange(index + 1, papers);
            }
            else
            {
                rebuilt.AddRange(papers);
            }
        }

        State.Papers = rebuilt;
    }
}
