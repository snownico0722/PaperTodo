using System.Windows;

namespace PaperTodo;

/// <summary>
/// 磁吸目标队列“插入指示”的几何输入。全部为描边窗口实际会使用的坐标口径：
/// QueueBounds / QueueEdgeLineBounds / InsertionLineBounds 是设备像素；DPI 用来把相对位置
/// 换算成窗口内 DIP。
/// </summary>
internal readonly record struct EdgeCapsuleQueueDropTargetLayout(
    DeviceScreenRect QueueBounds,
    DeviceScreenRect QueueEdgeLineBounds,
    DeviceScreenRect InsertionLineBounds,
    double DpiScaleX,
    double DpiScaleY,
    int TargetIndex,
    int CarriedCount);

/// <summary>
/// 插入指示的纯几何计算。
///
/// 预览不再为 incoming capsule 打开完整槽位，而是在插入点下方让出一个小间隙：
/// 队列底边 = 真实成员槽位高度 + 一个额外间隙。插入线居中于该间隙，因此队首、队中、队尾
/// 使用同一套公式，index 变化也不会改变目标队列底边。
/// </summary>
internal static class EdgeCapsuleQueueDropTargetGeometry
{
    /// <summary>插入点下方让出的基础额外间隙（DIP）。</summary>
    internal const double InsertionGapBaseDip = 10;

    /// <summary>Ctrl 整队拖拽时，每多携带一个胶囊追加的间隙（DIP）。</summary>
    internal const double InsertionGapPerExtraCarriedDip = 8;

    /// <summary>插入短线目标宽度（DIP），实际还会按胶囊可见宽度收窄。</summary>
    internal const double InsertionLineMaxWidthDip = 48;

    /// <summary>插入短线厚度（DIP）。</summary>
    internal const double InsertionLineThicknessDip = 3;

    /// <summary>目标队列内侧标识竖线宽度（DIP）。</summary>
    internal const double QueueEdgeLineWidthDip = 2;

    /// <summary>目标队列内侧标识竖线上下内缩（DIP）。</summary>
    internal const double QueueEdgeLineInsetDip = 2;

    /// <summary>目标队列内侧标识竖线不透明度。</summary>
    internal const double QueueEdgeLineOpacity = 0.45;

    /// <summary>
    /// 插入点下方需要让出的额外间隙：单个胶囊为 10 DIP，Ctrl 整队时每个额外胶囊再 +8 DIP。
    /// </summary>
    internal static double ExtraInsertionGapDip(int carriedCount) =>
        InsertionGapBaseDip +
        (Math.Max(1, carriedCount) - 1) * InsertionGapPerExtraCarriedDip;

    /// <summary>
    /// 预览队列高度：真实成员完整占位高度 + 插入点下方的额外间隙。
    /// 至少保留一个胶囊高度，避免 gap/参数异常时队列消失。
    /// </summary>
    internal static double PreviewQueueHeightDip(
        int memberCount,
        double slotHeight,
        double gap,
        double capsuleHeight,
        int carriedCount)
    {
        var count = Math.Max(1, memberCount);
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        var normalizedGap = double.IsFinite(gap) && gap > 0 ? gap : 0;
        var normalizedHeight = double.IsFinite(capsuleHeight) && capsuleHeight > 0
            ? capsuleHeight
            : 0;
        return Math.Max(
            normalizedHeight,
            count * slot - normalizedGap + ExtraInsertionGapDip(carriedCount));
    }

    /// <summary>
    /// 插入短线中心 Y：位于“插入点上方成员的底边”和“下方成员下移后的顶边”之间。
    ///
    /// 队首没有上方成员，间隙从队列顶边开始；队尾没有下方成员，队列底边只按额外间隙延伸，
    /// 因此三个位置共用同一公式：
    /// prevBottom = queueTop + (index - 1) * slot + capsuleHeight（index 0 时取 queueTop）
    /// nextTop    = queueTop + index * slot + extraGap（队尾再扣掉正常 gap）
    /// </summary>
    internal static double InsertionLineCenterYDip(
        double queueTopDip,
        int targetIndex,
        int memberCount,
        double slotHeight,
        double capsuleHeight,
        int carriedCount)
    {
        if (!double.IsFinite(queueTopDip))
        {
            return 0;
        }

        var count = Math.Max(0, memberCount);
        var index = Math.Clamp(targetIndex, 0, count);
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        var height = double.IsFinite(capsuleHeight) && capsuleHeight > 0 ? capsuleHeight : 0;
        var gap = Math.Max(0, slot - height);
        var prevBottom = index == 0
            ? queueTopDip
            : queueTopDip + (index - 1) * slot + height;
        // 队中/队首下方成员会整体下移 extraGap；队尾没有成员可下移，队列底边本身只延伸
        // extraGap，因此不再叠加正常 gap。
        var nextTop = queueTopDip
            + index * slot
            - (index == count ? gap : 0)
            + ExtraInsertionGapDip(carriedCount);
        return (prevBottom + nextTop) / 2.0;
    }

    /// <summary>插入短线宽度：不超过胶囊可见 body 宽度；非法宽度返回 0（窗口侧隐藏）。</summary>
    internal static double InsertionLineWidthDip(double bodyWidthDip) =>
        double.IsFinite(bodyWidthDip) && bodyWidthDip > 0
            ? Math.Min(InsertionLineMaxWidthDip, bodyWidthDip)
            : 0;

    /// <summary>
    /// 目标队列内侧标识竖线的 X（相对队列左边界）。
    ///
    /// 贴边胶囊窗口在最靠近屏幕内侧的一侧保留 <see cref="EdgeCapsuleLayout.WindowChromeMargin"/>
    /// 的透明 chrome；竖线画在这段 chrome 里、紧贴可见 body，不压住胶囊本体。
    /// </summary>
    internal static double QueueEdgeLineXDip(double queueVisibleWidthDip, bool isLeftQueue)
    {
        var width = double.IsFinite(queueVisibleWidthDip) && queueVisibleWidthDip > 0
            ? queueVisibleWidthDip
            : 0;
        var bodyWidth = Math.Max(0, width - EdgeCapsuleLayout.WindowChromeMargin);
        var x = isLeftQueue
            ? bodyWidth
            : EdgeCapsuleLayout.WindowChromeMargin - QueueEdgeLineWidthDip;
        return Math.Clamp(x, 0, Math.Max(0, width - QueueEdgeLineWidthDip));
    }

    /// <summary>
    /// 把设备矩形换算成相对描边窗口左上角的 DIP。
    ///
    /// 描边窗口本身无边框，客户区原点就是窗口左上角；调用方传入的 outlineBounds 已是
    /// 外扩 3 device px 后真正下发的位置，因此这里不能再额外扣描边 Margin。
    /// </summary>
    internal static Rect RelativeDip(
        DeviceScreenRect markerBounds,
        DeviceScreenRect outlineBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        var scaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        var scaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        return new Rect(
            (markerBounds.Left - outlineBounds.Left) / scaleX,
            (markerBounds.Top - outlineBounds.Top) / scaleY,
            markerBounds.Width / scaleX,
            markerBounds.Height / scaleY);
    }
}
