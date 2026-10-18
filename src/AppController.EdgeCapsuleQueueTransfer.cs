namespace PaperTodo;

/// <summary>
/// State captured when a free-queue drag escalates into a whole-queue move, so the gesture can be
/// rolled back exactly if it is cancelled.
/// </summary>
/// <remarks>
/// FQ-14 残余：仅恢复成员身份与源队列 margin 不足以覆盖完整提交——
/// <see cref="MergeTransferredQueueInto"/> / <see cref="StartTransferredQueueAt"/> 在
/// 「改身份 → 重排 State.Papers → 持久化 margin → Arrange/RefreshTrayMenu/SaveNow」
/// 区间内一旦抛异常，<see cref="AppController.CancelEdgeCapsuleQueueTransfer"/> 只会恢复
/// 身份与源队列 margin，不能回滚目标/邻队 margin 与 State.Papers 顺序。
/// 在 commit 入口额外快照 <see cref="PaperOrderIds"/> 与 <see cref="StartTopMargins"/>，
/// 让异常路径具备完整回滚能力。
/// </remarks>
internal sealed record EdgeCapsuleQueueTransferSnapshot(
    string SourceQueueKey,
    IReadOnlyList<string> PaperIds,
    string MonitorDeviceName,
    string Side,
    string QueueId,
    double StartTopMargin,
    bool HadStartTopMargin,
    // --- FQ-14: order + margin snapshots so a partial commit can be reverted wholesale ---
    // PaperOrderIds 是提交前 State.Papers 的**完整** id 顺序（不只是 carried 成员），
    // 因为提交会把整块搬到目标队列的插入位，只记相对顺序无法还原槽位。
    IReadOnlyList<string>? PaperOrderIds,
    IReadOnlyDictionary<string, double>? StartTopMargins);

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

    /// <summary>
    /// 测试钩子：在整队提交的 mutation 区间中途抛异常，用于验证 FQ-14 的回滚是否完整
    /// （State.Papers 顺序 + 目标/邻队 margin + 成员身份）。生产路径恒为 null。
    /// </summary>
    internal Action? EdgeCapsuleQueueTransferCommitFailureInjector { get; set; }

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

        // free 模式下 QueueKey 已是持久实例 key；快照的 margin 与恢复/清理都使用它，
        // 显示器断连/重连不会把源队列重定向到主屏。
        var startKey = queueKey;
        snapshot = new EdgeCapsuleQueueTransferSnapshot(
            queueKey,
            members.Select(p => p.Id).ToList(),
            paper.CapsuleMonitorDeviceName,
            DeepCapsuleSides.Normalize(paper.CapsuleSide),
            EdgeCapsuleQueueIds.Normalize(paper.CapsuleQueueId),
            State.DeepCapsuleQueueStartTopMargins.TryGetValue(startKey, out var margin)
                ? margin
                : EdgeCapsuleLayout.StartTopMargin,
            State.DeepCapsuleQueueStartTopMargins.ContainsKey(startKey),
            // FQ-14: 此刻 OrderIds/Margins 还没被 commit mutation 替换为可回滚点，留 null。
            // 真实快照在 commit 入口由 CaptureEdgeCapsuleQueueTransferCommit 填充；
            // 用户取消路径因没有 mutation，本就无需回滚。
            null,
            null);

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

        // 原生拖拽会泵 Dispatcher，外部 API/插件可能在拖拽期间增删、隐藏或移动成员。
        // 提交前必须按 live 成员序列复核，不能把旧快照覆盖到更新的状态上。
        if (!EdgeCapsuleQueueTransferSourceMatchesSnapshot(snapshot))
        {
            ReleaseEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        var carried = EdgeCapsuleQueueTransferMembers(snapshot);
        if (carried.Count == 0)
        {
            ArrangeDeepCapsules(animate: true);
            return false;
        }

        // 预览已经选定的目标优先：提交和预览必须落到同一条队列、同一槽位。
        if (!string.IsNullOrEmpty(expectedTargetQueueKey) &&
            expectedTargetIndex >= 0 &&
            FreeQueueMembersExcludingIds(expectedTargetQueueKey, snapshot.PaperIds).Count > 0)
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
    /// FQ-14: 在 commit 入口快照 carried 成员当前在 State.Papers 中的顺序与
    /// 整个 DeepCapsuleQueueStartTopMargins 字典。异常路径用
    /// <see cref="RestoreEdgeCapsuleQueueTransferCommit"/> 完整回滚——
    /// CancelEdgeCapsuleQueueTransfer 只覆盖成员身份与源队列 margin，无力回滚
    /// PersistEdgeCapsuleQueueDropLayoutMargins 写过的目标/邻队 margin 与 State.Papers 重排。
    /// </summary>
    private EdgeCapsuleQueueTransferSnapshot CaptureEdgeCapsuleQueueTransferCommit(
        EdgeCapsuleQueueTransferSnapshot source)
    {
        return source with
        {
            // 快照**完整**顺序而非仅 carried 成员的相对顺序：InsertPaperBesideQueues 会把
            // 整块搬到目标队列的插入位，只还原相对顺序无法把成员送回原来的槽位。
            PaperOrderIds = State.Papers.Select(p => p.Id).ToList(),
            StartTopMargins = new Dictionary<string, double>(
                State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// FQ-14: 把 <see cref="CaptureEdgeCapsuleQueueTransferCommit"/> 快照写回 State——
    /// State.Papers 按 snapshot.PaperOrderIds 的完整顺序还原；DeepCapsuleQueueStartTopMargins
    /// 整体覆盖还原；成员身份随后再由 <see cref="CancelEdgeCapsuleQueueTransfer"/> 写回。
    ///
    /// 拖拽期间外部增删的成员必须保留：快照里没有的新成员按其当前相对次序追加在末尾，
    /// 已被外部删除的成员自然跳过。State.Papers 始终是新 List 实例，不会与快照共享引用。
    /// </summary>
    private void RestoreEdgeCapsuleQueueTransferCommit(
        EdgeCapsuleQueueTransferSnapshot snapshot)
    {
        if (snapshot.PaperOrderIds is { Count: > 0 })
        {
            var byId = State.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var rebuilt = new List<PaperData>(State.Papers.Count);
            foreach (var id in snapshot.PaperOrderIds)
            {
                if (byId.Remove(id, out var paper))
                {
                    rebuilt.Add(paper);
                }
            }
            // 快照里没有的成员是拖拽期间被外部新增的，保持其当前相对次序。
            foreach (var paper in State.Papers)
            {
                if (byId.ContainsKey(paper.Id))
                {
                    rebuilt.Add(paper);
                }
            }
            State.Papers = rebuilt;
        }

        if (snapshot.StartTopMargins is not null)
        {
            State.DeepCapsuleQueueStartTopMargins.Clear();
            foreach (var (key, margin) in snapshot.StartTopMargins)
            {
                State.DeepCapsuleQueueStartTopMargins[key] = margin;
            }
        }
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
        int expectedTargetIndex = -1)
    {
        if (!EdgeCapsuleQueueTransferSourceMatchesSnapshot(snapshot))
        {
            ReleaseEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        // 排除全部 carried，而不是只排除第一个：被外部移入目标队列的非首个成员
        // 既不能出现在 targetMembers 里，也不能成为 anchor。
        var targetMembers = FreeQueueMembersExcludingIds(targetQueueKey, snapshot.PaperIds);
        if (targetMembers.Count == 0)
        {
            ReleaseEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        var targetQueueId = FirstQueueIdOf(targetMembers);
        if (!EdgeCapsuleQueueIds.TryGetEdge(
                targetQueueKey,
                out var monitorName,
                out var targetSideText))
        {
            ReleaseEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        var side = DeepCapsuleSides.Normalize(targetSideText);
        var insertAt = expectedTargetIndex >= 0
            ? Math.Clamp(expectedTargetIndex, 0, targetMembers.Count)
            : DeepCapsuleQueueInsertIndexFor(
                targetQueueKey,
                snapshot.PaperIds.FirstOrDefault() ?? "",
                anchor.CapsuleCenterYDevice,
                carried.Count,
                snapshot.PaperIds);

        // 先求完整布局，再改成员；空间不足时恢复整队快照，不留下半完成的合并。
        var layoutPlan = EvaluateEdgeCapsuleQueueDropLayout(carried, targetQueueKey, insertAt);
        if (!layoutPlan.Feasible)
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        // FQ-14: 在所有 mutation 之前快照 State.Papers 顺序与全部 margin。catch 中
        // 既能回滚自身写入，也能恢复外部在拖拽期间对 State.Papers 顺序的合法修改——
        // 外部新增/删除/移动的成员会落在原本 occupied slot 之外的 new/empty 位置，
        // 回滚只覆盖 carried 成员占据的 slot，外部成员自然保留。
        var commitUndo = CaptureEdgeCapsuleQueueTransferCommit(snapshot);
        try
        {
            foreach (var member in carried)
            {
                // 身份从目标队列 key 写出：断连目标保留原显示器，不会被静默主屏几何改写。
                member.CapsuleMonitorDeviceName = monitorName;
                member.CapsuleSide = side;
                member.CapsuleQueueId = targetQueueId;
            }

            InsertPaperBesideQueues(carried, targetMembers, layoutPlan.TargetIndex);
            PersistEdgeCapsuleQueueDropLayoutMargins(layoutPlan);
            RemoveVacatedQueueStartMargin(
                snapshot, PersistentQueueKey(monitorName, side, targetQueueId));

            EdgeCapsuleQueueTransferCommitFailureInjector?.Invoke();

            ArrangeDeepCapsules(animate: true);
            RefreshTrayMenu();
            SaveNow();
            return true;
        }
        catch
        {
            // FQ-14: 异常路径同时回滚 State.Papers 顺序与全部 margin，再走身份回滚与冻结释放。
            // 返回 false 让手势层走正常「未提交」收尾（弹回原位）。
            // 不能上抛：上层 PrepareAndRunNativeDrag 的异常处理不保证事务恢复。
            RestoreEdgeCapsuleQueueTransferCommit(commitUndo);
            CancelEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }
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

        // FQ-14: 同样的事务回滚。NewQueue 分支不重排 State.Papers（carried 成员已连续），
        // 但仍会改身份与写入目标 margin，异常时必须一并回滚。
        var commitUndo = CaptureEdgeCapsuleQueueTransferCommit(snapshot);
        try
        {
            foreach (var member in carried)
            {
                member.CapsuleMonitorDeviceName = drop.MonitorDeviceName;
                member.CapsuleSide = side;
                member.CapsuleQueueId = queueId;
            }

            var destinationKey = PersistentQueueKey(drop.MonitorDeviceName, side, queueId);
            RemoveVacatedQueueStartMargin(snapshot, destinationKey);
            State.DeepCapsuleQueueStartTopMargins[destinationKey] =
                EdgeCapsuleLayout.NormalizeStartTopMargin(
                    startTopDip,
                    monitor.LocalWorkAreaDip,
                    carried.Count,
                    DeepCapsuleGap);

            EdgeCapsuleQueueTransferCommitFailureInjector?.Invoke();

            ArrangeDeepCapsules(animate: true);
            RefreshTrayMenu();
            SaveNow();
        }
        catch
        {
            RestoreEdgeCapsuleQueueTransferCommit(commitUndo);
            CancelEdgeCapsuleQueueTransfer(snapshot);
        }
    }

    /// <summary>
    /// The source queue's remembered height must not outlive it. Kept only when the queue actually
    /// kept a member, so dropping a queue back onto its own start does not erase the memory.
    /// </summary>
    private void RemoveVacatedQueueStartMargin(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        string destinationStartKey) =>
        RemoveQueueStartMarginIfVacated(
            PersistentQueueKey(snapshot.MonitorDeviceName, snapshot.Side, snapshot.QueueId),
            destinationStartKey);

    /// <summary>
    /// 提交前复核源队列的 live 成员序列。与主胶囊整队路径一致：原生拖拽会泵
    /// Dispatcher，外部操作可能已增删/隐藏/移动成员，旧快照不能覆盖新状态。
    /// </summary>
    private bool EdgeCapsuleQueueTransferSourceMatchesSnapshot(
        EdgeCapsuleQueueTransferSnapshot snapshot)
    {
        var liveIds = DeepCapsulePapersInOrder()
            .Where(paper => string.Equals(
                QueueKey(paper), snapshot.SourceQueueKey, StringComparison.Ordinal))
            .Select(paper => paper.Id);
        return liveIds.SequenceEqual(snapshot.PaperIds);
    }

    /// <summary>
    /// 校验失败时释放事务，但不回写成员身份：拖拽期间成员可能正被外部合法修改，
    /// 用旧快照回滚会覆盖这些修改。
    /// </summary>
    private void ReleaseEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot snapshot)
    {
        _edgeCapsuleQueueTransferSources.Remove(snapshot.SourceQueueKey);
        ArrangeDeepCapsules(animate: false);
    }

    /// <summary>
    /// 用户主动取消时使用：源队列 live 序列未变才回滚，否则只释放事务，
    /// 避免把拖拽期间的外部合法修改覆盖掉。异常回滚仍使用 Cancel 强制恢复。
    /// </summary>
    internal void AbandonEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            _edgeCapsuleQueueTransferSources.Clear();
            return;
        }

        if (EdgeCapsuleQueueTransferSourceMatchesSnapshot(snapshot))
        {
            CancelEdgeCapsuleQueueTransfer(snapshot);
        }
        else
        {
            ReleaseEdgeCapsuleQueueTransfer(snapshot);
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

    private void RestoreQueueStartMargin(EdgeCapsuleQueueTransferSnapshot snapshot)
    {
        var key = PersistentQueueKey(snapshot.MonitorDeviceName, snapshot.Side, snapshot.QueueId);
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
