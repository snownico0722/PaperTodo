namespace PaperTodo;

/// <summary>计划中一条队列的预览成员与最终位置（目标显示器本地 DIP）。</summary>
internal sealed record EdgeCapsuleQueueDropPreview(
    string QueueKey,
    string QueueId,
    IReadOnlyList<PaperData> PreviewMembers,
    double TopDip,
    bool IsSource);

/// <summary>
/// 一次跨队列插入的完整布局计划。预览、描边与提交共用同一份计划，避免三者各算一套几何。
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
    public bool SourceBecomesEmpty { get; init; }
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

        var edgeKey = EdgeCapsuleQueueIds.StripInstance(targetQueueKey);
        var separator = edgeKey.LastIndexOf('|');
        if (separator < 0)
        {
            return InfeasibleDropLayoutPlan(targetQueueKey, targetIndex, carried.Count);
        }

        var monitorDeviceName = edgeKey[..separator];
        var side = edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
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

            var queueId = FirstQueueIdOf(allMembers);
            var margin = DeepCapsuleStartTopMarginForQueueKey(queueKey);
            var top = EdgeCapsuleLayout.TopForIndex(
                0,
                margin,
                area,
                previewMembers.Count,
                gap);
            var height = previewMembers.Count * slotHeight - gap;

            previews[queueKey] = new EdgeCapsuleQueueDropPreview(
                queueKey,
                queueId,
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

        // carried 尚未从源队列移除，因此必须显式排除它们再判断源队列是否会变空。
        // 使用 State.Papers 而不是可见成员：隐藏成员仍属于该队列，不能删除它的起始边距。
        var sourceBecomesEmpty = !State.Papers.Any(
            paper => !carriedIds.Contains(paper.Id) &&
                     string.Equals(QueueKey(paper), sourceQueueKey, StringComparison.Ordinal));

        if (solved.Feasible)
        {
            foreach (var (queueKey, preview) in previews)
            {
                previews[queueKey] = preview with
                {
                    TopDip = solved.TopsDip[queueKey]
                };
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
                TargetHeightDip = targetPreview.PreviewMembers.Count * slotHeight - gap,
                InsertionLineCenterYDip = lineCenter,
                Queues = previews,
                MonitorDeviceName = monitorDeviceName,
                Side = side,
                GapDip = gap,
                SourceQueueKey = sourceQueueKey,
                SourceOnTargetEdge = sourceOnTargetEdge,
                SourceBecomesEmpty = sourceBecomesEmpty
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
            gap);
        var realHeight = targetRealMembers.Count * slotHeight - gap;
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
            SourceOnTargetEdge = sourceOnTargetEdge,
            SourceBecomesEmpty = sourceBecomesEmpty
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
