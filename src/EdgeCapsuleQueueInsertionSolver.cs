namespace PaperTodo;

/// <summary>
/// 一次跨队列插入求解所需的队列占位。全部为目标显示器本地 DIP。
/// </summary>
internal readonly record struct EdgeCapsuleQueueInsertBox(
    string QueueKey,
    double TopDip,
    double HeightDip);

/// <summary>
/// 整边插入求解结果。Feasible 为 false 时调用方必须拒绝插入，不得回退到“带重叠的尽力布局”。
/// </summary>
internal sealed record EdgeCapsuleQueueInsertSolveResult(
    bool Feasible,
    IReadOnlyDictionary<string, double> TopsDip,
    double TotalDisplacementDip)
{
    internal static EdgeCapsuleQueueInsertSolveResult Infeasible { get; } =
        new(false, new Dictionary<string, double>(StringComparer.Ordinal), 0);
}

/// <summary>
/// 插入时的整边队列排布求解。
///
/// 目标队列是锚点，它的 top/height 由插入结果决定。队列不跨越锚点：上方队列只能向上让位，
/// 下方队列只能向下让位。在这个约束下，最小总位移解就是“整条受影响链贴紧锚点”，因此只需
/// 从锚点向外各扫一遍，不需要通用的代价优化。
///
/// 输入允许重叠（断屏回退后生产调用方传入的有效位置仍可能相交）：保持原位的队列也会
/// 推进扫描前沿，使更远处的重叠队列继续被推开。返回可行前会再次校验整边结果不重叠。
///
/// 纯函数，无 WPF / 控制器依赖。
/// </summary>
internal static class EdgeCapsuleQueueInsertionSolver
{
    private const double Epsilon = 1e-6;

    /// <summary>
    /// 求解插入后的整边队列 top。<paramref name="boxes"/> 必须非空、包含
    /// <paramref name="anchorQueueKey"/>，且所有 top/height 有限；<paramref name="gapDip"/>
    /// 同时作为槽间距与队列间距使用。<paramref name="topMarginDip"/> 必须有限、非负，且
    /// 两倍小于工作区高度。
    ///
    /// 任一前置条件不满足时返回 <see cref="EdgeCapsuleQueueInsertSolveResult.Infeasible"/>；
    /// 调用方必须放弃插入，不能退回到“带重叠的尽力布局”。
    /// </summary>
    internal static EdgeCapsuleQueueInsertSolveResult Solve(
        IReadOnlyList<EdgeCapsuleQueueInsertBox> boxes,
        string anchorQueueKey,
        double areaHeightDip,
        double gapDip,
        double topMarginDip)
    {
        if (boxes.Count == 0 ||
            string.IsNullOrEmpty(anchorQueueKey) ||
            !double.IsFinite(areaHeightDip) || areaHeightDip <= 0 ||
            !double.IsFinite(gapDip) || gapDip < 0 ||
            !double.IsFinite(topMarginDip) ||
            topMarginDip < 0 ||
            topMarginDip * 2 >= areaHeightDip)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        foreach (var box in boxes)
        {
            if (string.IsNullOrEmpty(box.QueueKey) ||
                !double.IsFinite(box.TopDip) ||
                !double.IsFinite(box.HeightDip) ||
                box.HeightDip < 0)
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }
        }

        var sorted = new List<EdgeCapsuleQueueInsertBox>(boxes);
        sorted.Sort(static (left, right) =>
        {
            var byTop = left.TopDip.CompareTo(right.TopDip);
            return byTop != 0 ? byTop : string.CompareOrdinal(left.QueueKey, right.QueueKey);
        });

        var anchorIndex = sorted.FindIndex(
            box => string.Equals(box.QueueKey, anchorQueueKey, StringComparison.Ordinal));
        if (anchorIndex < 0)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        var anchor = sorted[anchorIndex];
        var maxBottom = areaHeightDip - topMarginDip;
        if (anchor.TopDip < topMarginDip - Epsilon ||
            anchor.TopDip + anchor.HeightDip > maxBottom + Epsilon)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        var tops = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var box in boxes)
        {
            tops[box.QueueKey] = box.TopDip;
        }

        var cost = 0.0;

        // 上方：从最近锚点的队列开始向上扫描，直到某队列已经让出空间。
        var requiredBottom = anchor.TopDip - gapDip;
        for (var index = anchorIndex - 1; index >= 0; index--)
        {
            var box = sorted[index];
            if (box.TopDip + box.HeightDip <= requiredBottom + Epsilon)
            {
                // 该队列已让出空间并保持原位，但它的 top 仍是更上方队列的下限。
                // 推进前沿而不是 break，重叠输入下更远处的队列才会继续接受检查。
                requiredBottom = Math.Min(requiredBottom, box.TopDip - gapDip);
                continue;
            }

            var newTop = requiredBottom - box.HeightDip;
            if (newTop < topMarginDip - Epsilon)
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }

            tops[box.QueueKey] = newTop;
            cost += Math.Abs(newTop - box.TopDip);
            requiredBottom = newTop - gapDip;
        }

        // 下方：从最近锚点的队列开始向下扫描，直到某队列已经让出空间。
        var requiredTop = anchor.TopDip + anchor.HeightDip + gapDip;
        for (var index = anchorIndex + 1; index < sorted.Count; index++)
        {
            var box = sorted[index];
            if (box.TopDip >= requiredTop - Epsilon)
            {
                // 该队列已让出空间并保持原位，但它的底边仍是更下方队列的上限。
                requiredTop = Math.Max(requiredTop, box.TopDip + box.HeightDip + gapDip);
                continue;
            }

            if (requiredTop + box.HeightDip > maxBottom + Epsilon)
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }

            tops[box.QueueKey] = requiredTop;
            cost += Math.Abs(requiredTop - box.TopDip);
            requiredTop = requiredTop + box.HeightDip + gapDip;
        }

        // 兜底：Feasible 必须真的兑现“整边不重叠”。允许 0-gap 接触，但拒绝真实重叠。
        // 正常路径（含重叠输入的推开）不会触发；这里只保护返回值的不变量。
        var ordered = new List<(EdgeCapsuleQueueInsertBox Box, double TopDip)>(sorted.Count);
        foreach (var box in sorted)
        {
            ordered.Add((box, tops[box.QueueKey]));
        }

        ordered.Sort(static (left, right) =>
        {
            var byTop = left.TopDip.CompareTo(right.TopDip);
            return byTop != 0
                ? byTop
                : string.CompareOrdinal(left.Box.QueueKey, right.Box.QueueKey);
        });
        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (current.TopDip < previous.TopDip + previous.Box.HeightDip - Epsilon)
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }
        }

        return new EdgeCapsuleQueueInsertSolveResult(true, tops, cost);
    }
}
