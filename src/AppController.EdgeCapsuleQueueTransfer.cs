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
    /// Move every member of the transferred queue to the drop point. The queue keeps one shared
    /// instance id and one shared rest height, so the whole stack lands as a unit.
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

        var byId = State.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var moved = 0;
        foreach (var id in snapshot.PaperIds)
        {
            if (!byId.TryGetValue(id, out var member))
            {
                continue;
            }

            member.CapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitor.DeviceName);
            member.CapsuleSide = side;
            member.CapsuleQueueId = queueId;
            moved++;
        }

        if (moved == 0)
        {
            ArrangeDeepCapsules(animate: true);
            return;
        }

        // A queue resting higher up on the screen must not remember the source's old height, and
        // the source key must not keep pointing at a queue that no longer exists there.
        var sourceStartKey = QueueKey(snapshot.MonitorDeviceName, snapshot.Side, snapshot.QueueId);
        if (!string.Equals(sourceStartKey,
                QueueKey(monitor.DeviceName, side, queueId),
                StringComparison.Ordinal))
        {
            State.DeepCapsuleQueueStartTopMargins.Remove(sourceStartKey);
        }

        var startTopDip = monitor.DeviceYToLocalDip(dropPoint.Y);
        State.DeepCapsuleQueueStartTopMargins[QueueKey(monitor.DeviceName, side, queueId)] =
            EdgeCapsuleLayout.NormalizeStartTopMargin(
                startTopDip,
                monitor.LocalWorkAreaDip,
                moved,
                DeepCapsuleGap);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
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
