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
/// 纯函数，无 WPF / 控制器依赖。
/// </summary>
internal static class EdgeCapsuleQueueInsertionSolver
{
    private const double Epsilon = 1e-6;

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
                break;
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
                break;
            }

            if (requiredTop + box.HeightDip > maxBottom + Epsilon)
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }

            tops[box.QueueKey] = requiredTop;
            cost += Math.Abs(requiredTop - box.TopDip);
            requiredTop = requiredTop + box.HeightDip + gapDip;
        }

        return new EdgeCapsuleQueueInsertSolveResult(true, tops, cost);
    }
}
