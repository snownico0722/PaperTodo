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
    internal bool CommitEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot,
        EdgeCapsuleDropAnchor anchor,
        string expectedTargetQueueKey = "",
        int expectedTargetIndex = -1)
    {
        if (snapshot == null)
        {
            _edgeCapsuleQueueTransferSources.Clear();
            return false;
        }

        _edgeCapsuleQueueTransferSources.Remove(snapshot.SourceQueueKey);

        var carried = EdgeCapsuleQueueTransferMembers(snapshot);
        if (carried.Count == 0)
        {
            ArrangeDeepCapsules(animate: true);
            return false;
        }

        // 预览已经选定的目标优先：提交和预览必须落到同一条队列、同一槽位。
        if (!string.IsNullOrEmpty(expectedTargetQueueKey) &&
            expectedTargetIndex >= 0 &&
            FreeQueueMembersExcluding(
                expectedTargetQueueKey,
                snapshot.PaperIds.FirstOrDefault() ?? "").Count > 0)
        {
            return MergeTransferredQueueInto(
                snapshot,
                carried,
                expectedTargetQueueKey,
                anchor,
                expectedTargetIndex);
        }

        // Do not inherit a drag-time hold: the pointer is released, so the Schmitt trigger's exit
        // threshold no longer describes anything meaningful.
        _deepCapsuleDragHeldQueueKey = "";
        var plan = EvaluateDeepCapsuleQueueDrag(carried, anchor, excludeEntireSourceQueue: true);

        if (plan.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge)
        {
            return MergeTransferredQueueInto(
                snapshot,
                carried,
                plan.TargetQueueKey,
                anchor);
        }

        // 只有磁吸确认存在不重叠的合法落点时才允许新建队列。None 表示整条边没有空位
        // （或输入退化），此时必须回滚，不能退回“胶囊中心 - 半高”的原始落点硬放。
        if (plan.Outcome != EdgeCapsuleQueueMagnetOutcome.NewQueue)
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        // 新队列使用磁吸解析后的落点，而不是原始光标：落点已按整队高度做过工作区夹取与避让，
        // 并且 top 与浮动胶囊的实际位置对齐。
        if (!TryResolveQueueDropGeometry(carried[0], anchor, out var drop))
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        StartTransferredQueueAt(snapshot, carried, drop, plan.NewQueueStartTopDip);
        return true;
    }

    /// <summary>
    /// Join an existing queue: every carried member adopts the target's queue id and the whole
    /// block is placed at the magnet point, so the queue reads as one piece inside the target.
    /// </summary>
    private bool MergeTransferredQueueInto(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        string targetQueueKey,
        EdgeCapsuleDropAnchor anchor,
        int expectedTargetIndex = -1,
        bool allowMonitorFallback = true)
    {
        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, snapshot.PaperIds.FirstOrDefault() ?? "");
        var targetQueueId = FirstQueueIdOf(targetMembers);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(targetQueueKey);
        var separator = edgeKey.LastIndexOf('|');
        var side = separator >= 0 && edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? DeepCapsuleSides.Left
            : DeepCapsuleSides.Right;
        var monitorName = edgeKey[..Math.Max(separator, 0)];

        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitorName, out _))
        {
            if (!allowMonitorFallback)
            {
                CancelEdgeCapsuleQueueTransfer(snapshot);
                return false;
            }

            // 目标队列的显示器已不可用：按当前落点重新决策。只有磁吸确认的 Merge/NewQueue
            // 才继续，避免未经可行性校验就硬放一个可能与现有队列重叠的新队列。
            // 指针已释放，不能沿用拖拽期的吸附保持。
            _deepCapsuleDragHeldQueueKey = "";
            var fallbackPlan = EvaluateDeepCapsuleQueueDrag(
                carried, anchor, excludeEntireSourceQueue: true);
            if (fallbackPlan.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge)
            {
                return MergeTransferredQueueInto(
                    snapshot,
                    carried,
                    fallbackPlan.TargetQueueKey,
                    anchor,
                    allowMonitorFallback: false);
            }

            if (fallbackPlan.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue &&
                TryResolveQueueDropGeometry(carried[0], anchor, out var fallbackDrop))
            {
                StartTransferredQueueAt(
                    snapshot,
                    carried,
                    fallbackDrop,
                    fallbackPlan.NewQueueStartTopDip);
                return true;
            }

            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        var insertAt = expectedTargetIndex >= 0
            ? Math.Clamp(expectedTargetIndex, 0, targetMembers.Count)
            : DeepCapsuleQueueInsertIndexFor(
                targetQueueKey,
                snapshot.PaperIds.FirstOrDefault() ?? "",
                anchor.CapsuleCenterYDevice,
                carried.Count);

        // 先求完整布局，再改成员；空间不足时恢复整队快照，不留下半完成的合并。
        var layoutPlan = EvaluateEdgeCapsuleQueueDropLayout(carried, targetQueueKey, insertAt);
        if (!layoutPlan.Feasible)
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        foreach (var member in carried)
        {
            member.CapsuleMonitorDeviceName =
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorName);
            member.CapsuleSide = side;
            member.CapsuleQueueId = targetQueueId;
        }

        InsertPaperBesideQueues(carried, targetMembers, layoutPlan.TargetIndex);
        PersistEdgeCapsuleQueueDropLayoutMargins(layoutPlan);
        RemoveVacatedQueueStartMargin(snapshot, QueueKey(monitorName, side, targetQueueId));

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
        return true;
    }

    /// <summary>
    /// No queue in range: the carried members start a queue of their own at the drop height,
    /// sharing one instance id and one rest height so the stack lands as a unit.
    ///
    /// 调用方必须传入磁吸已确认可行（不与现有队列重叠）的落点；本方法不再重复校验。
    /// </summary>
    private void StartTransferredQueueAt(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        QueueDropGeometry drop,
        double startTopDip)
    {
        var monitor = drop.Geometry;
        var side = drop.Side;
        var queueId = EdgeCapsuleQueueIds.New();

        foreach (var member in carried)
        {
            member.CapsuleMonitorDeviceName = drop.MonitorDeviceName;
            member.CapsuleSide = side;
            member.CapsuleQueueId = queueId;
        }

        RemoveVacatedQueueStartMargin(
            snapshot, QueueKey(drop.MonitorDeviceName, side, queueId));
        State.DeepCapsuleQueueStartTopMargins[QueueKey(drop.MonitorDeviceName, side, queueId)] =
            EdgeCapsuleLayout.NormalizeStartTopMargin(
                startTopDip,
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
