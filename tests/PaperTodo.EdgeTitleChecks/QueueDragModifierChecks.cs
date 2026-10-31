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

        // 第 5 项回归：Ctrl 整队意图只属于启用的自由队列；关闭时 Ctrl 不得改变沿边重排。
        Check(
            !PaperWindow.ShouldBeginQueueTransfer(false, mkControl, ModifierKeys.Control, true),
            "Ctrl must not latch a queue transfer while free queues are off");
        Check(
            PaperWindow.ShouldBeginQueueTransfer(true, mkControl, ModifierKeys.None, false),
            "Free queues plus the native Ctrl bit latch a queue transfer");
        Check(
            PaperWindow.ShouldBeginQueueTransfer(true, 0, ModifierKeys.None, true),
            "Free queues plus the OS Ctrl state latch a queue transfer");
        Check(
            !PaperWindow.ShouldStartFloatingReorder(
                freeQueuesEnabled: false, transferRequested: true, unlockRequested: false),
            "Ctrl alone cannot start a floating reorder while free queues are off");
        Check(
            PaperWindow.ShouldStartFloatingReorder(
                freeQueuesEnabled: false, transferRequested: false, unlockRequested: true),
            "The legacy cross-edge unlock still starts a floating reorder");
        Check(
            PaperWindow.ShouldStartFloatingReorder(
                freeQueuesEnabled: true, transferRequested: true, unlockRequested: false),
            "Free queues plus Ctrl start a floating queue transfer");

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


}
