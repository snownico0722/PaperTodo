namespace PaperTodo;

/// <summary>计划中一条队列的预览成员与最终位置（目标显示器本地 DIP）。</summary>
internal sealed record EdgeCapsuleQueueDropPreview(
    string QueueKey,
    IReadOnlyList<PaperData> PreviewMembers,
    double TopDip,
    bool IsSource);

/// <summary>
/// 一次跨队列插入的完整布局计划。预览与描边共享同一份计划；提交按相同输入重新求解一次，
/// 避免复用可能已经过期的预览计划。
/// </summary>
internal sealed class EdgeCapsuleQueueDropLayoutPlan
{
    public bool Feasible { get; init; }
    public string TargetQueueKey { get; init; } = "";
    public int TargetIndex { get; init; }
    public int CarriedCount { get; init; }
    public double TargetTopDip { get; init; }
    public double TargetHeightDip { get; init; }
    public double InsertionLineCenterYDip { get; init; }
    public IReadOnlyDictionary<string, EdgeCapsuleQueueDropPreview> Queues { get; init; } =
        new Dictionary<string, EdgeCapsuleQueueDropPreview>(StringComparer.Ordinal);
    public string MonitorDeviceName { get; init; } = "";
    public string Side { get; init; } = DeepCapsuleSides.Right;
    public double GapDip { get; init; }
    public string SourceQueueKey { get; init; } = "";
    public bool SourceOnTargetEdge { get; init; }

    /// <summary>
    /// 求解器实际移动过的队列（不含 target）。持久化时只写 target 与这些队列，
    /// 未移动队列的临时静止避让位移不能写回保存 margin。
    /// </summary>
    public IReadOnlySet<string> MovedQueueKeys { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

public sealed partial class AppController
{
    private EdgeCapsuleQueueDropLayoutPlan? _edgeCapsuleQueueDropLayoutPlan;

    /// <summary>
    /// 构建“把 carried 插入 targetQueueKey 的 targetIndex 处”后的整边布局计划。
    ///
    /// 目标队列按最终槽位数归一化（允许现有向上夹取），作为求解器锚点；同边其它队列由
    /// 纯求解器决定最终 top。跨边源队列只收拢、不参与目标边求解。
    /// </summary>
    private EdgeCapsuleQueueDropLayoutPlan EvaluateEdgeCapsuleQueueDropLayout(
        IReadOnlyList<PaperData> carried,
        string targetQueueKey,
        int targetIndex)
    {
        if (carried.Count == 0)
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, 0);
        }

        if (!EdgeCapsuleQueueIds.TryGetEdge(
                targetQueueKey,
                out var monitorDeviceName,
                out var sideText))
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, carried.Count);
        }

        var side = sideText == DeepCapsuleSides.Left
            ? DeepCapsuleSides.Left
            : DeepCapsuleSides.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                monitorDeviceName,
                out var geometry))
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, carried.Count);
        }

        var area = geometry.LocalWorkAreaDip;
        var gap = DeepCapsuleGap;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(gap);
        var restingDeltas = BuildEffectiveQueueRestingDeltas();
        var sourceQueueKey = QueueKey(carried[0]);
        var carriedIds = new HashSet<string>(
            carried.Select(paper => paper.Id),
            StringComparer.Ordinal);

        // 身份用持久 key，几何分组用物理 live edge：断连队列回退渲染到主屏后，
        // 必须与主屏同侧队列一起参与求解/避让，否则会视觉重叠。
        var liveTargetEdgeKey = LiveEdgeOfQueueKey(targetQueueKey);
        var membersByQueue = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var queueOrder = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            var queueKey = QueueKey(paper);
            if (!string.Equals(
                    LiveEdgeOfQueueKey(queueKey),
                    liveTargetEdgeKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!membersByQueue.TryGetValue(queueKey, out var members))
            {
                members = new List<PaperData>();
                membersByQueue[queueKey] = members;
                queueOrder.Add(queueKey);
            }

            members.Add(paper);
        }

        if (!membersByQueue.TryGetValue(targetQueueKey, out var targetAllMembers))
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, carried.Count);
        }

        var targetRealMembers = targetAllMembers
            .Where(paper => !carriedIds.Contains(paper.Id))
            .ToList();
        var clampedIndex = Math.Clamp(targetIndex, 0, targetRealMembers.Count);
        var targetPreviewMembers = new List<PaperData>(targetRealMembers);
        targetPreviewMembers.InsertRange(clampedIndex, carried);

        var sourceOnTargetEdge = string.Equals(
            LiveEdgeOfQueueKey(sourceQueueKey),
            liveTargetEdgeKey,
            StringComparison.Ordinal);

        var previews = new Dictionary<string, EdgeCapsuleQueueDropPreview>(StringComparer.Ordinal);
        var boxes = new List<EdgeCapsuleQueueInsertBox>();
        foreach (var queueKey in queueOrder)
        {
            var allMembers = membersByQueue[queueKey];
            var isTarget = string.Equals(queueKey, targetQueueKey, StringComparison.Ordinal);
            var isSource = !isTarget &&
                           string.Equals(queueKey, sourceQueueKey, StringComparison.Ordinal);

            List<PaperData> previewMembers;
            if (isTarget)
            {
                previewMembers = targetPreviewMembers;
            }
            else if (isSource)
            {
                previewMembers = allMembers
                    .Where(paper => !carriedIds.Contains(paper.Id))
                    .ToList();
            }
            else
            {
                previewMembers = new List<PaperData>(allMembers);
            }

            if (previewMembers.Count == 0)
            {
                continue;
            }

            // 求解器输入必须是屏幕上的有效静止位置，而不是保存 margin：断屏回退或队列
            // 碰撞后，后者可能让原本分开的队列在输入里重叠。
            var margin = DeepCapsuleStartTopMarginForQueueKey(queueKey);
            var top = EdgeCapsuleLayout.TopForIndex(
                0,
                margin,
                area,
                previewMembers.Count,
                gap) + restingDeltas.GetValueOrDefault(queueKey);
            var height = EdgeCapsuleQueueDropTargetGeometry.QueueHeightDip(
                previewMembers.Count,
                slotHeight,
                gap);

            previews[queueKey] = new EdgeCapsuleQueueDropPreview(
                queueKey,
                previewMembers,
                top,
                isSource);
            boxes.Add(new EdgeCapsuleQueueInsertBox(queueKey, top, height));
        }

        if (!previews.TryGetValue(targetQueueKey, out var targetPreview))
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, carried.Count);
        }

        var solved = EdgeCapsuleQueueInsertionSolver.Solve(
            boxes,
            targetQueueKey,
            area.Height,
            gap,
            EdgeCapsuleLayout.TopMargin);

        if (solved.Feasible)
        {
            var movedQueueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (queueKey, preview) in previews)
            {
                var solvedTop = solved.TopsDip[queueKey];
                // 只有被求解器真正推动的队列才需要更新持久 margin；其余队列保持原偏好。
                if (!string.Equals(queueKey, targetQueueKey, StringComparison.Ordinal) &&
                    Math.Abs(solvedTop - preview.TopDip) > 1e-6)
                {
                    movedQueueKeys.Add(queueKey);
                }

                previews[queueKey] = preview with { TopDip = solvedTop };
            }

            var targetTop = solved.TopsDip[targetQueueKey];
            var lineCenter = EdgeCapsuleQueueDropTargetGeometry.InsertionLineCenterYDip(
                targetTop,
                clampedIndex,
                slotHeight,
                gap);

            return new EdgeCapsuleQueueDropLayoutPlan
            {
                Feasible = true,
                TargetQueueKey = targetQueueKey,
                TargetIndex = clampedIndex,
                CarriedCount = carried.Count,
                TargetTopDip = targetTop,
                TargetHeightDip = EdgeCapsuleQueueDropTargetGeometry.TargetQueueHeightDip(
                    targetRealMembers.Count,
                    carried.Count,
                    slotHeight,
                    gap),
                InsertionLineCenterYDip = lineCenter,
                Queues = previews,
                MonitorDeviceName = monitorDeviceName,
                Side = side,
                GapDip = gap,
                SourceQueueKey = sourceQueueKey,
                SourceOnTargetEdge = sourceOnTargetEdge,
                MovedQueueKeys = movedQueueKeys
            };
        }

        // 不可行：描边仍要显示目标队列与警示线，因此使用目标队列的真实布局，
        // 不展示一个实际不会发生的展开结果。
        var realMargin = DeepCapsuleStartTopMarginForQueueKey(targetQueueKey);
        var realTop = EdgeCapsuleLayout.TopForIndex(
            0,
            realMargin,
            area,
            targetRealMembers.Count,
            gap) + restingDeltas.GetValueOrDefault(targetQueueKey);
        var realHeight = EdgeCapsuleQueueDropTargetGeometry.QueueHeightDip(
            targetRealMembers.Count,
            slotHeight,
            gap);
        var warningLineCenter = EdgeCapsuleQueueDropTargetGeometry.InsertionLineCenterYDip(
            realTop,
            clampedIndex,
            slotHeight,
            gap);

        return new EdgeCapsuleQueueDropLayoutPlan
        {
            Feasible = false,
            TargetQueueKey = targetQueueKey,
            TargetIndex = clampedIndex,
            CarriedCount = carried.Count,
            TargetTopDip = realTop,
            TargetHeightDip = realHeight,
            InsertionLineCenterYDip = warningLineCenter,
            MonitorDeviceName = monitorDeviceName,
            Side = side,
            GapDip = gap,
            SourceQueueKey = sourceQueueKey,
            SourceOnTargetEdge = sourceOnTargetEdge
        };
    }

    /// <summary>
    /// 把计划中的队列 top 写回持久化的起始边距。提交成功后调用；拒绝路径不得调用。
    /// </summary>
    private void PersistEdgeCapsuleQueueDropLayoutMargins(
        EdgeCapsuleQueueDropLayoutPlan plan)
    {
        if (!plan.Feasible ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                plan.MonitorDeviceName,
                out var geometry))
        {
            return;
        }

        var area = geometry.LocalWorkAreaDip;
        foreach (var (queueKey, preview) in plan.Queues)
        {
            if (preview.PreviewMembers.Count == 0)
            {
                State.DeepCapsuleQueueStartTopMargins.Remove(queueKey);
                continue;
            }

            // 该队列只是按静止避让临时下推，插入并没有移动它；不要把临时位移写回偏好。
            if (!string.Equals(queueKey, plan.TargetQueueKey, StringComparison.Ordinal) &&
                !plan.MovedQueueKeys.Contains(queueKey))
            {
                continue;
            }

            State.DeepCapsuleQueueStartTopMargins[queueKey] =
                EdgeCapsuleLayout.NormalizeStartTopMargin(
                    preview.TopDip - area.Top,
                    area,
                    preview.PreviewMembers.Count,
                    plan.GapDip);
        }

        // carried 此时已改写到目标队列；源队列不再有成员（含隐藏）时统一清理起始边距。
        RemoveQueueStartMarginIfVacated(plan.SourceQueueKey, plan.TargetQueueKey);
    }

    private static EdgeCapsuleQueueDropLayoutPlan InfeasibleDropLayoutPlan(
        string targetQueueKey,
        int targetIndex,
        int carriedCount) =>
        new()
        {
            Feasible = false,
            TargetQueueKey = targetQueueKey,
            TargetIndex = targetIndex,
            CarriedCount = carriedCount
        };
}
