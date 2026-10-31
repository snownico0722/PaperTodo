namespace PaperTodo;

public sealed partial class AppController
{
    /// <summary>
    /// 当前状态下每条队列的静止避让位移。保存 margin 仍是位置偏好，delta 只是运行期派生量；
    /// 磁吸、插入求解与预览恢复共享这一份结果，避免“保存位置/显示位置”再次分叉。
    ///
    /// 使用完整当前状态计算：拖拽期间 Arrange 被 gate，屏幕上的位置就是上一次完整状态
    /// Arrange 的结果。剔除 carried 会把源队列下方队列算回未下推的位置，与屏幕不一致。
    /// delta 只取决于该队列上方队列的底边，与本队列自身成员数无关，因此搬入/搬出/收拢时
    /// 可以沿用，只需按新的槽位数重新归一化保存 margin。
    /// </summary>
    private IReadOnlyDictionary<string, double> BuildEffectiveQueueRestingDeltas()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!State.UseCapsuleFreeQueues || State.UseCapsuleCollapseAll)
        {
            return result;
        }

        var gap = DeepCapsuleGap;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(gap);
        var membersByQueue = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var queueOrder = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            var key = QueueKey(paper);
            if (!membersByQueue.TryGetValue(key, out var members))
            {
                members = new List<PaperData>();
                membersByQueue[key] = members;
                queueOrder.Add(key);
            }

            members.Add(paper);
        }

        var queues = new List<EdgeCapsuleRestingQueue>(queueOrder.Count);
        foreach (var key in queueOrder)
        {
            if (!EdgeCapsuleQueueIds.TryGetEdge(key, out var monitor, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry))
            {
                continue;
            }

            var papers = membersByQueue[key];
            var area = geometry.LocalWorkAreaDip;
            var baseTop = EdgeCapsuleLayout.TopForIndex(
                0,
                DeepCapsuleStartTopMarginForQueueKey(key),
                area,
                papers.Count,
                gap);
            var members = new List<EdgeCapsuleRestingQueueMember>(papers.Count);
            for (var index = 0; index < papers.Count; index++)
            {
                members.Add(new EdgeCapsuleRestingQueueMember(
                    papers[index].Id,
                    baseTop + (index * slotHeight)));
            }

            queues.Add(new EdgeCapsuleRestingQueue(
                LiveEdgeOfQueueKey(key),
                key,
                members,
                baseTop,
                baseTop + ((papers.Count - 1) * slotHeight) +
                    PaperLayoutDefaults.CapsuleHeight));
        }

        var offsets = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, gap);
        foreach (var queue in queues)
        {
            if (queue.Members.Count == 0)
            {
                continue;
            }

            result[queue.QueueKey] = offsets.GetValueOrDefault(queue.Members[0].PaperId);
        }

        return result;
    }

    /// <summary>
    /// 自由队列的每条队列都有自己的持久 margin，但同一物理边上多条队列可能互相重叠：
    /// 新建纸片进入默认队列，或副屏断开后队列几何回退主屏。此步骤在 preview layout 之后
    /// 按 live edge 把后一条队列整体向下推，只追加 delta，保留 preview 的成员偏移。
    /// 若某条队列正展开 hover 卡片，卡片完整高度也计入它的底边。
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

        // 展开卡片属于 hover preview 的职责，但它的底边必须参与后续队列的避让：
        // 卡片从 owner 的紧凑槽位向下生长，若只按 CapsuleHeight 计底，被静止避让下推的
        // 队列会整颗盖住下方队列。
        var previewSession = _edgeCapsulePreviewSession;
        var previewOwnerId = previewSession?.OwnerPaperId;
        var previewCardHeight = previewSession is { } session
            ? Math.Max(session.Size.HeightDip, PaperLayoutDefaults.CapsuleHeight)
            : 0;

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

                // 成员槽位仍按紧凑胶囊计算；若该成员正展开 hover 卡片，卡片完整高度
                // 也要计入队列底部，让后续队列按卡片底边避让。
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
                if (previewCardHeight > 0 &&
                    string.Equals(paper.Id, previewOwnerId, StringComparison.Ordinal))
                {
                    queueBottom = Math.Max(queueBottom, memberTop + previewCardHeight);
                }
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
