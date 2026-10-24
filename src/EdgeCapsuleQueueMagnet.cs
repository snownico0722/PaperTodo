namespace PaperTodo;

/// <summary>
/// One candidate queue's extent on the target monitor, in local DIP.
///
/// 队列在屏幕上占据一个贴墙矩形：水平方向是从墙面向内的一条固定宽度条带，垂直方向是逐槽位
/// 堆叠的区间。磁吸判定用的是拖动中心到这个矩形的距离，因此两个方向都必须参与。
/// </summary>
/// <param name="QueueKey">队列实例 key。</param>
/// <param name="SlotCount">队列当前真实槽位数（含主胶囊槽位，若有）。</param>
/// <param name="FirstSlotTopDip">
/// 当前 slot 0 的 top（已含该队列持久化的起始 margin；自由队列没有主胶囊槽位，不含 VisualOffset）。
/// </param>
/// <param name="SlotHeightDip">相邻槽位的间距（胶囊高 + 队列间距）。</param>
/// <param name="BandLeftDip">贴墙矩形水平条带的左边界（本地 DIP）。</param>
/// <param name="BandWidthDip">贴墙矩形水平条带的宽度（本地 DIP）。</param>
/// <param name="InsertionSlotCount">插入 carried 后的槽位数，必须 &gt; 0；磁吸距离只看插入后的几何。</param>
/// <param name="InsertionFirstSlotTopDip">
/// 插入后的 slot 0 top；必须与预览同值，否则贴底头插会先脱离磁吸。
/// </param>
internal readonly record struct EdgeCapsuleQueueMagnetCandidate(
    string QueueKey,
    int SlotCount,
    double FirstSlotTopDip,
    double SlotHeightDip,
    double BandLeftDip,
    double BandWidthDip,
    int InsertionSlotCount,
    double InsertionFirstSlotTopDip);

/// <summary>
/// 一次磁吸判定的全部输入。全部为本地 DIP，与 <see cref="EdgeCapsuleLayout"/> 的坐标口径一致。
/// </summary>
/// <param name="SourceQueueKey">
/// 被拖成员当前所在队列；命中该 key 时返回 Reorder，否则返回 Merge。
/// </param>
/// <param name="Candidates">
/// 目标边上参与磁吸的队列及几何。顺序必须确定：不同候选等距时，先遍历到的候选胜出。
/// </param>
/// <param name="HeldQueueKey">
/// 上一帧磁吸命中的队列；空串表示未吸附。用于 Schmitt trigger 的退出与接管判定。
/// </param>
/// <param name="MergeDistanceDip">首次吸附半径；非有限或负值时回退默认值。</param>
/// <param name="DetachDistanceDip">
/// 已吸附队列的脱落半径；小于 MergeDistanceDip 时回退默认值。调用方显式传相等值会让死区
/// 宽度变为 0。
/// </param>
/// <param name="NewQueueMinTopDip">
/// 新队列落点允许的最小 top。调用方提交时会把落点夹回工作区，所以磁吸必须用同一边界校验，
/// 否则它验证的位置与实际提交的位置不是同一个。
/// </param>
/// <param name="NewQueueMaxTopDip">新队列落点允许的最大 top。</param>
/// <param name="NewQueueSlotCount">
/// 判定为新队列时要放下的槽位数。单胶囊为 1；Ctrl 整队搬移为整队成员数。
/// 落点避让必须按整队高度校验，否则第一颗不重叠、后续成员仍可能压住下方队列。
/// </param>
internal readonly record struct EdgeCapsuleQueueMagnetInput(
    double DraggedCenterTopDip,
    string SourceQueueKey,
    IReadOnlyList<EdgeCapsuleQueueMagnetCandidate> Candidates,
    double SlotHeightDip,
    double CapsuleHeightDip,
    double MergeDistanceDip,
    double DetachDistanceDip,
    string HeldQueueKey,
    double DraggedCenterLeftDip,
    double NewQueueMinTopDip = double.NegativeInfinity,
    double NewQueueMaxTopDip = double.PositiveInfinity,
    int NewQueueSlotCount = 1);

internal enum EdgeCapsuleQueueMagnetOutcome
{
    /// <summary>
    /// 没有可作用的队列，或新建队列找不到不与现有队列重叠的合法落点；
    /// 调用方应保持现状（拒绝本次落位）。
    /// </summary>
    None,

    /// <summary>在源队列内重排。</summary>
    Reorder,

    /// <summary>并入另一条队列。</summary>
    Merge,

    /// <summary>脱离所有队列，自成一条新队列。</summary>
    NewQueue
}

/// <summary>
/// 判定结果。<see cref="ScoreDip"/> 是拖动中心到命中候选矩形的区间距离；NewQueue/None 为
/// <see cref="double.MaxValue"/>。当前生产调用方不读取它，主要由测试断言使用。
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

    /// <summary>
    /// 默认脱落距离，大于合并距离，二者之差即死区宽度。实现允许调用方显式传入等于合并距离的
    /// 值（死区为 0），但默认值必须保持大于合并距离。
    /// </summary>
    internal const double DefaultDetachDistanceDip = 32;

    /// <summary>
    /// 另一个候选要接管 held 队列所需的“明确更近”距离。只对已吸附场景生效：
    /// 新候选还必须已进入 merge 半径；首次吸附仍只按 merge 距离判定。
    /// </summary>
    internal const double DefaultTakeoverDistanceDip = 6;

    /// <summary>
    /// 对当前拖动位置做一次纯磁吸判定。不修改输入或任何全局状态；中心坐标非有限、候选几何
    /// 非法或找不到合法新队列落点时返回 <see cref="None"/>，调用方应保持布局不变。
    /// </summary>
    public static EdgeCapsuleQueueMagnetPlan Evaluate(in EdgeCapsuleQueueMagnetInput input)
    {
        // A non-finite centre would make both range comparisons false, scoring every queue as a
        // perfect match, and would carry a NaN into the persisted queue start. Report "no plan"
        // so the caller leaves the layout untouched.
        if (!double.IsFinite(input.DraggedCenterTopDip) ||
            !double.IsFinite(input.DraggedCenterLeftDip))
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
        var sourceKey = Normalize(sanitized.SourceQueueKey);
        var held = Normalize(sanitized.HeldQueueKey);
        var bestKey = "";
        var bestDistance = double.MaxValue;
        var heldStillHeld = false;
        var heldDistance = double.MaxValue;

        EdgeCapsuleQueueMagnetPlan PlanFor(string targetKey, double distance) =>
            new(
                string.Equals(targetKey, sourceKey, StringComparison.Ordinal)
                    ? EdgeCapsuleQueueMagnetOutcome.Reorder
                    : EdgeCapsuleQueueMagnetOutcome.Merge,
                targetKey,
                0,
                distance);

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
                    heldDistance = distance;
                }

                continue;
            }

            if (distance > mergeDip)
            {
                continue;
            }

            // 同分时按 key 序数决胜，使结果不依赖候选集合的遍历顺序。
            if (distance < bestDistance ||
                (distance == bestDistance && string.CompareOrdinal(key, bestKey) < 0))
            {
                bestDistance = distance;
                bestKey = key;
            }
        }

        // 已吸附队列仍保留 Schmitt 死区，但更近且已进入 merge 半径的其他候选可以接管：
        // 否则相邻队列（尤其 0 gap 邻居）永远无法成为落点。
        if (heldStillHeld &&
            bestKey.Length > 0 &&
            heldDistance - bestDistance >= DefaultTakeoverDistanceDip)
        {
            return PlanFor(bestKey, bestDistance);
        }

        if (heldStillHeld)
        {
            return PlanFor(held, heldDistance);
        }

        if (bestKey.Length > 0)
        {
            return PlanFor(bestKey, bestDistance);
        }

        // 判定为独立成队列后仍要做一次合法性校验：合并半径小于胶囊高度，落点可能压在队列上。
        // 整个合法区间都被现有队列占满时没有可提交的落点，返回 None 让调用方保持原布局，
        // 而不是接受一个必然重叠的位置。
        if (!TryResolveDetachedTopDip(in sanitized, detachedTop, out var resolvedTop))
        {
            return None;
        }

        return new EdgeCapsuleQueueMagnetPlan(
            EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "",
            resolvedTop,
            double.MaxValue);
    }

    /// <summary>
    /// 判定不出可用目标（或新建队列没有合法落点）时返回的计划；调用方应保持当前布局不变。
    /// </summary>
    public static EdgeCapsuleQueueMagnetPlan None { get; } = new(
        EdgeCapsuleQueueMagnetOutcome.None, "", 0, double.MaxValue);

    /// <summary>
    /// 拖拽胶囊中心到候选队列所占贴墙矩形的距离。
    ///
    /// 队列在屏幕上是一个矩形：水平是从墙面向内的固定宽度条带，垂直是逐槽位堆叠的区间。
    /// 两个方向合成欧氏距离，因此「水平对齐、垂直靠近」和「垂直对齐、水平靠近」的手感一致，
    /// 而停在屏幕正中间（离墙很远）不会再因为 Y 恰好对齐而被吸附。
    ///
    /// 水平对齐时水平项为 0，合成结果与只看垂直距离完全相同 —— 贴墙拖动这条主路径的手感不变。
    /// </summary>
    private static double DistanceToQueue(
        in EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate)
    {
        var horizontal = HorizontalDistance(input.DraggedCenterLeftDip, candidate);
        if (!double.IsFinite(horizontal))
        {
            return double.PositiveInfinity;
        }

        var vertical = VerticalDistance(input, candidate);
        if (!double.IsFinite(vertical))
        {
            return double.PositiveInfinity;
        }

        return horizontal <= 0 ? vertical : Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    /// <summary>
    /// 拖拽中心在水平方向到队列贴墙条带的距离；落在条带内为 0。
    ///
    /// 条带宽度无效（非有限或不大于 0）视为该候选不可达，而不是退化成「无水平约束」——
    /// 后者会让一条宽度算错的队列重新变成全场通吃，那正是本次要修掉的行为。
    /// </summary>
    private static double HorizontalDistance(double centerLeft, EdgeCapsuleQueueMagnetCandidate candidate)
    {
        if (!double.IsFinite(candidate.BandLeftDip) ||
            !double.IsFinite(candidate.BandWidthDip) ||
            candidate.BandWidthDip <= 0)
        {
            return double.PositiveInfinity;
        }

        var bandLeft = candidate.BandLeftDip;
        var bandRight = bandLeft + candidate.BandWidthDip;
        if (centerLeft < bandLeft)
        {
            return bandLeft - centerLeft;
        }

        return centerLeft > bandRight ? centerLeft - bandRight : 0;
    }

    /// <summary>
    /// 候选队列当前实际占据的垂直区间 [low, high]。生产候选已剔除 carried，
    /// 因此 SlotCount 就是剩余档位数；这里只服务 NewQueue 避让。
    /// </summary>
    private static bool TryOccupiedVerticalSpan(
        EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate,
        out double low,
        out double high)
    {
        low = 0;
        high = 0;

        var count = candidate.SlotCount;
        var first = candidate.FirstSlotTopDip;
        return count > 0 &&
            double.IsFinite(first) &&
            TrySpan(input, candidate, first, count, out low, out high);
    }

    /// <summary>
    /// 插入 carried 后的垂直区间；必须与预览同几何，否则贴底队首空位会先脱离磁吸。
    /// </summary>
    private static bool TryInsertionVerticalSpan(
        EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate,
        out double low,
        out double high)
    {
        low = 0;
        high = 0;

        var count = candidate.InsertionSlotCount;
        var first = candidate.InsertionFirstSlotTopDip;
        return count > 0 &&
            double.IsFinite(first) &&
            TrySpan(input, candidate, first, count, out low, out high);
    }

    /// <summary>
    /// 由第一个槽位的 top 与槽位数求队列的真实垂直占位：
    /// `last` 是最后一个槽位的 top，底边还要再加一个胶囊高度。
    /// </summary>
    private static bool TrySpan(
        EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate,
        double first,
        int count,
        out double low,
        out double high)
    {
        low = 0;
        high = 0;

        var slotHeight = double.IsFinite(candidate.SlotHeightDip) && candidate.SlotHeightDip > 0
            ? candidate.SlotHeightDip
            : input.SlotHeightDip;
        if (!double.IsFinite(slotHeight) ||
            slotHeight <= 0 ||
            count <= 0 ||
            !double.IsFinite(first))
        {
            return false;
        }

        var last = first + (count - 1) * slotHeight;
        low = first;
        high = last + input.CapsuleHeightDip;
        return true;
    }

    /// <summary>
    /// 拖拽胶囊到候选队列的垂直距离，按「胶囊边缘到队列边缘」计算。
    ///
    /// 输入是拖拽胶囊的中心，因此把队列的真实占位两端各外扩半个胶囊，再量中心到该区间的距离。
    /// 这等价于两颗胶囊的「边缘到边缘」距离；两端采用同一种口径，保证从队列上方和下方
    /// 靠近时触发距离严格相等。
    ///
    /// 取「到整段区间」而非「到某个具体槽」：进入队列的语义是「我靠近这一列」，
    /// 具体插入位由调用方按既有的 slot 高度公式另算。
    ///
    /// 距离使用“carried 插入后”的占位：贴底队列扩容时预览 top 会上移，磁吸必须跟着上移，
    /// 否则用户看得见队首空位却吸不住。NewQueue 避让仍使用当前真实占位（见
    /// <see cref="TryResolveDetachedTopDip"/>），避免用幽灵占位把新队列推远。
    /// </summary>
    private static double VerticalDistance(
        in EdgeCapsuleQueueMagnetInput input,
        EdgeCapsuleQueueMagnetCandidate candidate)
    {
        if (!TryInsertionVerticalSpan(input, candidate, out var low, out var high))
        {
            return double.PositiveInfinity;
        }

        var half = input.CapsuleHeightDip / 2.0;
        var reachLow = low - half;
        var reachHigh = high + half;
        var center = input.DraggedCenterTopDip;
        if (center < reachLow)
        {
            return reachLow - center;
        }

        return center > reachHigh ? center - reachHigh : 0;
    }

    /// <summary>
    /// 把「自成新队列」的落点挤到不与任何候选队列重叠的位置。
    ///
    /// 纯竖直拖拽时，合并半径（18 DIP）按边缘距离度量，判定为独立队列的落点本身不会压住
    /// 另一条队列。但斜向落点（水平分量把总距离推出半径）、Ctrl 整队搬移（整队比单胶囊高）
    /// 以及工作区夹回，都可能让落点与现有队列相交。这里按真实占位做一次最终合法性校验。
    ///
    /// 校验必须在**调用方真正会提交的坐标**下进行：提交时落点会被夹回工作区
    /// （NormalizeStartTopMargin），若在这里校验未夹的原始值，一个拖到屏幕外的落点会被判定为
    /// 「不重叠」，夹回来却正好压在贴边队列上。因此先把期望落点夹进合法区间，再校验。
    ///
    /// 只挪位置、不改归属：调用方已经判定 NewQueue，本方法绝不把它改写成合并，
    /// 避免把用户「拖远了」的意图变成入队。
    ///
    /// 候选队列已按同一侧筛选（不同侧的胶囊不在同一竖直线上，垂直避让即可），因此只需在
    /// 垂直方向上取离期望落点最近的合法位置。
    ///
    /// 返回 false 表示整个合法区间都被现有队列占满：此时无论落在哪都会重叠，调用方必须拒绝
    /// 这次落位（单胶囊回原队列、整队恢复快照），而不是提交一个视觉上叠在一起的新队列。
    /// </summary>
    private static bool TryResolveDetachedTopDip(
        in EdgeCapsuleQueueMagnetInput input,
        double desiredTop,
        out double resolvedTop)
    {
        resolvedTop = desiredTop;
        if (!double.IsFinite(desiredTop))
        {
            return false;
        }

        var height = input.CapsuleHeightDip;
        if (!double.IsFinite(height) || height <= 0)
        {
            // 退化几何没有可比较的占位，保持旧行为：原样接受落点。
            return true;
        }

        // 单胶囊时等于胶囊高度；整队搬移时是 N 个槽位叠加后的整块高度。
        var stackHeight = EdgeCapsuleQueueDropTargetGeometry.CarriedBlockHeightDip(
            input.NewQueueSlotCount,
            input.SlotHeightDip,
            height);

        var min = input.NewQueueMinTopDip;
        var max = input.NewQueueMaxTopDip;
        // min == max 也是有效边界：整队高度接近工作区高度时上界会退化为 TopMargin，
        // 此时唯一可提交的位置就是 min，必须按有界区间校验，否则会选到区间外。
        var bounded = double.IsFinite(min) && double.IsFinite(max) && min <= max;
        if (bounded)
        {
            desiredTop = Math.Clamp(desiredTop, min, max);
        }

        // 没有重叠就保持原落点，这是绝大多数拖拽的路径，必须逐位不变。
        if (!OverlapsAnyCandidate(input, desiredTop, stackHeight))
        {
            resolvedTop = desiredTop;
            return true;
        }

        var best = double.NaN;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < input.Candidates.Count; i++)
        {
            var candidate = input.Candidates[i];
            if (!TryOccupiedVerticalSpan(input, candidate, out var low, out var high))
            {
                continue;
            }

            // 紧贴该队列上方 / 下方。两条都是几何上必然不与它重叠的位置。
            TryConsider(input, high, bounded ? min : double.NegativeInfinity,
                bounded ? max : double.PositiveInfinity,
                stackHeight, desiredTop, ref best, ref bestDistance);
            TryConsider(input, low - stackHeight, bounded ? min : double.NegativeInfinity,
                bounded ? max : double.PositiveInfinity,
                stackHeight, desiredTop, ref best, ref bestDistance);
        }

        if (double.IsNaN(best))
        {
            return false;
        }

        resolvedTop = best;
        return true;
    }

    private static void TryConsider(
        in EdgeCapsuleQueueMagnetInput input,
        double top,
        double min,
        double max,
        double stackHeight,
        double desiredTop,
        ref double chosen,
        ref double chosenDistance)
    {
        // 越界的位置提交时会被夹回来，因此只有区间内的位置才是真正会被采用的落点。
        if (top < min || top > max)
        {
            return;
        }

        if (OverlapsAnyCandidate(input, top, stackHeight))
        {
            return;
        }

        var distance = Math.Abs(top - desiredTop);
        // 严格小于：同一候选内先尝试下方位置，同距时下方胜出。不同候选等距时，先遍历到的
        // 候选取胜，因此调用方必须传入确定性顺序。
        if (distance < chosenDistance)
        {
            chosen = top;
            chosenDistance = distance;
        }
    }

    private static bool OverlapsAnyCandidate(
        in EdgeCapsuleQueueMagnetInput input,
        double top,
        double stackHeight)
    {
        var bottom = top + stackHeight;
        for (var i = 0; i < input.Candidates.Count; i++)
        {
            var candidate = input.Candidates[i];
            if (TryOccupiedVerticalSpan(input, candidate, out var low, out var high) &&
                top < high &&
                bottom > low)
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string? key) => key ?? "";
}
