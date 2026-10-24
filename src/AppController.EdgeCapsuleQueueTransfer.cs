namespace PaperTodo;

/// <summary>
/// 单个成员在某个时间点的持久队列身份。取消/回滚时必须逐成员恢复，
/// 不能用被拖成员的字段代表整条队列（主屏切换后同一运行队列可能混用 "" 与设备名）。
/// </summary>
internal readonly record struct EdgeCapsuleQueueMemberIdentity(
    string PaperId,
    string MonitorDeviceName,
    string Side,
    string QueueId);

/// <summary>
/// 起拖时冻结的源队列事实：源 key、成员 id 与逐成员身份。
/// 提交撤销所需的顺序/margin 另见 <see cref="EdgeCapsuleQueueCommitUndo"/>。
/// </summary>
internal sealed record EdgeCapsuleQueueTransferSnapshot(
    string SourceQueueKey,
    IReadOnlyList<string> PaperIds,
    IReadOnlyList<EdgeCapsuleQueueMemberIdentity> MemberIdentities);

/// <summary>
/// 提交前捕获的撤销记录：逐成员身份、<see cref="AppState.Papers"/> 完整顺序与全部 margin。
/// 提交会把整块搬到插入位，只记相对顺序无法还原槽位。
/// </summary>
internal sealed record EdgeCapsuleQueueCommitUndo(
    IReadOnlyList<EdgeCapsuleQueueMemberIdentity> MemberIdentities,
    IReadOnlyList<string> PaperOrderIds,
    IReadOnlyDictionary<string, double> StartTopMargins);

/// <summary>
/// Whole-queue drag for free queues (D-058).
///
/// Holding Ctrl while dragging any member lifts the entire queue off the wall and drops it as a
/// single capsule, mirroring what the master pill already does for a whole edge. The source
/// queue's members are frozen for the duration so the layout cannot reflow underneath the drag.
/// </summary>
public sealed partial class AppController
{
    // 当前处于搬移中的队列实例 key。该队列的成员在手势提交或回滚前不参与布局决策。
    // TryBegin 明确拒绝第二个并发 transfer，因此单值比集合更直接地表达“至多一个源队列”。
    private string _edgeCapsuleQueueTransferSourceKey = "";

    /// <summary>
    /// 测试钩子：在整队提交的 mutation 区间中途抛异常，用于验证 FQ-14 的回滚是否完整
    /// （State.Papers 顺序 + 目标/邻队 margin + 成员身份）。生产路径恒为 null。
    /// </summary>
    internal Action? EdgeCapsuleQueueTransferCommitFailureInjector { get; set; }

    internal bool IsEdgeCapsuleQueueTransferActive =>
        _edgeCapsuleQueueTransferSourceKey.Length > 0;

    internal bool IsPaperInTransferringEdgeQueue(PaperData paper) =>
        string.Equals(
            _edgeCapsuleQueueTransferSourceKey,
            QueueKey(paper),
            StringComparison.Ordinal);

    /// <summary>
    /// Freeze the queue <paramref name="paper"/> belongs to and return everything needed to undo it.
    /// Returns false when free queues are off, nothing is docked, or a transfer is already running.
    /// </summary>
    internal bool TryBeginEdgeCapsuleQueueTransfer(
        PaperData paper,
        out EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!EdgeCapsuleFreeQueuesEnabled || _edgeCapsuleQueueTransferSourceKey.Length > 0)
        {
            return false;
        }

        var queueKey = QueueKey(paper);
        var members = FreeQueueMembersExcluding(queueKey, "");
        if (members.Count == 0)
        {
            return false;
        }

        // free 模式下 QueueKey 已是持久实例 key；快照的恢复/清理都使用它，
        // 显示器断连/重连不会把源队列重定向到主屏。
        snapshot = new EdgeCapsuleQueueTransferSnapshot(
            queueKey,
            members.Select(p => p.Id).ToList(),
            // 逐成员捕获：同一条运行队列可能混用 "" 与主屏设备名（运行中切换主屏）。
            members.Select(p => new EdgeCapsuleQueueMemberIdentity(
                p.Id,
                p.CapsuleMonitorDeviceName,
                DeepCapsuleSides.Normalize(p.CapsuleSide),
                EdgeCapsuleQueueIds.Normalize(p.CapsuleQueueId))).ToList());
        // 顺序、margin 与 mutation 前身份由 CaptureEdgeCapsuleQueueCommitUndo 在提交入口捕获；
        // 用户取消路径没有 mutation，本就无需回滚。

        _edgeCapsuleQueueTransferSourceKey = queueKey;
        return true;
    }

    /// <summary>
    /// 提交异常的唯一回滚入口：有 undo 时恢复顺序/margin/身份；只有起拖快照时只恢复身份。
    /// 普通取消应走 <see cref="AbandonEdgeCapsuleQueueTransfer"/>，否则会覆盖拖拽期间的外部修改。
    /// </summary>
    internal void RollbackEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot,
        EdgeCapsuleQueueCommitUndo? undo)
    {
        if (snapshot == null && undo == null)
        {
            return;
        }

        if (undo != null)
        {
            RestoreEdgeCapsuleQueueCommitUndo(undo);
        }
        else if (snapshot != null)
        {
            RestoreMemberIdentities(snapshot.MemberIdentities);
        }

        if (snapshot != null)
        {
            ClearEdgeCapsuleQueueTransferSource(snapshot.SourceQueueKey);
        }

        ArrangeDeepCapsules(animate: false);
    }

    private void RestoreMemberIdentities(
        IReadOnlyList<EdgeCapsuleQueueMemberIdentity> identities)
    {
        var byId = State.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (byId.TryGetValue(identity.PaperId, out var member))
            {
                member.CapsuleMonitorDeviceName = identity.MonitorDeviceName;
                member.CapsuleSide = identity.Side;
                member.CapsuleQueueId = identity.QueueId;
            }
        }
    }

    /// <summary>
    /// Commit a whole-queue drag. Decides with the same magnet a single capsule uses; the carried
    /// count additionally participates in the new-queue bounds and overlap checks, so a whole queue
    /// can be rejected or placed differently from a single capsule. On a merge the whole queue joins
    /// the target at the magnet point, keeping its internal order; with no candidate it starts a
    /// queue of its own at the drop height.
    /// </summary>
    internal bool CommitEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot,
        EdgeCapsuleDropAnchor anchor,
        string expectedTargetQueueKey = "",
        int expectedTargetIndex = -1)
    {
        if (snapshot == null)
        {
            _edgeCapsuleQueueTransferSourceKey = "";
            return false;
        }

        ClearEdgeCapsuleQueueTransferSource(snapshot.SourceQueueKey);

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
            // 尚未修改任何业务数据：只释放冻结与预览。
            AbandonEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        // 新队列使用磁吸解析后的落点，而不是原始光标：落点已按整队高度做过工作区夹取与避让，
        // 并且 top 与浮动胶囊的实际位置对齐。
        if (!TryResolveQueueDropGeometry(carried[0], anchor, out var drop))
        {
            AbandonEdgeCapsuleQueueTransfer(snapshot);
            return false;
        }

        return StartTransferredQueueAt(snapshot, carried, drop, plan.NewQueueStartTopDip);
    }

    /// <summary>
    /// 在 mutation 前捕获撤销记录。回滚点取提交前的 live 身份，而不是起拖值：
    /// 拖拽期间外部 API/插件可能合法修改成员身份。
    /// </summary>
    private EdgeCapsuleQueueCommitUndo CaptureEdgeCapsuleQueueCommitUndo(
        IReadOnlyList<PaperData> carried)
    {
        return new EdgeCapsuleQueueCommitUndo(
            carried.Select(paper => new EdgeCapsuleQueueMemberIdentity(
                paper.Id,
                paper.CapsuleMonitorDeviceName,
                DeepCapsuleSides.Normalize(paper.CapsuleSide),
                EdgeCapsuleQueueIds.Normalize(paper.CapsuleQueueId))).ToList(),
            // 记录**完整**顺序而非仅 carried 成员的相对顺序：InsertPaperBesideQueues 会把
            // 整块搬到目标队列的插入位，只还原相对顺序无法把成员送回原来的槽位。
            State.Papers.Select(p => p.Id).ToList(),
            new Dictionary<string, double>(
                State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal));
    }

    /// <summary>
    /// 把撤销记录写回 State。外部新增成员按当前相对次序追加，已删除成员跳过；
    /// State.Papers 始终是新 List，不与快照共享引用。
    /// </summary>
    private void RestoreEdgeCapsuleQueueCommitUndo(EdgeCapsuleQueueCommitUndo undo)
    {
        if (undo.PaperOrderIds.Count > 0)
        {
            var byId = State.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var rebuilt = new List<PaperData>(State.Papers.Count);
            foreach (var id in undo.PaperOrderIds)
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

        State.DeepCapsuleQueueStartTopMargins.Clear();
        foreach (var (key, margin) in undo.StartTopMargins)
        {
            State.DeepCapsuleQueueStartTopMargins[key] = margin;
        }

        RestoreMemberIdentities(undo.MemberIdentities);
    }

    /// <summary>
    /// 单颗与整队共用的“并入目标队列”提交边界：过滤目标成员、求插入位与布局、
    /// 改身份、整块插入、持久化 margin，并在异常时回滚。carried 保持传入时的内部顺序。
    /// </summary>
    /// <param name="requireSourceMatch">
    /// 整队为 true：mutation 前按 live 序列再复核一次，防止原生拖拽期间的外部修改被旧快照覆盖。
    /// </param>
    /// <param name="animateOnReject">单颗拒绝时是否播放重排动画；整队走 Abandon 的静默重排。</param>
    private bool MergeCarriedIntoQueue(
        IReadOnlyList<PaperData> carried,
        EdgeCapsuleQueueTransferSnapshot? transfer,
        string targetQueueKey,
        double anchorCenterDeviceY,
        int expectedTargetIndex,
        bool requireSourceMatch,
        bool animateOnReject)
    {
        if (carried.Count == 0)
        {
            RejectQueueMerge(transfer, animateOnReject);
            return false;
        }

        if (requireSourceMatch &&
            transfer != null &&
            !EdgeCapsuleQueueTransferSourceMatchesSnapshot(transfer))
        {
            ReleaseEdgeCapsuleQueueTransfer(transfer);
            return false;
        }

        var carriedIds = carried.Select(paper => paper.Id).ToArray();
        // 排除全部 carried：非首个成员也不能进入 targetMembers 或成为 anchor。
        var targetMembers = FreeQueueMembersExcludingIds(targetQueueKey, carriedIds);
        if (targetMembers.Count == 0)
        {
            RejectQueueMerge(transfer, animateOnReject);
            return false;
        }

        var targetQueueId = FirstQueueIdOf(targetMembers);
        if (!EdgeCapsuleQueueIds.TryGetEdge(
                targetQueueKey,
                out var monitorName,
                out var targetSideText))
        {
            RejectQueueMerge(transfer, animateOnReject);
            return false;
        }

        var side = DeepCapsuleSides.Normalize(targetSideText);
        var insertAt = expectedTargetIndex >= 0
            ? Math.Clamp(expectedTargetIndex, 0, targetMembers.Count)
            : DeepCapsuleQueueInsertIndexFor(
                targetQueueKey,
                carried[0].Id,
                anchorCenterDeviceY,
                carried.Count,
                carriedIds);

        // 先求完整布局，再改成员；空间不足时不留下半完成的插入。
        var layoutPlan = EvaluateEdgeCapsuleQueueDropLayout(carried, targetQueueKey, insertAt);
        if (!layoutPlan.Feasible)
        {
            RejectQueueMerge(transfer, animateOnReject);
            return false;
        }

        // 在所有 mutation 之前捕获撤销记录：顺序、全部 margin 与逐成员身份。
        var commitUndo = CaptureEdgeCapsuleQueueCommitUndo(carried);
        try
        {
            foreach (var member in carried)
            {
                // 目标就是成员当前队列时保持原身份（浮空拖回源队列的纯插入）。
                if (string.Equals(targetQueueKey, QueueKey(member), StringComparison.Ordinal))
                {
                    continue;
                }

                // 身份从目标队列 key 写出：断连目标保留原显示器，不会被静默主屏几何改写。
                member.CapsuleMonitorDeviceName = monitorName;
                member.CapsuleSide = side;
                member.CapsuleQueueId = targetQueueId;
            }

            InsertPaperBesideQueues(carried, targetMembers, layoutPlan.TargetIndex);
            PersistEdgeCapsuleQueueDropLayoutMargins(layoutPlan);

            EdgeCapsuleQueueTransferCommitFailureInjector?.Invoke();

            ArrangeDeepCapsules(animate: true);
            RefreshTrayMenu();
            SaveNow();
            return true;
        }
        catch
        {
            // 回滚顺序/margin/身份并释放冻结；不上抛，上层原生拖拽不保证事务恢复。
            RollbackEdgeCapsuleQueueTransfer(transfer, commitUndo);
            return false;
        }
    }

    /// <summary>尚未修改业务数据时的合并拒绝收尾。</summary>
    private void RejectQueueMerge(
        EdgeCapsuleQueueTransferSnapshot? transfer,
        bool animate)
    {
        if (transfer != null)
        {
            AbandonEdgeCapsuleQueueTransfer(transfer);
            return;
        }

        ArrangeDeepCapsules(animate);
    }

    /// <summary>整队并入目标队列的薄包装；共用提交边界见 <see cref="MergeCarriedIntoQueue"/>。</summary>
    private bool MergeTransferredQueueInto(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        string targetQueueKey,
        EdgeCapsuleDropAnchor anchor,
        int expectedTargetIndex = -1) =>
        MergeCarriedIntoQueue(
            carried,
            snapshot,
            targetQueueKey,
            anchor.CapsuleCenterYDevice,
            expectedTargetIndex,
            requireSourceMatch: true,
            animateOnReject: false);

    /// <summary>
    /// 单颗与整队共用的“自成新队列”提交边界：调用方保证落点不与现有队列重叠。
    /// 单颗在源队列消失且同边时复用原实例 id；整队恒用新 id。
    /// </summary>
    private bool StartCarriedQueueAt(
        IReadOnlyList<PaperData> carried,
        string sourceQueueKey,
        EdgeCapsuleQueueTransferSnapshot? transfer,
        string monitorDeviceName,
        string side,
        MonitorGeometry geometry,
        double startTopDip,
        bool reuseVacatedIdentity,
        string reuseMonitorDeviceName)
    {
        if (carried.Count == 0)
        {
            return false;
        }

        var queueId = reuseVacatedIdentity
            ? EdgeCapsuleQueueIds.Normalize(carried[0].CapsuleQueueId)
            : EdgeCapsuleQueueIds.New();
        var destinationMonitor = reuseVacatedIdentity
            ? WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(reuseMonitorDeviceName)
            : monitorDeviceName;
        var normalizedSide = DeepCapsuleSides.Normalize(side);
        var destinationKey = PersistentQueueKey(destinationMonitor, normalizedSide, queueId);

        var commitUndo = CaptureEdgeCapsuleQueueCommitUndo(carried);
        try
        {
            foreach (var member in carried)
            {
                member.CapsuleMonitorDeviceName = destinationMonitor;
                member.CapsuleSide = normalizedSide;
                member.CapsuleQueueId = queueId;
            }

            // 先写新值再清理：原地复用 id 时两个 key 相同，helper 会直接返回，不会删掉刚写入的值。
            State.DeepCapsuleQueueStartTopMargins[destinationKey] =
                EdgeCapsuleLayout.NormalizeStartTopMargin(
                    startTopDip,
                    geometry.LocalWorkAreaDip,
                    carried.Count,
                    DeepCapsuleGap);
            RemoveQueueStartMarginIfVacated(sourceQueueKey, destinationKey);

            EdgeCapsuleQueueTransferCommitFailureInjector?.Invoke();

            ArrangeDeepCapsules(animate: true);
            RefreshTrayMenu();
            SaveNow();
            return true;
        }
        catch
        {
            RollbackEdgeCapsuleQueueTransfer(transfer, commitUndo);
            return false;
        }
    }

    /// <summary>整队自成新队列的薄包装；共用提交边界见 <see cref="StartCarriedQueueAt"/>。</summary>
    private bool StartTransferredQueueAt(
        EdgeCapsuleQueueTransferSnapshot snapshot,
        IReadOnlyList<PaperData> carried,
        QueueDropGeometry drop,
        double startTopDip) =>
        StartCarriedQueueAt(
            carried,
            snapshot.SourceQueueKey,
            snapshot,
            drop.MonitorDeviceName,
            drop.Side,
            drop.Geometry,
            startTopDip,
            reuseVacatedIdentity: false,
            reuseMonitorDeviceName: "");

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
        ClearEdgeCapsuleQueueTransferSource(snapshot.SourceQueueKey);
        ArrangeDeepCapsules(animate: false);
    }

    /// <summary>只在传入 key 确实是当前冻结源时释放，避免误清另一个已开始的事务。</summary>
    private void ClearEdgeCapsuleQueueTransferSource(string queueKey)
    {
        if (string.Equals(
                _edgeCapsuleQueueTransferSourceKey,
                queueKey,
                StringComparison.Ordinal))
        {
            _edgeCapsuleQueueTransferSourceKey = "";
        }
    }

    /// <summary>
    /// 用户主动取消时使用：尚未修改业务数据，只释放冻结并重排。
    /// 异常回滚必须使用 <see cref="RollbackEdgeCapsuleQueueTransfer"/>。
    /// </summary>
    internal void AbandonEdgeCapsuleQueueTransfer(
        EdgeCapsuleQueueTransferSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            // TryBegin 拒绝并发 transfer，因此这里清空只会释放唯一冻结的源队列。
            _edgeCapsuleQueueTransferSourceKey = "";
            return;
        }

        ReleaseEdgeCapsuleQueueTransfer(snapshot);
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
        var key = _edgeCapsuleQueueTransferSourceKey;
        if (key.Length == 0)
        {
            return;
        }

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
