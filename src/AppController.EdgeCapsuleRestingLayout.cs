namespace PaperTodo;

public sealed partial class AppController
{
    /// <summary>
    /// 自由队列的每条队列都有自己的持久 margin，但同一物理边上多条队列可能互相重叠：
    /// 新建纸片进入默认队列，或副屏断开后队列几何回退主屏。此步骤在 preview layout 之后
    /// 按 live edge 把后一条队列整体向下推，只追加 delta，保留 preview 的成员偏移。
    /// 临时避让是派生量，绝不写回 <see cref="AppState.DeepCapsuleQueueStartTopMargins"/>。
    /// </summary>
    private EdgeCapsuleQueuePlan ApplyDeepCapsuleRestingAvoidance(
        EdgeCapsuleQueuePlan plan)
    {
        if (!State.UseCapsuleFreeQueues ||
            State.UseCapsuleCollapseAll ||
            plan.Queues.Count < 2)
        {
            return plan;
        }

        var gap = DeepCapsuleGap;
        var queues = new List<EdgeCapsuleRestingQueue>(plan.Queues.Count);
        foreach (var queue in plan.Queues)
        {
            if (queue.Papers.Count == 0 ||
                !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    monitor,
                    out var geometry))
            {
                continue;
            }

            var margin = DeepCapsuleStartTopMarginForQueueKey(queue.Key);
            var area = geometry.LocalWorkAreaDip;
            var members = new List<EdgeCapsuleRestingQueueMember>(queue.Papers.Count);
            var queueTop = double.PositiveInfinity;
            var queueBottom = double.NegativeInfinity;
            foreach (var paper in queue.Papers)
            {
                if (!plan.Placements.TryGetValue(paper.Id, out var rawPlacement))
                {
                    members.Clear();
                    break;
                }

                var placement = rawPlacement.Normalize();
                if (!placement.IsPlaced)
                {
                    members.Clear();
                    break;
                }

                // 常规槽位高度始终按紧凑胶囊计算；展开卡片属于 hover preview 的职责。
                var memberTop = EdgeCapsuleLayout.TopForIndex(
                    placement.VisualIndex,
                    margin,
                    area,
                    placement.SlotCount,
                    gap) + placement.TopOffsetDip;
                members.Add(new EdgeCapsuleRestingQueueMember(paper.Id, memberTop));
                queueTop = Math.Min(queueTop, memberTop);
                queueBottom = Math.Max(
                    queueBottom,
                    memberTop + PaperLayoutDefaults.CapsuleHeight);
            }

            // 缺少任一成员的 placement 时整条队列跳过，避免只移动部分成员破坏队列节奏。
            if (members.Count == 0)
            {
                continue;
            }

            queues.Add(new EdgeCapsuleRestingQueue(
                LiveEdgeOfQueueKey(queue.Key),
                queue.Key,
                members,
                queueTop,
                queueBottom));
        }

        var deltas = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, gap);
        if (deltas.Count == 0)
        {
            return plan;
        }

        // EdgeCapsuleQueuePlan 是 sealed class，不支持 with，只能重新构造。
        var placements = plan.Placements.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (paperId, delta) in deltas)
        {
            if (placements.TryGetValue(paperId, out var placement))
            {
                // 累加，不能覆盖：TopOffsetDip 里可能已有 preview layout 写入的偏移。
                placements[paperId] = placement with
                {
                    TopOffsetDip = placement.TopOffsetDip + delta
                };
            }
        }

        return new EdgeCapsuleQueuePlan(plan.Queues, placements);
    }
}
