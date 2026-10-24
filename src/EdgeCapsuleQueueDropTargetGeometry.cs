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
    int CarriedCount,
    bool IsFeasible);

/// <summary>
/// 插入指示的纯几何计算。
///
/// 预览按完整槽位模拟插入结果：目标队列高度增加 carriedCount 个槽位，插入短线标记
/// “插入块的上边界”，而不是继续使用人工的小间隙。
/// </summary>
internal static class EdgeCapsuleQueueDropTargetGeometry
{
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
    /// 队列可见高度：count 个槽位，尾槽不含 gap；0 成员返回 0。预览、描边与磁吸共用。
    /// </summary>
    internal static double QueueHeightDip(
        int memberCount,
        double slotHeight,
        double gap)
    {
        var count = Math.Max(0, memberCount);
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        var normalizedGap = double.IsFinite(gap) && gap > 0 ? gap : 0;
        return Math.Max(0, count * slot - normalizedGap);
    }

    /// <summary>插入块高度：至少一个槽位，尾槽不含 gap。</summary>
    internal static double InsertionBlockHeightDip(
        int carriedCount,
        double slotHeight,
        double gap) =>
        QueueHeightDip(Math.Max(1, carriedCount), slotHeight, gap);

    /// <summary>插入后的完整高度：真实成员 + carried 槽位，尾槽不含 gap。</summary>
    internal static double TargetQueueHeightDip(
        int memberCount,
        int carriedCount,
        double slotHeight,
        double gap) =>
        QueueHeightDip(
            Math.Max(0, memberCount) + Math.Max(1, carriedCount),
            slotHeight,
            gap);

    /// <summary>
    /// 整队块高度：首颗用自身高度，后续按 slotHeight 堆叠；slotHeight 无效时退回 capsuleHeight。
    /// </summary>
    internal static double CarriedBlockHeightDip(
        int carriedCount,
        double slotHeight,
        double capsuleHeight)
    {
        var count = Math.Max(1, carriedCount);
        var height = double.IsFinite(capsuleHeight) && capsuleHeight > 0 ? capsuleHeight : 0;
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : height;
        return height + (count - 1) * Math.Max(0, slot);
    }

    /// <summary>
    /// 插入短线中心 Y：标记插入块的上边界。
    ///
    /// 队首没有上方成员，短线落在队列顶边；其余情况落在“上方成员底边”和“插入块顶边”
    /// 之间的正常 gap 中央。完整槽位由布局本身表达，不再让短线随 carriedCount 移动。
    /// </summary>
    internal static double InsertionLineCenterYDip(
        double queueTopDip,
        int targetIndex,
        double slotHeight,
        double gap)
    {
        if (!double.IsFinite(queueTopDip))
        {
            return 0;
        }

        var index = Math.Max(0, targetIndex);
        if (index == 0)
        {
            return queueTopDip;
        }

        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        var normalizedGap = double.IsFinite(gap) && gap > 0 ? gap : 0;
        return queueTopDip + index * slot - normalizedGap / 2.0;
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
