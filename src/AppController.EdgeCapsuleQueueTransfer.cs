namespace PaperTodo;

/// <summary>
/// State captured when a free-queue drag escalates into a whole-queue move, so the gesture can be
/// rolled back exactly if it is cancelled.
/// </summary>
internal sealed record EdgeCapsuleQueueTransferSnapshot(
    string SourceQueueKey,
    IReadOnlyList<string> PaperIds,
    string MonitorDeviceName,
    string Side,
    string QueueId,
    double StartTopMargin,
    bool HadStartTopMargin);

/// <summary>
/// Whole-queue drag for free queues (D-058).
///
/// Holding Ctrl while dragging any member lifts the entire queue off the wall and drops it as a
/// single capsule, mirroring what the master pill already does for a whole edge. The source
/// queue's members are frozen for the duration so the layout cannot reflow underneath the drag.
/// </summary>
public sealed partial class AppController
{
    // Queue instance keys currently mid-transfer. A paper in one of these queues is not allowed to
    // take part in layout decisions until the gesture commits or rolls back.
    private readonly HashSet<string> _edgeCapsuleQueueTransferSources = new(StringComparer.Ordinal);

    internal bool IsEdgeCapsuleQueueTransferActive => _edgeCapsuleQueueTransferSources.Count > 0;

    internal bool IsPaperInTransferringEdgeQueue(PaperData paper) =>
        _edgeCapsuleQueueTransferSources.Contains(QueueKey(paper));

    /// <summary>
    /// Freeze the queue <paramref name="paper"/> belongs to and return everything needed to undo it.
    /// Returns false when free queues are off, nothing is docked, or a transfer is already running.
    /// </summary>
    internal bool TryBeginEdgeCapsuleQueueTransfer(
        PaperData paper,
        out EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!EdgeCapsuleFreeQueuesEnabled || _edgeCapsuleQueueTransferSources.Count > 0)
        {
            return false;
        }

        var queueKey = QueueKey(paper);
        var members = FreeQueueMembersExcluding(queueKey, "");
        if (members.Count == 0)
        {
            return false;
        }

        var edge = paper.CapsuleSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;
        var startKey = QueueKey(paper.CapsuleMonitorDeviceName, paper.CapsuleSide, paper.CapsuleQueueId);
        snapshot = new EdgeCapsuleQueueTransferSnapshot(
            queueKey,
            members.Select(p => p.Id).ToList(),
            paper.CapsuleMonitorDeviceName,
            DeepCapsuleSides.Normalize(paper.CapsuleSide),
            EdgeCapsuleQueueIds.Normalize(paper.CapsuleQueueId),
            State.DeepCapsuleQueueStartTopMargins.TryGetValue(startKey, out var margin)
                ? margin
                : EdgeCapsuleLayout.StartTopMargin,
            State.DeepCapsuleQueueStartTopMargins.ContainsKey(startKey));

        _edgeCapsuleQueueTransferSources.Add(queueKey);
        return true;
    }

    /// <summary>Put the queue back exactly as it was before the drag started.</summary>
    internal void CancelEdgeCapsuleQueueTransfer(EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        _edgeCapsuleQueueTransferSources.Remove(snapshot?.SourceQueueKey ?? "");
        if (snapshot == null)
        {
            return;
        }

        var byId = State.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in snapshot.PaperIds)
        {
            if (byId.TryGetValue(id, out var member))
            {
                member.CapsuleMonitorDeviceName = snapshot.MonitorDeviceName;
                member.CapsuleSide = snapshot.Side;
                member.CapsuleQueueId = snapshot.QueueId;
            }
        }

        RestoreQueueStartMargin(snapshot);
        ArrangeDeepCapsules(animate: false);
    }

    /// <summary>
    /// Commit a whole-queue drag. Decides with the same magnet a single capsule uses, so carrying
    /// five capsules lands exactly where carrying one would. On a merge the whole queue joins the
    /// target at the magnet point, keeping its internal order; with no candidate it starts a queue
    /// of its own at the drop height.
    /// </summary>
    internal void CommitEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot,
        DeviceScreenPoint dropPoint)
    {
        if (snapshot == null)
        {
            _edgeCapsuleQueueTransferSources.Clear();
            return;
        }

        _edgeCapsuleQueueTransferSources.Remove(snapshot.SourceQueueKey);

        var carried = EdgeCapsuleQueueTransferMembers(snapshot);
        if (carried.Count == 0)
        {
            ArrangeDeepCapsules(animate: true);
            return;
        }

        // Do not inherit a drag-time hold: the pointer is released, so the Schmitt trigger's exit
        // threshold no longer describes anything meaningful.
        _deepCapsuleDragHeldQueueKey = "";
        var plan = EvaluateDeepCapsuleQueueDrag(carried, dropPoint, excludeEntireSourceQueue: true);

        if (plan.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge)
        {
            MergeTransferredQueueInto(snapshot, carried, plan.TargetQueueKey, dropPoint);
            return;
        }

        StartTransferredQueueAt(snapshot, carried, dropPoint);
    }

    /// <summary>
    /// Join an existing queue: every carried member adopts the target's queue id and the whole
    /// block is placed at the magnet point, so the queue reads as one piece inside the target.
    /// </summary>
    private void MergeTransferredQueueInto(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        string targetQueueKey,
        DeviceScreenPoint dropPoint)
    {
        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, snapshot.PaperIds.FirstOrDefault() ?? "");
        var targetQueueId = FirstQueueIdOf(targetMembers);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(targetQueueKey);
        var separator = edgeKey.LastIndexOf('|');
        var side = separator >= 0 && edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? DeepCapsuleSides.Left
            : DeepCapsuleSides.Right;
        var monitorName = edgeKey[..Math.Max(separator, 0)];

        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitorName, out var geometry))
        {
            StartTransferredQueueAt(snapshot, carried, dropPoint);
            return;
        }

        var insertAt = DeepCapsuleQueueInsertIndexFor(
            targetQueueKey,
            snapshot.PaperIds.FirstOrDefault() ?? "",
            geometry.DeviceYToLocalDip(dropPoint.Y),
            carried.Count);

        foreach (var member in carried)
        {
            member.CapsuleMonitorDeviceName =
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorName);
            member.CapsuleSide = side;
            member.CapsuleQueueId = targetQueueId;
        }

        InsertPaperBesideQueues(carried, targetMembers, insertAt);
        RemoveVacatedQueueStartMargin(snapshot, QueueKey(monitorName, side, targetQueueId));

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>
    /// No queue in range: the carried members start a queue of their own at the drop height,
    /// sharing one instance id and one rest height so the stack lands as a unit.
    /// </summary>
    private void StartTransferredQueueAt(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        DeviceScreenPoint dropPoint)
    {
        var geometry = MonitorGeometryForQueue(snapshot.MonitorDeviceName);
        if (geometry is null)
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return;
        }

        var monitor = geometry.GetValueOrDefault();
        var side = dropPoint.X < monitor.WorkArea.Left + monitor.WorkArea.Width / 2.0
            ? DeepCapsuleSides.Left
            : DeepCapsuleSides.Right;
        var queueId = EdgeCapsuleQueueIds.New();

        foreach (var member in carried)
        {
            member.CapsuleMonitorDeviceName =
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitor.DeviceName);
            member.CapsuleSide = side;
            member.CapsuleQueueId = queueId;
        }

        RemoveVacatedQueueStartMargin(snapshot, QueueKey(monitor.DeviceName, side, queueId));
        State.DeepCapsuleQueueStartTopMargins[QueueKey(monitor.DeviceName, side, queueId)] =
            EdgeCapsuleLayout.NormalizeStartTopMargin(
                monitor.DeviceYToLocalDip(dropPoint.Y),
                monitor.LocalWorkAreaDip,
                carried.Count,
                DeepCapsuleGap);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>
    /// The source queue's remembered height must not outlive it. Kept only when the queue actually
    /// kept a member, so dropping a queue back onto its own start does not erase the memory.
    /// </summary>
    private void RemoveVacatedQueueStartMargin(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        string destinationStartKey)
    {
        var sourceStartKey = QueueKey(snapshot.MonitorDeviceName, snapshot.Side, snapshot.QueueId);
        if (string.Equals(sourceStartKey, destinationStartKey, StringComparison.Ordinal))
        {
            return;
        }

        if (!State.Papers.Any(p => QueueKey(p) == sourceStartKey))
        {
            State.DeepCapsuleQueueStartTopMargins.Remove(sourceStartKey);
        }
    }

    /// <summary>
    /// Members the in-flight whole-queue transfer is carrying, in their existing order.
    /// Empty when no transfer is running or the snapshot is unusable.
    /// </summary>
    internal IReadOnlyList<PaperData> EdgeCapsuleQueueTransferMembers(
        EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.PaperIds.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<string, PaperData>(StringComparer.Ordinal);
        foreach (var paper in State.Papers)
        {
            byId[paper.Id] = paper;
        }

        var members = new List<PaperData>(snapshot.PaperIds.Count);
        foreach (var id in snapshot.PaperIds)
        {
            if (byId.TryGetValue(id, out var paper))
            {
                members.Add(paper);
            }
        }

        return members;
    }

    /// <summary>
    /// Hide every member of a transferring queue except the one being dragged, so the stack
    /// visually lifts off the wall as the single floating capsule.
    ///
    /// Must be called only after the floating host is on screen. Detaching first would leave a
    /// frame where nothing is drawn at all, which AGENTS.md forbids.
    /// </summary>
    internal void HideEdgeCapsuleQueueTransferMembers(string keepPaperId)
    {
        foreach (var key in _edgeCapsuleQueueTransferSources)
        {
            foreach (var paper in DeepCapsulePapersInOrder())
            {
                if (paper.Id == keepPaperId || QueueKey(paper) != key)
                {
                    continue;
                }

                if (_windows.TryGetValue(paper.Id, out var window))
                {
                    window.DetachFromDeepCapsuleStack();
                }
            }
        }
    }

    private void RestoreQueueStartMargin(EdgeCapsuleQueueTransferSnapshot snapshot)    {
        var key = QueueKey(snapshot.MonitorDeviceName, snapshot.Side, snapshot.QueueId);
        if (snapshot.HadStartTopMargin)
        {
            State.DeepCapsuleQueueStartTopMargins[key] = snapshot.StartTopMargin;
        }
        else
        {
            State.DeepCapsuleQueueStartTopMargins.Remove(key);
        }
    }
}
