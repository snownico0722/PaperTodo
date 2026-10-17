using System.Diagnostics;
using System.Reflection;
using PaperTodo;

/// <summary>
/// 拖拽期“目标队列开槽”预览的恢复检查。
///
/// 磁吸进入目标队列时，目标成员会被推下去让出插入空隙；离开吸附区后必须能按真实成员顺序
/// 放回。这里直接调用控制器原语验证 placement，不需要驱动真实指针或等待动画。
/// </summary>
internal static class EdgeCapsuleQueuePreviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "free-queue preview fixture slots did not become visible");

        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var targetPaper = controller.State.Papers.Single(paper => paper.Id == "fixture-2");
        var targetKey = AppController.QueueKeyForPaper(targetPaper);

        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-3"]).Index == 1,
            "target queue did not start with its true slot order");

        // 源队列 qa 由 [0, 1] 撤走 0 后收拢为 [1]；目标 qb 由 [2, 3] 插入 carried 后变成 [2, 0, 3]。
        controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1);
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 3,
            "preview did not keep the first target member in place");
        Require(
            Placement(windows["fixture-3"]).Index == 2 &&
            Placement(windows["fixture-3"]).SlotCount == 3,
            "preview did not open a slot for the carried capsule");
        Require(
            Placement(windows["fixture-1"]).Index == 0,
            "preview did not close the source queue's vacated slot");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip - 20) < 1e-6,
            "the queue below the target did not avoid the inserted slot");

        controller.RestoreDeepCapsuleQueuePreview(targetKey);
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 2,
            "restore did not shrink the target queue back to its real slot count");
        Require(
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 2,
            "restore left the target queue's opened gap behind");
        Require(
            Placement(windows["fixture-1"]).Index == 0,
            "restore disturbed the collapsed source queue");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "restore left the neighbouring queue displaced");

        // 重新进入同一目标必须能再次开槽；交互层若残留 preview key，会在这里暴露。
        controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1);
        Require(
            Placement(windows["fixture-3"]).Index == 2 &&
            Placement(windows["fixture-3"]).SlotCount == 3,
            "re-entering the same target did not open its slot again");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip - 20) < 1e-6,
            "re-entering the target did not restore the neighbouring avoidance");

        // 幽灵占位框：同一 (队列, index, carriedCount) 的重复下发不得重启滑动动画。
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        var outline = Field(controller, "_edgeCapsuleQueueDropTargetOutline");
        Require(outline != null, "drop-target outline was not created");
        Require(
            (int)Part(outline!, "PlaceholderIndex") == 1,
            "the placeholder did not track the insertion index");
        Require(
            (int)Part(outline!, "PlaceholderHeightAnimationCount") == 1,
            "the placeholder did not grow from zero on first appearance");
        Require(
            (int)Part(outline!, "PlaceholderSlideCount") == 0,
            "the first appearance must not slide");

        var slidesAtOne = (int)Part(outline!, "PlaceholderSlideCount");
        var heightsAtOne = (int)Part(outline!, "PlaceholderHeightAnimationCount");
        var topAtOne = (double)Part(outline!, "PlaceholderTopTargetDip");
        var heightAtOne = (double)Part(outline!, "PlaceholderTargetHeightDip");

        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        Require(
            (int)Part(outline!, "PlaceholderSlideCount") == slidesAtOne &&
            (int)Part(outline!, "PlaceholderHeightAnimationCount") == heightsAtOne,
            "repeating the same insertion slot restarted a placeholder animation");

        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 0);
        Require(
            (int)Part(outline!, "PlaceholderSlideCount") == slidesAtOne + 1,
            "changing the insertion index did not slide the placeholder");
        Require(
            (int)Part(outline!, "PlaceholderHeightAnimationCount") == heightsAtOne,
            "an index change must not restart the height animation");
        var topAtZero = (double)Part(outline!, "PlaceholderTopTargetDip");
        Require(
            topAtZero < topAtOne,
            "the placeholder did not move toward the earlier slot");

        // carriedCount 变大时，占位块高度也应重新展开。
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 2, 0);
        Require(
            (int)Part(outline!, "PlaceholderHeightAnimationCount") == heightsAtOne + 1,
            "a taller carried block did not animate its height");
        Require(
            (double)Part(outline!, "PlaceholderTargetHeightDip") > heightAtOne,
            "a taller carried block did not increase the placeholder height");

        controller.EndEdgeCapsuleQueueDropTargetOutline();
    }

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Part(window, "_edgeCapsule")).Placement;

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target);

    private static object Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);

    private static async Task Until(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12))
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(10);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
