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

    /// <summary>
    /// 自由队列拖拽反馈的分支选择：贴边 Reorder 才显示同队列重排；NewQueue/None 与浮空
    /// 都应收拢源队列；整队搬移保持现状；跨队列进入开槽路径。
    /// </summary>
    private static void FreeQueuePreviewActionChecks()
    {
        static EdgeCapsuleFreeQueuePreviewAction Resolve(
            EdgeCapsuleQueueMagnetOutcome outcome,
            bool crossQueue = false,
            bool floatingPhase = false,
            bool hasTransfer = false) =>
            PaperWindow.ResolveEdgeCapsuleFreeQueuePreviewAction(
                outcome, crossQueue, floatingPhase, hasTransfer);

        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.Merge, crossQueue: true) ==
              EdgeCapsuleFreeQueuePreviewAction.CrossQueue,
            "A cross-queue merge opens the target slot");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.Merge, hasTransfer: true) ==
              EdgeCapsuleFreeQueuePreviewAction.KeepTransfer,
            "A whole-queue transfer keeps the hidden source untouched");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.Reorder) ==
              EdgeCapsuleFreeQueuePreviewAction.SameQueueReorder,
            "A docked reorder previews the new slot");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.Reorder, floatingPhase: true) ==
              EdgeCapsuleFreeQueuePreviewAction.VacateSource,
            "A floating reorder closes the source instead of previewing a slot");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.NewQueue) ==
              EdgeCapsuleFreeQueuePreviewAction.VacateSource,
            "A docked new-queue drop closes the source instead of reordering it");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.NewQueue, floatingPhase: true) ==
              EdgeCapsuleFreeQueuePreviewAction.VacateSource,
            "A floating new-queue drop closes the source");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.None) ==
              EdgeCapsuleFreeQueuePreviewAction.VacateSource,
            "An unavailable plan closes the source");
        Check(Resolve(EdgeCapsuleQueueMagnetOutcome.None, crossQueue: true) ==
              EdgeCapsuleFreeQueuePreviewAction.CrossQueue,
            "Cross-queue takes precedence over the plan outcome");
    }
}
