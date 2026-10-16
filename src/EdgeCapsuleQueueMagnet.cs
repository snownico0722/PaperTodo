namespace PaperTodo;

/// <summary>
/// One candidate queue's vertical extent, in local DIP of the target monitor.
/// </summary>
/// <param name="QueueKey">队列实例 key。</param>
/// <param name="SlotCount">队列槽位数（含主胶囊槽位，若有）。</param>
/// <param name="FirstSlotTopDip">slot 0 的 top（已含 visualOffset 造成的偏移）。</param>
/// <param name="SlotHeightDip">相邻槽位的间距（胶囊高 + 队列间距）。</param>
/// <param name="ExcludedMemberIndex">
/// 计算区间时要排除的成员下标。源队列传入被拖动成员的槽位，使「拖离本队列」以剩余成员
/// 的实际占位来判定；-1 表示不排除。
/// </param>
internal readonly record struct EdgeCapsuleQueueMagnetCandidate(
    string QueueKey,
    int SlotCount,
    double FirstSlotTopDip,
    double SlotHeightDip,
    int ExcludedMemberIndex);

/// <summary>
/// 一次磁吸判定的全部输入。全部为本地 DIP，与 <see cref="EdgeCapsuleLayout"/> 的坐标口径一致。
/// </summary>
internal readonly record struct EdgeCapsuleQueueMagnetInput(
    double DraggedCenterTopDip,
    string SourceQueueKey,
    IReadOnlyList<EdgeCapsuleQueueMagnetCandidate> Candidates,
    double SlotHeightDip,
    double CapsuleHeightDip,
    double MergeDistanceDip,
    double DetachDistanceDip,
    string HeldQueueKey);

internal enum EdgeCapsuleQueueMagnetOutcome
{
    /// <summary>没有可作用的队列；调用方应保持现状。</summary>
    None,

    /// <summary>在源队列内重排。</summary>
    Reorder,

    /// <summary>并入另一条队列。</summary>
    Merge,

    /// <summary>脱离所有队列，自成一条新队列。</summary>
    NewQueue
}

/// <summary>
/// 判定结果。<see cref="ScoreDip"/> 是与胜出候选的区间距离，供调用方决定是否值得做视觉反馈。
/// </summary>
internal readonly record struct EdgeCapsuleQueueMagnetPlan(
    EdgeCapsuleQueueMagnetOutcome Outcome,
    string TargetQueueKey,
    double NewQueueStartTopDip,
    double ScoreDip);

/// <summary>
/// 自由队列模式的磁吸判定。纯函数，无 WPF / 窗口 / AppState 依赖。
///
/// 防抖是一个 Schmitt trigger：进入用 <c>MergeDistanceDip</c>，退出用更大的
/// <c>DetachDistanceDip</c>，两者之间是一段死区。这样「已吸附的队列不会因为指针轻微抖动而
/// 反复脱落重附」，而抖动不会在纯函数内部产生任何状态 —— 它只依赖上一帧的 held 队列，
/// 因此完全可单测，也不会因掉帧而失稳。
/// </summary>
internal static class EdgeCapsuleQueueMagnet
{
    /// <summary>开始吸附到某条队列的距离。</summary>
    internal const double DefaultMergeDistanceDip = 18;

    /// <summary>从已吸附的队列上脱落的距离。必须大于合并距离，二者之差即死区宽度。</summary>
    internal const double DefaultDetachDistanceDip = 32;

    public static EdgeCapsuleQueueMagnetPlan Evaluate(in EdgeCapsuleQueueMagnetInput input)
    {
        // A non-finite centre would make both range comparisons false, scoring every queue as a
        // perfect match, and would carry a NaN into the persisted queue start. Report "no plan"
        // so the caller leaves the layout untouched.
        if (!double.IsFinite(input.DraggedCenterTopDip))
        {
            return None;
        }

        var heightDip = double.IsFinite(input.CapsuleHeightDip) && input.CapsuleHeightDip > 0
            ? input.CapsuleHeightDip
            : 0;
        var mergeDip = double.IsFinite(input.MergeDistanceDip) && input.MergeDistanceDip >= 0
            ? input.MergeDistanceDip
            : DefaultMergeDistanceDip;
        var detachDip = double.IsFinite(input.DetachDistanceDip) && input.DetachDistanceDip >= mergeDip
            ? input.DetachDistanceDip
            : Math.Max(mergeDip, DefaultDetachDistanceDip);

        var sanitized = input with
        {
            CapsuleHeightDip = heightDip,
            MergeDistanceDip = mergeDip,
            DetachDistanceDip = detachDip
        };

        var detachedTop = sanitized.DraggedCenterTopDip - heightDip / 2.0;
        var held = Normalize(sanitized.HeldQueueKey);
        var bestKey = "";
        var bestDistance = double.MaxValue;
        var heldStillHeld = false;

        for (var i = 0; i < sanitized.Candidates.Count; i++)
        {
            var candidate = sanitized.Candidates[i];
            var key = Normalize(candidate.QueueKey);
            if (key.Length == 0)
            {
                continue;
            }

            var distance = DistanceToQueue(in sanitized, candidate);
            if (!double.IsFinite(distance))
            {
                continue;
            }

            if (key == held)
            {
                // 已吸附的队列按退出阈值衡量，避免在死区里反复脱落重附。
                if (distance > detachDip)
                {
                    held = "";
                }
                else
                {
                    heldStillHeld = true;
                }

                continue;
            }

            if (distance > mergeDip)
            {
                continue;
            }

            // 同分时按 key 序数决胜：候选集合来自 LINQ 遍历，顺序不应影响结果。
            if (distance < bestDistance ||
                (distance == bestDistance && string.CompareOrdinal(key, bestKey) < 0))
            {
                bestDistance = distance;
                bestKey = key;
            }
        }

        if (heldStillHeld)
        {
            return new EdgeCapsuleQueueMagnetPlan(
                string.Equals(held, Normalize(sanitized.SourceQueueKey), StringComparison.Ordinal)
                    ? EdgeCapsuleQueueMagnetOutcome.Reorder
                    : EdgeCapsuleQueueMagnetOutcome.Merge,
                held,
                0,
                bestDistance);
        }

        if (bestKey.Length > 0)
        {
            return new EdgeCapsuleQueueMagnetPlan(
                string.Equals(bestKey, Normalize(sanitized.SourceQueueKey), StringComparison.Ordinal)
                    ? EdgeCapsuleQueueMagnetOutcome.Reorder
                    : EdgeCapsuleQueueMagnetOutcome.Merge,
                bestKey,
                0,
                bestDistance);
        }

        return new EdgeCapsuleQueueMagnetPlan(
            EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "",
            detachedTop,
            double.MaxValue);
    }

    /// <summary>判定不出任何可用目标时返回的计划；调用方应保持当前布局不变。</summary>
    public static EdgeCapsuleQueueMagnetPlan None { get; } = new(
        EdgeCapsuleQueueMagnetOutcome.None, "", 0, double.MaxValue);

    /// <summary>
    /// 拖拽胶囊中心到候选队列整段槽位区间的一维距离。
    ///
    /// 取「到整段区间」而非「到某个具体槽」：进入队列的语义是「我靠近这一列」，
    /// 具体插入位由调用方按既有的 slot 高度公式另算。
    ///
    /// 被拖成员一旦脱离，它后面的成员会重新紧贴堆叠，所以区间按「移除后的新下标」计算，
    /// 而不是简单地挖掉一个洞 —— 否则从队列中部拖走时会把空洞误算成仍然贴得很近。
    /// 移除后队列为空（源队列只剩它自己）时返回正无穷，即任何垂直位移都已脱离。
    /// </summary>
    private static double DistanceToQueue(
        in EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate)
    {
        var slotHeight = double.IsFinite(candidate.SlotHeightDip) && candidate.SlotHeightDip > 0
            ? candidate.SlotHeightDip
            : input.SlotHeightDip;
        if (!double.IsFinite(slotHeight) || slotHeight <= 0)
        {
            return double.PositiveInfinity;
        }

        var count = candidate.SlotCount;
        if (count <= 0)
        {
            return double.PositiveInfinity;
        }

        var excluded = candidate.ExcludedMemberIndex;
        var remaining = count;
        var first = candidate.FirstSlotTopDip;
        if (!double.IsFinite(first))
        {
            return double.PositiveInfinity;
        }

        if (excluded >= 0)
        {
            if (excluded >= count)
            {
                return double.PositiveInfinity;
            }

            remaining = count - 1;
            if (remaining == 0)
            {
                return double.PositiveInfinity;
            }

            if (excluded == 0)
            {
                first += slotHeight;
            }
        }

        var last = first + (remaining - 1) * slotHeight;
        var center = input.DraggedCenterTopDip;
        var half = input.CapsuleHeightDip / 2.0;
        var low = first - half;
        var high = last + half;
        if (center < low)
        {
            return low - center;
        }

        return center > high ? center - high : 0;
    }

    private static string Normalize(string? key) => key ?? "";
}
