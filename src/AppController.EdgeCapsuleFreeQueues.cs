using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// 一次拖拽的落点锚点。
///
/// Pointer 用于解析显示器、选择贴墙侧和计算水平距离；CapsuleCenterYDevice 是被拖胶囊的
/// 真实中心设备 Y。浮空阶段胶囊窗口以光标为中心，两者 Y 相同；贴边阶段胶囊顶边跟随光标，
/// 中心还要补上抓取偏移与半个胶囊高度。
/// </summary>
internal readonly record struct EdgeCapsuleDropAnchor(
    DeviceScreenPoint Pointer,
    double CapsuleCenterYDevice);

/// <summary>
/// Free edge-capsule queues (D-058).
///
/// The default arrangement lets one edge hold exactly one queue, whose height comes from a single
/// remembered start margin. Free mode keeps that arrangement intact but lets several queues share
/// an edge: a capsule dropped far from every queue starts a new one, a capsule dropped near one
/// joins it, and dragging a member away takes it out on its own.
///
/// Magnet decisions live in <see cref="EdgeCapsuleQueueMagnet"/> (pure); the edge-wide insertion
/// layout is solved by <see cref="EdgeCapsuleQueueInsertionSolver"/>. This file collects live state
/// into those inputs and applies the resulting plan.
/// </summary>
public sealed partial class AppController
{
    internal bool EdgeCapsuleFreeQueuesEnabled =>
        State.UseCapsuleFreeQueues && State.UseCapsuleMode && State.UseDeepCapsuleMode;

    /// <summary>
    /// Queue instance key of a paper, for callers outside the controller that need to tell a
    /// same-queue reorder apart from a cross-queue merge.
    /// </summary>
    internal string QueueKeyForPaper(PaperData paper) => QueueKey(paper);

    // The queue this drag is currently snapped to. Fed back into the magnet as its held queue so
    // the Schmitt trigger's dead band keeps a queue attached while the pointer jitters near the
    // exit threshold, instead of releasing and re-acquiring every few frames.
    private string _deepCapsuleDragHeldQueueKey = "";

    // FQ-12: 当前持有共享磁吸 / 描边状态的窗口纸片 id。held key、候选缓存、避让队列、描边
    // 都是 controller 级单例，只有 owner 窗口可以清理共享状态——无关纸片在隐藏 / 关闭 / 折叠
    // 等场景下调 CancelDeepCapsuleReorderDrag 早退分支，不能让它顺手把别人的描边擦掉。
    private string _edgeCapsuleQueueDragOwnerPaperId = "";

    /// <summary>当前纸片是否就是本次拖拽手势的 owner，决定它能不能清理共享状态。</summary>
    internal bool OwnsEdgeCapsuleQueueMagnetPreview(string paperId) =>
        _edgeCapsuleQueueDragOwnerPaperId.Length > 0 &&
        string.Equals(_edgeCapsuleQueueDragOwnerPaperId, paperId, StringComparison.Ordinal);

    /// <summary>在 <see cref="BeginDeepCapsuleReorderDrag"/> 内记录当前 owner。</summary>
    internal void ClaimEdgeCapsuleQueueDragOwnership(string paperId)
    {
        _edgeCapsuleQueueDragOwnerPaperId = paperId ?? "";
    }

    /// <summary>在 <see cref="CompleteDeepCapsuleReorderDrag"/> / 取消 finally 中清空 owner。</summary>
    internal void ReleaseEdgeCapsuleQueueDragOwnership()
    {
        _edgeCapsuleQueueDragOwnerPaperId = "";
    }

    // 候选依赖 carried、目标显示器、队列成员/标题/宽度、margin、gap 与排版设置。每帧重建会
    // 重新测量所有胶囊标题，因此用手势级签名缓存：任一影响候选几何的输入变化都会重建。
    private string _deepCapsuleMagnetCandidateSignature = "";
    private List<EdgeCapsuleQueueMagnetCandidate>? _deepCapsuleMagnetCandidateCache;

    /// <summary>
    /// 真正重建磁吸候选集合的次数。测试用它断言缓存命中；生产用它作为预览计划的输入版本，
    /// 候选重建后必须使同一槽位的旧预览失效。
    /// </summary>
    internal int MagnetCandidateBuildCount { get; private set; }

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
    /// Split a capsule out of its current queue into a queue resting at
    /// <paramref name="startTopDip"/>.
    ///
    /// 单成员队列在同一显示器同一侧再次放下时复用原实例 id：拖放不应改变队列身份，否则
    /// 每次拖放都会生成新 id，旧 margin 变成孤儿并持久化。源队列仍有成员（含隐藏成员）或
    /// 目的地换了边时生成新 id；源队列真正消失时清掉它记住的起始边距。
    /// </summary>
    private bool SplitCapsuleIntoQueue(
        PaperData paper,
        QueueDropGeometry drop,
        double startTopDip)
    {
        var sourceQueueKey = InstanceQueueKey(paper);
        var sourceStillHasMembers = State.Papers.Any(candidate =>
            !string.Equals(candidate.Id, paper.Id, StringComparison.Ordinal) &&
            string.Equals(InstanceQueueKey(candidate), sourceQueueKey, StringComparison.Ordinal));

        // 同一物理边（显示器当前不可用时折叠为主屏）视为原地重建：没有剩余成员时复用
        // queue id，并保留持久 monitor tag，显示器重连后队列仍能回到原屏。
        var sourceLiveEdgeKey = EdgeKey(paper.CapsuleMonitorDeviceName, paper.CapsuleSide);
        var destinationLiveEdgeKey = EdgeKey(drop.MonitorDeviceName, drop.Side);
        var reuseIdentity = !sourceStillHasMembers &&
            string.Equals(sourceLiveEdgeKey, destinationLiveEdgeKey, StringComparison.Ordinal);

        return StartCarriedQueueAt(
            [paper],
            sourceQueueKey,
            transfer: null,
            drop.MonitorDeviceName,
            drop.Side,
            drop.Geometry,
            startTopDip,
            reuseIdentity,
            paper.CapsuleMonitorDeviceName);
    }

    /// <summary>
    /// 队列的持久实例 key。margin 字典按持久实例 key 存储，不受运行期
    /// <see cref="AppState.UseCapsuleFreeQueues"/> 对 <see cref="QueueKey(PaperData)"/> 的折叠影响。
    /// </summary>
    private static string InstanceQueueKey(PaperData paper) =>
        PersistentQueueKey(paper.CapsuleMonitorDeviceName, paper.CapsuleSide, paper.CapsuleQueueId);

    /// <summary>
    /// 源队列不再有任何成员（含隐藏成员）时删除它记住的起始边距。
    /// 目的地与源 key 相同时表示队列原地复用同一实例，保留并覆盖，不删除。
    /// </summary>
    private void RemoveQueueStartMarginIfVacated(string sourceQueueKey, string destinationQueueKey)
    {
        if (string.IsNullOrEmpty(sourceQueueKey) ||
            string.Equals(sourceQueueKey, destinationQueueKey, StringComparison.Ordinal))
        {
            return;
        }

        if (!State.Papers.Any(paper => string.Equals(
                InstanceQueueKey(paper),
                sourceQueueKey,
                StringComparison.Ordinal)))
        {
            State.DeepCapsuleQueueStartTopMargins.Remove(sourceQueueKey);
        }
    }

    // 候选构建与候选签名必须使用同一套成员过滤规则，否则签名可能误判缓存有效。
    private static bool IsMagnetCandidatePaper(
        PaperData paper,
        HashSet<string> carriedIds,
        string targetMonitor,
        string targetSide) =>
        !carriedIds.Contains(paper.Id) &&
        string.Equals(
            LiveQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
            targetMonitor,
            StringComparison.Ordinal) &&
        DeepCapsuleSides.Normalize(paper.CapsuleSide) == targetSide;

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
        sourceQueueKey = carried.Count == 0 ? "" : QueueKey(carried[0]);
        var signature = BuildEdgeCapsuleQueueMagnetCandidateSignature(
            carried, targetGeometry, targetMonitor, targetSide, excludeEntireSourceQueue);
        if (_deepCapsuleMagnetCandidateCache is { } cached &&
            string.Equals(_deepCapsuleMagnetCandidateSignature, signature, StringComparison.Ordinal))
        {
            return cached;
        }

        var result = new List<EdgeCapsuleQueueMagnetCandidate>();
        var carriedIds = new HashSet<string>(carried.Select(p => p.Id), StringComparer.Ordinal);
        var members = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            // 显示器按 live 映射过滤：断连队列回退到主屏后仍是同一条物理边上的候选，
            // 但分组 key 仍是持久身份，不会与主屏队列合并。
            if (!IsMagnetCandidatePaper(paper, carriedIds, targetMonitor, targetSide))
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
        var restingDeltas = BuildEffectiveQueueRestingDeltas();
        foreach (var key in order)
        {
            var papers = members[key];
            // Free mode has no master pill, so slot 0 belongs to a real capsule.
            var slotCount = papers.Count;

            // The magnet measures to the queue's wall-anchored rectangle, so the band must be the
            // width this queue actually occupies: its widest resting capsule. LocalWorkAreaDip's X
            // origin is the work area's left edge, matching EdgeCapsuleGeometry's `wall` for a left
            // queue and `wall - width` for a right one.
            var bandWidth = MeasureEdgeCapsuleQueueWidthDip(papers);

            var bandLeft = targetSide == DeepCapsuleSides.Left
                ? 0
                : Math.Max(0, area.Width - bandWidth);

            // 当前占位与插入后占位都要叠加静止避让 delta：屏幕上的队列已经被下推，候选几何
            // 必须与显示位置一致。贴底扩容时按新的槽位数重新归一化保存 margin，再叠加 delta。
            // NewQueue 避让仍使用当前占位，因此同一个候选同时携带两套区间。
            var margin = DeepCapsuleStartTopMarginForQueueKey(key);
            var delta = restingDeltas.GetValueOrDefault(key);
            var insertionSlotCount = slotCount + carried.Count;
            result.Add(new EdgeCapsuleQueueMagnetCandidate(
                key,
                slotCount,
                EdgeCapsuleLayout.TopForIndex(0, margin, area, slotCount, DeepCapsuleGap) + delta,
                slotHeight,
                bandLeft,
                bandWidth,
                insertionSlotCount,
                EdgeCapsuleLayout.TopForIndex(
                    0, margin, area, insertionSlotCount, DeepCapsuleGap) + delta));
        }

        _deepCapsuleMagnetCandidateSignature = signature;
        _deepCapsuleMagnetCandidateCache = result;
        // 候选输入变了，描边宽度也必须重新测量，否则描边会沿用旧宽度。
        _edgeCapsuleQueueDropTargetWidthCache.Clear();
        MagnetCandidateBuildCount++;
        return result;
    }

    /// <summary>
    /// 影响磁吸候选几何的全部输入的廉价签名。不重新测量胶囊宽度，只读取窗口的
    /// <see cref="PaperWindow.CapsuleWidthRevision"/> 等版本/文本输入；任一输入变化都会
    /// 使下一次评估重建候选。
    /// </summary>
    private string BuildEdgeCapsuleQueueMagnetCandidateSignature(
        IReadOnlyList<PaperData> carried,
        MonitorGeometry targetGeometry,
        string targetMonitor,
        string targetSide,
        bool excludeEntireSourceQueue)
    {
        var builder = new StringBuilder(256);
        static void AppendNumber(StringBuilder target, double value) =>
            target.Append(value.ToString("R", CultureInfo.InvariantCulture));
        static void AppendSeparator(StringBuilder target) => target.Append('\u001f');

        builder.Append(targetMonitor).Append('\u001f')
            .Append(targetSide).Append('\u001f')
            .Append(excludeEntireSourceQueue ? '1' : '0').Append('\u001f')
            .Append(targetGeometry.DeviceName ?? "").Append('\u001f');
        AppendNumber(builder, targetGeometry.LocalWorkAreaDip.Left); AppendSeparator(builder);
        AppendNumber(builder, targetGeometry.LocalWorkAreaDip.Top); AppendSeparator(builder);
        AppendNumber(builder, targetGeometry.LocalWorkAreaDip.Width); AppendSeparator(builder);
        AppendNumber(builder, targetGeometry.LocalWorkAreaDip.Height); AppendSeparator(builder);
        AppendNumber(builder, targetGeometry.DpiScaleX); AppendSeparator(builder);
        AppendNumber(builder, targetGeometry.DpiScaleY); AppendSeparator(builder);
        AppendNumber(builder, DeepCapsuleGap); AppendSeparator(builder);
        AppendCapsuleWidthInputs(builder);
        builder.Append(State.ShowDeepCapsuleWhileExpanded);
        AppendSeparator(builder);
        builder.Append(State.UseCapsuleCollapseAll ? '1' : '0');

        foreach (var paper in carried)
        {
            builder.Append('\u001e')
                .Append(paper.Id).Append(':')
                .Append(QueueKey(paper));
        }

        var carriedIds = new HashSet<string>(carried.Select(p => p.Id), StringComparer.Ordinal);
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (!IsMagnetCandidatePaper(paper, carriedIds, targetMonitor, targetSide))
            {
                continue;
            }

            var queueKey = QueueKey(paper);
            builder.Append('\u001e')
                .Append(paper.Id).Append(':')
                .Append(queueKey).Append(':')
                .Append(EdgeCapsuleQueueIds.Normalize(paper.CapsuleQueueId)).Append(':')
                .Append(paper.IsCollapsed ? 'c' : 'e').Append(':')
                .Append(paper.Type).Append(':')
                .Append(LiveQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName)).Append(':')
                .Append(PaperCapsuleTitle(paper));
            AppendSeparator(builder);
            AppendNumber(builder, DeepCapsuleStartTopMarginForQueueKey(queueKey));
            if (_windows.TryGetValue(paper.Id, out var window))
            {
                builder.Append(":w").Append(window.CapsuleWidthRevision);
            }
            else
            {
                builder.Append(":nw");
            }
        }

        return builder.ToString();
    }

    private void ClearDeepCapsuleQueueMagnetCandidates()
    {
        _deepCapsuleMagnetCandidateCache = null;
        _deepCapsuleMagnetCandidateSignature = "";
        _edgeCapsuleQueueDropTargetWidthCache.Clear();
    }

    private static string FirstQueueIdOf(List<PaperData> papers) =>
        papers.Count == 0 ? "" : EdgeCapsuleQueueIds.Normalize(papers[0].CapsuleQueueId);

    /// <summary>
    /// Commit a free-queue drag: decide with the magnet, then apply the resulting Reorder / Merge /
    /// NewQueue outcome. Returns false when free mode is off so the caller can take its legacy path.
    /// </summary>
    /// <param name="paper">被拖拽的胶囊。</param>
    /// <param name="anchor">松手时的指针与胶囊中心锚点。</param>
    /// <param name="expectedTargetQueueKey">预览已显示的目标队列；非空且仍有效时优先按该目标提交。</param>
    /// <param name="expectedTargetIndex">预览已显示的插入槽位；小于 0 时按锚点重新计算。</param>
    /// <param name="sameQueueReorderPreviewed">
    /// 贴边阶段是否已展示同队列重排预览。浮空阶段不沿用该标记，否则会把旧槽位当成当前落点。
    /// </param>
    /// <returns>
    /// 已提交返回 true；自由队列关闭、几何无法解析、目标失效、磁吸为 None 或布局不可行时返回 false。
    /// </returns>
    internal bool CommitDeepCapsuleQueueDrop(
        PaperData paper,
        EdgeCapsuleDropAnchor anchor,
        string expectedTargetQueueKey = "",
        int expectedTargetIndex = -1,
        bool sameQueueReorderPreviewed = false)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            return false;
        }

        if (!TryResolveQueueDropGeometry(paper, anchor, out var drop))
        {
            // No monitor to resolve against: leave the arrangement untouched rather than guess.
            ArrangeDeepCapsules(animate: true);
            return false;
        }

        // 预览已经选定了一个合法目标：提交必须落到同一目标/同一槽位，不能因为清掉 held
        // 后重新评估磁吸而悄悄落到别处。目标队列若已消失，则退回磁吸重新决定。
        if (!string.IsNullOrEmpty(expectedTargetQueueKey) &&
            expectedTargetIndex >= 0 &&
            FreeQueueMembersExcluding(expectedTargetQueueKey, paper.Id).Count > 0 &&
            TryResolveQueueGeometry(expectedTargetQueueKey, out var expectedGeometry))
        {
            // 贴边同队列重排的预览只改本队列顺序；提交必须走同一条纯重排路径，
            // 不能触发整边求解器去推开/改写邻居 margin。浮空拖回源队列仍走 Merge。
            if (sameQueueReorderPreviewed)
            {
                if (string.Equals(
                        expectedTargetQueueKey,
                        QueueKey(paper),
                        StringComparison.Ordinal))
                {
                    ReorderDeepCapsuleInFreeQueue(
                        paper,
                        expectedTargetQueueKey,
                        expectedGeometry,
                        anchor,
                        expectedTargetIndex);
                    return true;
                }

                // 拖拽期间纸片被外部移走：预览目标已失效，落到下面的磁吸重算，
                // 不能用旧目标把它强行拉回去。
            }
            else
            {
                return MergeCapsuleIntoFreeQueue(
                    paper,
                    expectedTargetQueueKey,
                    anchor,
                    expectedTargetIndex);
            }
        }

        // The commit must not inherit a drag-time hold: the pointer is already released, so the
        // Schmitt trigger's exit threshold would no longer reflect anything meaningful.
        _deepCapsuleDragHeldQueueKey = "";
        var plan = EvaluateDeepCapsuleQueueDrop(paper, drop);

        switch (plan.Outcome)
        {
            case EdgeCapsuleQueueMagnetOutcome.None:
                ArrangeDeepCapsules(animate: true);
                return false;

            case EdgeCapsuleQueueMagnetOutcome.Reorder:
                ReorderDeepCapsuleInFreeQueue(paper, plan.TargetQueueKey, drop.Geometry, anchor);
                return true;

            case EdgeCapsuleQueueMagnetOutcome.Merge:
                return MergeCapsuleIntoFreeQueue(
                    paper,
                    plan.TargetQueueKey,
                    anchor);

            default:
                return SplitCapsuleIntoQueue(
                    paper,
                    drop,
                    plan.NewQueueStartTopDip);
        }
    }

    private bool TryResolveQueueGeometry(string queueKey, out MonitorGeometry geometry)
    {
        geometry = default;
        return EdgeCapsuleQueueIds.TryGetEdge(queueKey, out var monitor, out _) &&
               WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out geometry);
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
        EdgeCapsuleDropAnchor anchor,
        bool excludeEntireSourceQueue = false)
    {
        if (carried.Count == 0 ||
            !EdgeCapsuleFreeQueuesEnabled ||
            !TryResolveQueueDropGeometry(carried[0], anchor, out var drop))
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
    /// <param name="requesterPaperId">调用方纸片 id。非 owner 窗口（无关纸片隐藏 / 关闭等场景）
    /// 调用本方法会被直接拒绝——它的取消不应触碰正在被另一个窗口持有的共享状态（FQ-12）。</param>
    internal void EndDeepCapsuleQueueMagnetPreview(string? requesterPaperId)
    {
        // FQ-12: 无关窗口的普通取消会清空当前手势的 held key / 候选缓存 / 避让队列 / 描边，
        // 让真正在拖的纸片出现描边闪断。只清理 owner 的状态，其他纸片的取消必须透传。
        if (requesterPaperId == null ||
            !OwnsEdgeCapsuleQueueMagnetPreview(requesterPaperId))
        {
            return;
        }
        _edgeCapsuleQueueDragOwnerPaperId = "";
        ResetEdgeCapsuleQueueDragSharedState(closeOutline: true);
    }

    /// <summary>
    /// 手势结束时复位 controller 级共享拖拽状态；调用方负责 owner 校验（FQ-12）。
    /// 描边、布局计划、候选/测宽缓存与避让状态统一从这里清理。
    /// </summary>
    private void ResetEdgeCapsuleQueueDragSharedState(bool closeOutline)
    {
        _deepCapsuleDragHeldQueueKey = "";
        ClearDeepCapsuleQueueDragAvoidanceState();
        ClearDeepCapsuleQueueMagnetCandidates();
        if (closeOutline)
        {
            EndEdgeCapsuleQueueDropTargetOutline();
        }
    }

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

        // 新队列提交时会按实际放下的槽位数夹回工作区：单胶囊是 1，Ctrl 整队搬移是整队成员数。
        // 磁吸必须用同一上界校验，否则它验证的位置与实际提交的位置不是同一个。
        var area = drop.Geometry.LocalWorkAreaDip;
        return new EdgeCapsuleQueueMagnetInput(
            drop.CenterTopDip,
            sourceQueueKey,
            candidates,
            EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap),
            PaperLayoutDefaults.CapsuleHeight,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            heldQueueKey,
            drop.CenterLeftDip,
            EdgeCapsuleLayout.TopMargin,
            EdgeCapsuleLayout.MaxStartTopMarginForCount(
                Math.Max(1, carried.Count), area, DeepCapsuleGap),
            Math.Max(1, carried.Count));
    }

    /// <summary>
    /// 指针位置解析出的目标显示器、贴墙侧与胶囊中心（本地 DIP）。
    /// </summary>
    private readonly record struct QueueDropGeometry(
        MonitorGeometry Geometry,
        string MonitorDeviceName,
        string Side,
        double CenterTopDip,
        double CenterLeftDip);

    private bool TryResolveQueueDropGeometry(
        PaperData paper,
        EdgeCapsuleDropAnchor anchor,
        out QueueDropGeometry drop)
    {
        drop = default;
        var pointer = anchor.Pointer;
        MonitorGeometry? resolved = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            pointer,
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
            // 取目标显示器更近的物理墙。
            pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
                ? DeepCapsuleSides.Left
                : DeepCapsuleSides.Right,
            // 胶囊中心已由调用方按拖拽阶段换算好，这里不能再加半个胶囊高度。
            geometry.DeviceYToLocalDip(anchor.CapsuleCenterYDevice),
            // 浮空时胶囊以光标为中心；贴边时光标位于胶囊内，仍在同一贴墙条带附近。
            geometry.DeviceXToLocalDip(pointer.X));
        return true;
    }

    /// <summary>
    /// 被拖胶囊中心落在设备 Y <paramref name="draggedCenterDeviceY"/> 时，它在
    /// <paramref name="queueKey"/> 中会占据的槽位下标。
    ///
    /// 预览与提交共用这一函数，因此用户看到的空隙就是松手后得到的空隙。距离按「中心减半个
    /// 胶囊高度」换算成 top，抓取点位于胶囊顶部、中心还是底部都不会改变插入位。
    /// 坐标转换在目标队列自己的显示器上完成，跨屏/混合 DPI 时也能得到正确槽位。
    /// 取整规则沿用自由队列之前的插入规则（四舍五入，恰好 .5 时向上）。
    /// </summary>
    internal int DeepCapsuleQueueInsertIndexFor(
        string queueKey,
        string draggedPaperId,
        double draggedCenterDeviceY,
        int carriedCount = 1,
        IReadOnlyCollection<string>? excludedPaperIds = null)
    {
        var excluded = new HashSet<string>(
            excludedPaperIds ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(draggedPaperId))
        {
            excluded.Add(draggedPaperId);
        }

        var members = FreeQueueMembersExcludingIds(queueKey, excluded);
        // 畸形 key（无 '|'）沿用原有语义：按空显示器名回退主屏几何。
        _ = EdgeCapsuleQueueIds.TryGetEdge(queueKey, out var monitor, out _);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry))
        {
            return 0;
        }

        var area = geometry.LocalWorkAreaDip;
        var dropTopDip = geometry.DeviceYToLocalDip(draggedCenterDeviceY)
                         - PaperLayoutDefaults.CapsuleHeight / 2.0;
        var realCount = members.Count + Math.Max(1, carriedCount);
        // 队列可能已被静止避让下推；插入索引必须按屏幕上的有效位置计算。
        var firstTop = EdgeCapsuleLayout.TopForIndex(
            0,
            DeepCapsuleStartTopMarginForQueueKey(queueKey),
            area,
            realCount,
            DeepCapsuleGap) + BuildEffectiveQueueRestingDeltas().GetValueOrDefault(queueKey);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        return Math.Clamp((int)Math.Floor((dropTopDip - firstTop) / slotHeight + 0.5), 0, members.Count);
    }

    /// <summary>Insert the dragged capsule into an existing queue at the slot nearest the drop.</summary>
    private bool MergeCapsuleIntoFreeQueue(
        PaperData paper,
        string targetQueueKey,
        EdgeCapsuleDropAnchor anchor,
        int expectedTargetIndex = -1)
    {
        // 目标已空时保持旧行为：不重排，交由上层继续/弹回。
        if (FreeQueueMembersExcluding(targetQueueKey, paper.Id).Count == 0)
        {
            return false;
        }

        return MergeCarriedIntoQueue(
            [paper],
            transfer: null,
            targetQueueKey,
            anchor.CapsuleCenterYDevice,
            expectedTargetIndex,
            requireSourceMatch: false,
            animateOnReject: true);
    }

    /// <summary>Move the dragged capsule to a new slot inside the queue it is already in.</summary>
    private void ReorderDeepCapsuleInFreeQueue(
        PaperData paper,
        string queueKey,
        MonitorGeometry targetGeometry,
        EdgeCapsuleDropAnchor anchor,
        int expectedTargetIndex = -1)
    {
        var members = FreeQueueMembersExcluding(queueKey, paper.Id);
        var insertAt = expectedTargetIndex >= 0
            ? Math.Clamp(expectedTargetIndex, 0, members.Count)
            : DeepCapsuleQueueInsertIndexFor(
                queueKey, paper.Id, anchor.CapsuleCenterYDevice);

        // 正常情况下 queueKey 就是当前队列，不改归属；仅当拖拽期间纸片被外部移走（防御分支）
        // 时，才把身份重新同步到 queueKey。
        if (!string.Equals(queueKey, QueueKey(paper), StringComparison.Ordinal))
        {
            if (!EdgeCapsuleQueueIds.TryGetEdge(
                    queueKey,
                    out var targetMonitor,
                    out var targetSideText))
            {
                targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                    targetGeometry.DeviceName);
                targetSideText = DeepCapsuleSides.Right;
            }

            paper.CapsuleMonitorDeviceName = targetMonitor;
            paper.CapsuleSide = DeepCapsuleSides.Normalize(targetSideText);
            paper.CapsuleQueueId = FirstQueueIdOf(members);
        }

        InsertPaperBesideQueue(paper, members, insertAt);
        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    private List<PaperData> FreeQueueMembersExcluding(string queueKey, string excludedPaperId)
        => FreeQueueMembersExcludingIds(
            queueKey,
            string.IsNullOrEmpty(excludedPaperId)
                ? Array.Empty<string>()
                : [excludedPaperId]);

    private List<PaperData> FreeQueueMembersExcludingIds(
        string queueKey,
        IReadOnlyCollection<string> excludedPaperIds)
    {
        var excluded = excludedPaperIds as ISet<string> ??
            new HashSet<string>(excludedPaperIds, StringComparer.Ordinal);
        var members = new List<PaperData>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (!excluded.Contains(paper.Id) &&
                string.Equals(QueueKey(paper), queueKey, StringComparison.Ordinal))
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

        // targetMembers 理论上已排除 moving ids；这里仍做防御，避免 anchor 恰好是
        // carried 成员时整块被 continue 跳过、从 State.Papers 中消失。
        var stableMembers = targetMembers
            .Where(member => !movingIds.Contains(member.Id))
            .ToList();
        var stableInsertAt = targetMembers
            .Take(Math.Clamp(insertAt, 0, targetMembers.Count))
            .Count(member => !movingIds.Contains(member.Id));
        var anchorPaper = stableInsertAt < stableMembers.Count
            ? stableMembers[stableInsertAt]
            : null;

        var rebuilt = new List<PaperData>(State.Papers.Count);
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
            if (stableMembers.Count > 0)
            {
                var lastId = stableMembers[^1].Id;
                var index = rebuilt.FindIndex(p => p.Id == lastId);
                // 最后一个稳定成员可能已被外部删除；此时追加到末尾，绝不插到列表开头。
                if (index >= 0)
                {
                    rebuilt.InsertRange(index + 1, papers);
                }
                else
                {
                    rebuilt.AddRange(papers);
                }
            }
            else
            {
                rebuilt.AddRange(papers);
            }
        }

        State.Papers = rebuilt;
    }
}
