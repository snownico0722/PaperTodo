using System.Windows;

namespace PaperTodo;

/// <summary>
/// 磁吸目标队列“插入占位框”的几何输入。全部为描边窗口实际会使用的坐标口径：
/// QueueBounds / PlaceholderBounds 是设备像素；DPI 用来把相对位置换算成窗口内 DIP。
/// </summary>
internal readonly record struct EdgeCapsuleQueueDropTargetLayout(
    DeviceScreenRect QueueBounds,
    DeviceScreenRect PlaceholderBounds,
    double DpiScaleX,
    double DpiScaleY,
    int TargetIndex,
    int CarriedCount);

/// <summary>
/// 插入占位框的纯几何计算。
///
/// 占位框与目标队列共用同一套槽位几何：第 index 个槽的 top = 队列首槽 top + index * 槽距；
/// 高度覆盖 carriedCount 个槽，最后一个槽不含尾部 gap。纯函数，便于独立测试。
/// </summary>
internal static class EdgeCapsuleQueueDropTargetGeometry
{
    /// <summary>占位框顶边：队列首槽 top 加上 index 个槽距。</summary>
    internal static double PlaceholderTopDip(
        double queueTopDip,
        int targetIndex,
        double slotHeight)
    {
        var index = Math.Max(0, targetIndex);
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        return queueTopDip + index * slot;
    }

    /// <summary>
    /// 占位框高度：carriedCount 个槽位叠起来，减去最后一个槽尾部不存在的 gap；
    /// 至少保留一个胶囊高度，避免 gap 异常时占位框消失。
    /// </summary>
    internal static double PlaceholderHeightDip(
        int carriedCount,
        double slotHeight,
        double gap,
        double capsuleHeight)
    {
        var count = Math.Max(1, carriedCount);
        var slot = double.IsFinite(slotHeight) && slotHeight > 0 ? slotHeight : 0;
        var normalizedGap = double.IsFinite(gap) && gap > 0 ? gap : 0;
        var normalizedHeight = double.IsFinite(capsuleHeight) && capsuleHeight > 0
            ? capsuleHeight
            : 0;
        return Math.Max(normalizedHeight, count * slot - normalizedGap);
    }

    /// <summary>
    /// 把占位框的设备矩形换算成相对描边窗口左上角的 DIP。
    ///
    /// 描边窗口本身无边框，客户区原点就是窗口左上角；调用方传入的 outlineBounds 已是
    /// 外扩 3 device px 后真正下发的位置，因此这里不能再额外扣描边 Margin。
    /// </summary>
    internal static Rect RelativeDip(
        DeviceScreenRect placeholderBounds,
        DeviceScreenRect outlineBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        var scaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        var scaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        return new Rect(
            (placeholderBounds.Left - outlineBounds.Left) / scaleX,
            (placeholderBounds.Top - outlineBounds.Top) / scaleY,
            placeholderBounds.Width / scaleX,
            placeholderBounds.Height / scaleY);
    }
}
