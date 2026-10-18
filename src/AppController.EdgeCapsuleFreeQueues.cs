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
    internal string QueueKeyForPaper(PaperData paper) => QueueKey(paper);

    // The queue this drag is currently snapped to. Fed back into the magnet as its held queue so
    // the Schmitt trigger's dead band keeps a queue attached while the pointer jitters near the
    // exit threshold, instead of releasing and re-acquiring every few frames.
    private string _deepCapsuleDragHeldQueueKey = "";

    // 候选依赖 carried、目标显示器、队列成员/标题/宽度、margin、gap 与排版设置。每帧重建会
    // 重新测量所有胶囊标题，因此用手势级签名缓存：任一影响候选几何的输入变化都会重建。
    private string _deepCapsuleMagnetCandidateSignature = "";
    private List<EdgeCapsuleQueueMagnetCandidate>? _deepCapsuleMagnetCandidateCache;

    /// <summary>测试用：真正重建磁吸候选集合的次数。</summary>
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
    private void SplitCapsuleIntoQueue(
        PaperData paper,
        string monitorDeviceName,
        string side,
        double startTopDip)
    {
        var normalizedMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName);
        var normalizedSide = DeepCapsuleSides.Normalize(side);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(normalizedMonitor, out var geometry))
        {
            return;
        }

        var sourceQueueKey = InstanceQueueKey(paper);
        var sourceStillHasMembers = State.Papers.Any(candidate =>
            !string.Equals(candidate.Id, paper.Id, StringComparison.Ordinal) &&
            string.Equals(InstanceQueueKey(candidate), sourceQueueKey, StringComparison.Ordinal));

        // 同一物理边（显示器当前不可用时折叠为主屏）视为原地重建：没有剩余成员时复用
        // queue id，并保留持久 monitor tag，显示器重连后队列仍能回到原屏。
        var sourceLiveEdgeKey = EdgeKey(paper.CapsuleMonitorDeviceName, paper.CapsuleSide);
        var destinationLiveEdgeKey = EdgeKey(normalizedMonitor, normalizedSide);
        var reuseIdentity = !sourceStillHasMembers &&
            string.Equals(sourceLiveEdgeKey, destinationLiveEdgeKey, StringComparison.Ordinal);

        var destinationMonitor = reuseIdentity
            ? WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName)
            : normalizedMonitor;
        var queueId = reuseIdentity
            ? EdgeCapsuleQueueIds.Normalize(paper.CapsuleQueueId)
            : EdgeCapsuleQueueIds.New();

        paper.CapsuleMonitorDeviceName = destinationMonitor;
        paper.CapsuleSide = normalizedSide;
        paper.CapsuleQueueId = queueId;

        // Clamp at commit time only: while dragging, the capsule tracks the pointer 1:1 so the
        // drop position never feels like it lags behind the cursor.
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            startTopDip,
            geometry.LocalWorkAreaDip,
            1,
            DeepCapsuleGap);
        var destinationQueueKey = PersistentQueueKey(destinationMonitor, normalizedSide, queueId);
        State.DeepCapsuleQueueStartTopMargins[destinationQueueKey] = startTop;
        // 先写新值再清理：原地复用 id 时两个 key 相同，helper 会直接返回，不会删掉刚写入的值。
        RemoveQueueStartMarginIfVacated(sourceQueueKey, destinationQueueKey);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
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
            if (carriedIds.Contains(paper.Id) ||
                !string.Equals(LiveQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
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
                DeepCapsuleStartTopMarginForQueueKey(key),
                area,
                slotCount,
                DeepCapsuleGap);

            // The magnet measures to the queue's wall-anchored rectangle, so the band must be the
            // width this queue actually occupies: its widest resting capsule. LocalWorkAreaDip's X
            // origin is the work area's left edge, matching EdgeCapsuleGeometry's `wall` for a left
            // queue and `wall - width` for a right one.
            var bandWidth = 0.0;
            foreach (var member in papers)
            {
                if (_windows.TryGetValue(member.Id, out var memberWindow))
                {
                    bandWidth = Math.Max(bandWidth, memberWindow.DeepCapsuleRestingVisibleWidth);
                }
            }

            var bandLeft = targetSide == DeepCapsuleSides.Left
                ? 0
                : Math.Max(0, area.Width - bandWidth);
            result.Add(new EdgeCapsuleQueueMagnetCandidate(
                key,
                slotCount,
                EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap),
                slotHeight,
                -1,
                bandLeft,
                bandWidth));
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
        builder.Append(State.CapsuleTextSize).Append('\u001f');
        builder.Append(State.CapsuleTextBold).Append('\u001f');
        AppendNumber(builder, State.Zoom); AppendSeparator(builder);
        builder.Append(State.UiFontPreset).Append('\u001f')
            .Append(State.UiLanguage).Append('\u001f')
            .Append(State.TextRenderingProfile).Append('\u001f')
            .Append(State.DeepCapsuleTitleMeasureCharacterLimit).Append('\u001f')
            .Append(State.ExperimentalEdgeCapsuleHoverPreview).Append('\u001f')
            .Append(State.ShowDeepCapsuleWhileExpanded);

        foreach (var paper in carried)
        {
            builder.Append('\u001e')
                .Append(paper.Id).Append(':')
                .Append(QueueKey(paper));
        }

        var carriedIds = new HashSet<string>(carried.Select(p => p.Id), StringComparer.Ordinal);
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (carriedIds.Contains(paper.Id) ||
                !string.Equals(LiveQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
                    targetMonitor, StringComparison.Ordinal) ||
                DeepCapsuleSides.Normalize(paper.CapsuleSide) != targetSide)
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
    /// <summary>
    /// Commit a free-queue drag: decide with the magnet, then apply the resulting Reorder / Merge /
    /// NewQueue outcome. Falls back to the legacy arrangement when free mode is off.
    /// </summary>
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
                    expectedGeometry,
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
                    drop.Geometry,
                    anchor);

            default:
                SplitCapsuleIntoQueue(
                    paper,
                    drop.MonitorDeviceName,
                    drop.Side,
                    plan.NewQueueStartTopDip);
                return true;
        }
    }

    private bool TryResolveQueueGeometry(string queueKey, out MonitorGeometry geometry)
    {
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        if (separator < 0)
        {
            geometry = default;
            return false;
        }

        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
            edgeKey[..separator],
            out geometry);
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
    internal void EndDeepCapsuleQueueMagnetPreview()
    {
        _deepCapsuleDragHeldQueueKey = "";
        ClearDeepCapsuleQueueDragAvoidanceState();
        ClearDeepCapsuleQueueMagnetCandidates();
        EndEdgeCapsuleQueueDropTargetOutline();
    }

    /// <summary>Drop the drag-time hold so the next gesture starts from a clean slate.</summary>
    internal void ForgetDeepCapsuleQueueDrag()
    {
        _deepCapsuleDragHeldQueueKey = "";
        ClearDeepCapsuleQueueMagnetCandidates();
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
    /// 取整规则沿用自由队列之前的插入规则（最近槽位，数值向上取整）。
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
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(edgeKey[..Math.Max(separator, 0)], out var geometry))
        {
            return 0;
        }

        var area = geometry.LocalWorkAreaDip;
        var dropTopDip = geometry.DeviceYToLocalDip(draggedCenterDeviceY)
                         - PaperLayoutDefaults.CapsuleHeight / 2.0;
        var realCount = members.Count + Math.Max(1, carriedCount);
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueueKey(queueKey),
            area,
            realCount,
            DeepCapsuleGap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, realCount, DeepCapsuleGap);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        return Math.Clamp((int)Math.Floor((dropTopDip - firstTop) / slotHeight + 0.5), 0, members.Count);
    }

    /// <summary>Insert the dragged capsule into an existing queue at the slot nearest the drop.</summary>
    private bool MergeCapsuleIntoFreeQueue(
        PaperData paper,
        string targetQueueKey,
        MonitorGeometry targetGeometry,
        EdgeCapsuleDropAnchor anchor,
        int expectedTargetIndex = -1)
    {
        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, paper.Id);
        if (targetMembers.Count == 0)
        {
            return false;
        }

        var queueId = FirstQueueIdOf(targetMembers);
        if (!EdgeCapsuleQueueIds.TryGetEdge(
                targetQueueKey,
                out var targetMonitor,
                out var targetSideText))
        {
            targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                targetGeometry.DeviceName);
            targetSideText = DeepCapsuleSides.Right;
        }

        var targetSide = DeepCapsuleSides.Normalize(targetSideText);
        var insertAt = expectedTargetIndex >= 0
            ? Math.Clamp(expectedTargetIndex, 0, targetMembers.Count)
            : DeepCapsuleQueueInsertIndexFor(
                targetQueueKey, paper.Id, anchor.CapsuleCenterYDevice);

        // 先求完整布局，再改成员：空间不足时不能留下半完成的插入。
        var layoutPlan = EvaluateEdgeCapsuleQueueDropLayout([paper], targetQueueKey, insertAt);
        if (!layoutPlan.Feasible)
        {
            ArrangeDeepCapsules(animate: true);
            return false;
        }

        // 身份从目标队列 key 派生，而不是从解析后的窗口几何反写：断连目标队列会静默
        // 回退到主屏几何，用几何写 tag 会把成员错误地迁到主屏。同队列时保持原身份不变。
        if (!string.Equals(targetQueueKey, QueueKey(paper), StringComparison.Ordinal))
        {
            paper.CapsuleMonitorDeviceName = targetMonitor;
            paper.CapsuleSide = targetSide;
            paper.CapsuleQueueId = queueId;
        }

        InsertPaperBesideQueue(paper, targetMembers, layoutPlan.TargetIndex);
        PersistEdgeCapsuleQueueDropLayoutMargins(layoutPlan);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
        return true;
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

        // 纯重排不改变队列归属；拖拽期间若纸片已被外部移走，则不要把它强行拉回。
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

    /// <summary>Diagnostics only: how many members a queue would have besides the dragged capsule.</summary>
    internal int FreeQueueMemberCount(string queueKey, string excludedPaperId) =>
        FreeQueueMembersExcluding(queueKey, excludedPaperId).Count;

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
