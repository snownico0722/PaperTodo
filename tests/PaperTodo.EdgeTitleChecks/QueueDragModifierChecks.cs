using System.Windows.Input;
using PaperTodo;

internal static partial class Program
{
    /// <summary>
    /// Ctrl 整队意图的三个来源必须互相兜底：宿主原生 wParam、WPF 键盘状态、
    /// OS 异步键状态。最后一个用于展开纸片持有焦点、host 不是键盘焦点窗口的场景。
    /// </summary>
    private static void QueueDragModifierLatch()
    {
        const int mkControl = 0x0008;

        Check(
            PaperWindow.IsQueueDragModifier(mkControl, ModifierKeys.None, false),
            "The native MK_CONTROL bit latches a queue drag");
        Check(
            PaperWindow.IsQueueDragModifier(0, ModifierKeys.Control, false),
            "The WPF modifier state latches a queue drag");
        Check(
            PaperWindow.IsQueueDragModifier(0, ModifierKeys.None, true),
            "The OS Ctrl state latches a queue drag when the host has no focus");
        Check(
            !PaperWindow.IsQueueDragModifier(0, ModifierKeys.None, false),
            "No Ctrl source does not latch a queue drag");
        Check(
            PaperWindow.IsQueueDragModifier(0, ModifierKeys.Control | ModifierKeys.Shift, false),
            "Ctrl+Shift still latches a queue drag");
        Check(
            !PaperWindow.IsQueueDragModifier(0, ModifierKeys.Shift, false),
            "Shift alone does not latch a queue drag");
        Check(
            PaperWindow.IsQueueDragModifier(mkControl, ModifierKeys.Control, true),
            "Multiple Ctrl sources are OR-combined");

        Check(
            !WindowNative.IsAsyncKeyDown(0),
            "A zero async key state is not down");
        Check(
            WindowNative.IsAsyncKeyDown(unchecked((short)0x8000)),
            "The high GetAsyncKeyState bit counts as down");
        Check(
            WindowNative.IsAsyncKeyDown(unchecked((short)0x8001)),
            "Sign extension keeps the high bit detectable");
        Check(
            !WindowNative.IsAsyncKeyDown(1),
            "The low GetAsyncKeyState bit alone is not a held key");
    }

    /// <summary>
    /// 插入预览的节流：pointer move 受 8ms 间隔约束，原生拖拽定时器强制评估。
    /// </summary>
    private static void QueuePreviewThrottle()
    {
        Check(
            !PaperWindow.IsDeepCapsuleQueuePreviewDue(7.9, 8, false),
            "A pointer move inside the preview interval is skipped");
        Check(
            PaperWindow.IsDeepCapsuleQueuePreviewDue(8, 8, false),
            "A pointer move at the preview interval is evaluated");
        Check(
            PaperWindow.IsDeepCapsuleQueuePreviewDue(0, 8, true),
            "The native drag timer forces an evaluation regardless of the interval");
    }
}
