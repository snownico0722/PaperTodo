namespace PaperTodo;

/// <summary>队列某个成员在当前帧的实际顶部位置（已包含 preview 等附加偏移）。</summary>
internal readonly record struct EdgeCapsuleRestingQueueMember(string PaperId);

/// <summary>
/// 一条静止队列。LiveEdgeKey 会把断连显示器折叠到主屏同侧，因此回退队列与主屏队列
/// 进入同一条避让链；QueueKey 仍是持久身份，只用于稳定排序。
/// </summary>
internal sealed record EdgeCapsuleRestingQueue(
    string LiveEdgeKey,
    string QueueKey,
    IReadOnlyList<EdgeCapsuleRestingQueueMember> Members,
    double TopDip,
    double BottomDip,
    int SortPriority = 0);

/// <summary>
/// 自由队列静止避让策略：同一物理边上，后一条队列整体下移，直到清空前一条队列的
/// 底边 + gap。只下推、不上提，每个成员使用相同 delta 以保持队列内部节奏。
/// 纯函数，无 WPF / 控制器依赖。
/// </summary>
internal static class EdgeCapsuleRestingLayoutCoordinator
{
    private const double Epsilon = 1e-6;

    /// <summary>
    /// 计算每条队列需要的向下位移。
    /// </summary>
    /// <returns>paperId → 需要追加到 placement.TopOffsetDip 上的 delta。</returns>
    internal static IReadOnlyDictionary<string, double> BuildDownwardOffsets(
        IReadOnlyList<EdgeCapsuleRestingQueue> queues,
        double gapDip)
    {
        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        var gap = double.IsFinite(gapDip) ? Math.Max(0, gapDip) : 0;
        foreach (var group in queues
                     .Where(queue => queue.Members.Count > 0)
                     .GroupBy(queue => queue.LiveEdgeKey, StringComparer.Ordinal))
        {
            double? previousBottom = null;
            foreach (var queue in group
                         .Where(queue => double.IsFinite(queue.TopDip) &&
                                         double.IsFinite(queue.BottomDip))
                         .OrderBy(queue => queue.TopDip)
                         // 同 top 时已连接显示器优先，断连回退队列让位；最后才用 key
                         // 保证确定性，避免主屏队列因字典序反而被下推。
                         .ThenBy(queue => queue.SortPriority)
                         .ThenBy(queue => queue.QueueKey, StringComparer.Ordinal))
            {
                var delta = previousBottom is { } bottom
                    ? Math.Max(0, bottom + gap - queue.TopDip)
                    : 0;
                if (delta <= Epsilon)
                {
                    delta = 0;
                }
                if (delta > 0)
                {
                    foreach (var member in queue.Members)
                    {
                        offsets[member.PaperId] = delta;
                    }
                }

                previousBottom = queue.BottomDip + delta;
            }
        }

        return offsets;
    }
}
