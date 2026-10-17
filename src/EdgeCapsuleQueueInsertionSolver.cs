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
/// 目标队列是锚点：它的 top/height 由插入结果决定，求解器只移动其它队列。受影响的队列
/// 形成一条从锚点向外的链；每条链被转换成非递减序列后，用带边界的 L1 单调回归（PAVA +
/// 中位数）求“总位移最小”的解。这样同一条链里可以一部分下推、一部分上推，而不是每次
/// 碰撞独立选择最近方向。
///
/// 纯函数，无 WPF / 控制器依赖，便于独立测试。
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

        var anchorIndex = -1;
        for (var index = 0; index < boxes.Count; index++)
        {
            if (string.Equals(boxes[index].QueueKey, anchorQueueKey, StringComparison.Ordinal))
            {
                anchorIndex = index;
                break;
            }
        }
        if (anchorIndex < 0)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        var anchor = boxes[anchorIndex];
        var maxBottom = areaHeightDip - topMarginDip;
        if (anchor.TopDip < topMarginDip - Epsilon ||
            anchor.TopDip + anchor.HeightDip > maxBottom + Epsilon)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        var sorted = new List<EdgeCapsuleQueueInsertBox>(boxes);
        sorted.Sort(static (left, right) =>
        {
            var byTop = left.TopDip.CompareTo(right.TopDip);
            return byTop != 0 ? byTop : string.CompareOrdinal(left.QueueKey, right.QueueKey);
        });

        var sortedAnchorIndex = sorted.FindIndex(
            box => string.Equals(box.QueueKey, anchorQueueKey, StringComparison.Ordinal));
        if (sortedAnchorIndex < 0)
        {
            return EdgeCapsuleQueueInsertSolveResult.Infeasible;
        }

        var aboveAll = new List<EdgeCapsuleQueueInsertBox>(sortedAnchorIndex);
        for (var index = 0; index < sortedAnchorIndex; index++)
        {
            aboveAll.Add(sorted[index]);
        }

        var belowAll = new List<EdgeCapsuleQueueInsertBox>(sorted.Count - sortedAnchorIndex - 1);
        for (var index = sortedAnchorIndex + 1; index < sorted.Count; index++)
        {
            belowAll.Add(sorted[index]);
        }

        // 从锚点向外找出真正需要移动的队列；链外的既有布局保持原样。
        var aboveChain = BuildAboveChain(aboveAll, anchor, gapDip);
        var belowChain = BuildBelowChain(belowAll, anchor, gapDip);

        while (true)
        {
            if (!TrySolveChain(
                    aboveChain,
                    isAbove: true,
                    anchor,
                    areaHeightDip,
                    gapDip,
                    topMarginDip,
                    out var aboveTops,
                    out var aboveCost) ||
                !TrySolveChain(
                    belowChain,
                    isAbove: false,
                    anchor,
                    areaHeightDip,
                    gapDip,
                    topMarginDip,
                    out var belowTops,
                    out var belowCost))
            {
                return EdgeCapsuleQueueInsertSolveResult.Infeasible;
            }

            var expanded = false;

            // 解完后若链尾（或链首）又碰到链外下一队列，把下一队列纳入链后重新求解。
            if (aboveChain.Count > 0 && aboveChain.Count < aboveAll.Count)
            {
                var next = aboveAll[aboveAll.Count - aboveChain.Count - 1];
                var chainTop = aboveTops[aboveChain[0].QueueKey];
                if (chainTop < next.TopDip + next.HeightDip + gapDip - Epsilon)
                {
                    aboveChain.Insert(0, next);
                    expanded = true;
                }
            }

            if (belowChain.Count > 0 && belowChain.Count < belowAll.Count)
            {
                var next = belowAll[belowChain.Count];
                var last = belowChain[belowChain.Count - 1];
                var lastTop = belowTops[last.QueueKey];
                if (lastTop + last.HeightDip + gapDip > next.TopDip + Epsilon)
                {
                    belowChain.Add(next);
                    expanded = true;
                }
            }

            if (expanded)
            {
                continue;
            }

            var tops = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var box in boxes)
            {
                tops[box.QueueKey] = box.TopDip;
            }
            foreach (var (queueKey, topDip) in aboveTops)
            {
                tops[queueKey] = topDip;
            }
            foreach (var (queueKey, topDip) in belowTops)
            {
                tops[queueKey] = topDip;
            }

            return new EdgeCapsuleQueueInsertSolveResult(
                true,
                tops,
                aboveCost + belowCost);
        }
    }

    /// <summary>
    /// 上方受影响链：从最近锚点的队列开始向上找，直到某队列已经让出所需底部空间。
    /// 返回顺序保持从上到下（最远 → 最近）。
    /// </summary>
    private static List<EdgeCapsuleQueueInsertBox> BuildAboveChain(
        IReadOnlyList<EdgeCapsuleQueueInsertBox> sortedAbove,
        EdgeCapsuleQueueInsertBox anchor,
        double gapDip)
    {
        var chain = new List<EdgeCapsuleQueueInsertBox>();
        var requiredBottom = anchor.TopDip - gapDip;
        for (var index = sortedAbove.Count - 1; index >= 0; index--)
        {
            var box = sortedAbove[index];
            if (box.TopDip + box.HeightDip <= requiredBottom + Epsilon)
            {
                break;
            }

            chain.Insert(0, box);
            requiredBottom = requiredBottom - box.HeightDip - gapDip;
        }

        return chain;
    }

    /// <summary>
    /// 下方受影响链：从最近锚点的队列开始向下找，直到某队列已经让出所需顶部空间。
    /// 返回顺序保持从上到下（最近 → 最远）。
    /// </summary>
    private static List<EdgeCapsuleQueueInsertBox> BuildBelowChain(
        IReadOnlyList<EdgeCapsuleQueueInsertBox> sortedBelow,
        EdgeCapsuleQueueInsertBox anchor,
        double gapDip)
    {
        var chain = new List<EdgeCapsuleQueueInsertBox>();
        var requiredTop = anchor.TopDip + anchor.HeightDip + gapDip;
        foreach (var box in sortedBelow)
        {
            if (box.TopDip >= requiredTop - Epsilon)
            {
                break;
            }

            chain.Add(box);
            requiredTop = requiredTop + box.HeightDip + gapDip;
        }

        return chain;
    }

    /// <summary>
    /// 对一条从上到下的链求带边界的 L1 最优单调布局。
    ///
    /// 令 s_i = Σ_{j&lt;i}(height_j + gap)、y_i = x_i - s_i，则相邻约束变为 y 非递减；
    /// 目标是最小化 Σ|y_i - (oldTop_i - s_i)|。PAVA 合并违反单调的块，块值取目标值中位数
    /// 并夹到块边界内。
    /// </summary>
    private static bool TrySolveChain(
        IReadOnlyList<EdgeCapsuleQueueInsertBox> chain,
        bool isAbove,
        EdgeCapsuleQueueInsertBox anchor,
        double areaHeightDip,
        double gapDip,
        double topMarginDip,
        out Dictionary<string, double> tops,
        out double cost)
    {
        tops = new Dictionary<string, double>(StringComparer.Ordinal);
        cost = 0;
        if (chain.Count == 0)
        {
            return true;
        }

        var maxBottom = areaHeightDip - topMarginDip;
        var count = chain.Count;
        var offsets = new double[count];
        var targets = new double[count];
        var lowers = new double[count];
        var uppers = new double[count];

        var offset = 0.0;
        for (var index = 0; index < count; index++)
        {
            var box = chain[index];
            offsets[index] = offset;
            targets[index] = box.TopDip - offset;
            lowers[index] = topMarginDip - offset;
            uppers[index] = maxBottom - box.HeightDip - offset;
            offset += box.HeightDip + gapDip;
        }

        if (isAbove)
        {
            // 最近锚点的最后一项：bottom + gap 必须让到锚点上方。
            var last = count - 1;
            uppers[last] = Math.Min(
                uppers[last],
                anchor.TopDip - gapDip - chain[last].HeightDip - offsets[last]);
        }
        else
        {
            // 最近锚点的第一项：top 必须让到锚点下方。
            lowers[0] = Math.Max(lowers[0], anchor.TopDip + anchor.HeightDip + gapDip);
        }

        for (var index = 0; index < count; index++)
        {
            if (lowers[index] > uppers[index] + Epsilon)
            {
                return false;
            }
        }

        if (!TryFitMonotonic(targets, lowers, uppers, out var fitted))
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            var topDip = fitted[index] + offsets[index];
            var box = chain[index];
            if (topDip < topMarginDip - Epsilon ||
                topDip + box.HeightDip > maxBottom + Epsilon)
            {
                return false;
            }

            tops[box.QueueKey] = topDip;
            cost += Math.Abs(topDip - box.TopDip);
        }

        return true;
    }

    /// <summary>
    /// 带上下界的 L1 单调回归：求非递减 y，最小化 Σ|y_i - target_i|。
    /// 使用 PAVA：每个元素一个块，左块值大于右块值时合并，块值取中位数并夹到边界内。
    /// </summary>
    private static bool TryFitMonotonic(
        IReadOnlyList<double> targets,
        IReadOnlyList<double> lowers,
        IReadOnlyList<double> uppers,
        out double[] fitted)
    {
        fitted = [];
        var blocks = new List<Block>(targets.Count);
        for (var index = 0; index < targets.Count; index++)
        {
            var block = new Block(index, index, lowers[index], uppers[index]);
            block.Values.Add(targets[index]);
            block.Value = Clamp(Median(block.Values), block.Lower, block.Upper);
            blocks.Add(block);
        }

        while (true)
        {
            var merged = false;
            for (var index = 0; index < blocks.Count - 1; index++)
            {
                if (blocks[index].Value <= blocks[index + 1].Value + Epsilon)
                {
                    continue;
                }

                var left = blocks[index];
                var right = blocks[index + 1];
                left.End = right.End;
                left.Values.AddRange(right.Values);
                left.Lower = Math.Max(left.Lower, right.Lower);
                left.Upper = Math.Min(left.Upper, right.Upper);
                if (left.Lower > left.Upper + Epsilon)
                {
                    return false;
                }

                left.Value = Clamp(Median(left.Values), left.Lower, left.Upper);
                blocks.RemoveAt(index + 1);
                merged = true;
                break;
            }

            if (!merged)
            {
                break;
            }
        }

        fitted = new double[targets.Count];
        foreach (var block in blocks)
        {
            for (var index = block.Start; index <= block.End; index++)
            {
                fitted[index] = block.Value;
            }
        }

        return true;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[(values.Count - 1) / 2];
    }

    private static double Clamp(double value, double lower, double upper) =>
        Math.Min(Math.Max(value, lower), upper);

    private sealed class Block
    {
        internal Block(int start, int end, double lower, double upper)
        {
            Start = start;
            End = end;
            Lower = lower;
            Upper = upper;
        }

        internal int Start { get; }
        internal int End { get; set; }
        internal double Lower { get; set; }
        internal double Upper { get; set; }
        internal double Value { get; set; }
        internal List<double> Values { get; } = new();
    }
}
