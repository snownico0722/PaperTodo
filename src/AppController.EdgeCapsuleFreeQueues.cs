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
    /// </summary>
    private List<EdgeCapsuleQueueMagnetCandidate> BuildEdgeCapsuleQueueMagnetCandidates(
        PaperData dragged,
        MonitorGeometry targetGeometry,
        string targetMonitor,
        string targetSide,
        out string sourceQueueKey)
    {
        var result = new List<EdgeCapsuleQueueMagnetCandidate>();
        sourceQueueKey = QueueKey(dragged);

        var members = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (paper.Id == dragged.Id ||
                !string.Equals(WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
                    targetMonitor, StringComparison.Ordinal) ||
                DeepCapsuleSides.Normalize(paper.CapsuleSide) != targetSide)
            {
                continue;
            }

            var key = QueueKey(paper);
            if (!members.TryGetValue(key, out var list))
            {
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
    /// Commit a free-queue drag: decide with the magnet, then apply the resulting Reorder / Merge /
    /// NewQueue outcome. Falls back to the legacy arrangement when free mode is off.
    /// </summary>
    internal void CommitDeepCapsuleQueueDrop(PaperData paper, DeviceScreenPoint dropPoint)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            return;
        }

        MonitorGeometry? resolvedGeometry = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            dropPoint,
            out var atDropPoint)
            ? atDropPoint
            : MonitorGeometryForQueue(paper.CapsuleMonitorDeviceName);
        if (!resolvedGeometry.HasValue)
        {
            // No monitor to resolve against: leave the arrangement untouched rather than guess.
            ArrangeDeepCapsules(animate: true);
            return;
        }

        var geometry = resolvedGeometry.GetValueOrDefault();

        var targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName);
        var targetSide = dropPoint.X <
                         geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
            ? DeepCapsuleSides.Left
            : DeepCapsuleSides.Right;

        // The dragged capsule's centre in the target monitor's DIP space. Candidates on other
        // monitors or the other edge are not reachable by a vertical drop, so they never compete.
        var dropCenterTopDip =
            geometry.DeviceYToLocalDip(dropPoint.Y) + PaperLayoutDefaults.CapsuleHeight / 2.0;

        var candidates = BuildEdgeCapsuleQueueMagnetCandidates(
            paper, geometry, targetMonitor, targetSide, out var sourceQueueKey);

        var plan = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
            dropCenterTopDip,
            sourceQueueKey,
            candidates,
            EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap),
            PaperLayoutDefaults.CapsuleHeight,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            ""));

        switch (plan.Outcome)
        {
            case EdgeCapsuleQueueMagnetOutcome.None:
                ArrangeDeepCapsules(animate: true);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Reorder:
                ReorderDeepCapsuleInFreeQueue(paper, plan.TargetQueueKey, geometry, dropPoint);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Merge:
                MergeCapsuleIntoFreeQueue(paper, plan.TargetQueueKey, geometry, dropPoint);
                return;

            default:
                SplitCapsuleIntoQueue(
                    paper,
                    targetMonitor,
                    targetSide,
                    plan.NewQueueStartTopDip,
                    EdgeCapsuleQueueIds.New());
                return;
        }
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

        var area = targetGeometry.LocalWorkAreaDip;
        var realCount = targetMembers.Count + 1;
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(
                targetGeometry.DeviceName,
                targetSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right,
                queueId),
            area,
            realCount,
            DeepCapsuleGap);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, realCount, DeepCapsuleGap);
        var dropY = targetGeometry.DeviceYToLocalDip(dropPoint.Y);
        var insertAt = Math.Clamp(
            (int)Math.Floor((dropY - firstTop) / slotHeight + 0.5),
            0,
            targetMembers.Count);

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

        var area = targetGeometry.LocalWorkAreaDip;
        var realCount = members.Count + 1;
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(
                targetGeometry.DeviceName,
                targetSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right,
                queueId),
            area,
            realCount,
            DeepCapsuleGap);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, realCount, DeepCapsuleGap);
        var dropY = targetGeometry.DeviceYToLocalDip(dropPoint.Y);
        var insertAt = Math.Clamp(
            (int)Math.Floor((dropY - firstTop) / slotHeight + 0.5),
            0,
            members.Count);

        InsertPaperBesideQueue(paper, members, insertAt);
        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        MarkDirty();
    }

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

    /// <summary>
    /// Rebuild <see cref="AppState.Papers"/> so the dragged paper lands at
    /// <paramref name="insertAt"/> within the run of papers that already hold the target queue.
    /// Every unrelated paper keeps its position, matching the legacy reorder rule.
    /// </summary>
    private void InsertPaperBesideQueue(PaperData paper, List<PaperData> targetMembers, int insertAt)
    {
        var rebuilt = new List<PaperData>(State.Papers.Count);
        var anchorPaper = insertAt < targetMembers.Count ? targetMembers[insertAt] : null;
        foreach (var existing in State.Papers)
        {
            if (existing.Id == paper.Id)
            {
                continue;
            }

            if (anchorPaper != null && existing.Id == anchorPaper.Id)
            {
                rebuilt.Add(paper);
            }

            rebuilt.Add(existing);
        }

        if (anchorPaper == null)
        {
            if (targetMembers.Count > 0)
            {
                var lastId = targetMembers[^1].Id;
                var index = rebuilt.FindIndex(p => p.Id == lastId);
                rebuilt.Insert(index + 1, paper);
            }
            else
            {
                rebuilt.Add(paper);
            }
        }

        State.Papers = rebuilt;
    }
}
