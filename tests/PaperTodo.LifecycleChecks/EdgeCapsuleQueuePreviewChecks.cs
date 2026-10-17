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

        // 源队列 qa 由 [0, 1] 撤走 0 后收拢为 [1]；目标 qb 由 [2, 3] 只在插入点下方
        // 轻微让位：成员 Index 不变，SlotCount 按最终提交槽数归一化，额外间隙为 10 DIP。
        controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1);
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 3 &&
            Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip) < 1e-6,
            "preview did not keep the first target member in place");
        Require(
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 3 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip - 10) < 1e-6,
            "preview did not part the member below the insertion point");
        Require(
            Placement(windows["fixture-1"]).Index == 0,
            "preview did not close the source queue's vacated slot");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "the small insertion gap pushed a neighbouring queue that already had room");

        controller.RestoreDeepCapsuleQueuePreview(targetKey);
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 2,
            "restore did not shrink the target queue back to its real slot count");
        Require(
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 2 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6,
            "restore left the target queue's opened gap behind");
        Require(
            Placement(windows["fixture-1"]).Index == 0,
            "restore disturbed the collapsed source queue");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "restore left the neighbouring queue displaced");

        // 重新进入同一目标必须能再次让位；交互层若残留 preview key，会在这里暴露。
        controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1);
        Require(
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 3 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip - 10) < 1e-6,
            "re-entering the same target did not part its members again");

        // Ctrl 整队：额外间隙随携带数量小幅加宽（2 个 → 18 DIP）。
        var secondSource = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        controller.PreviewDeepCapsuleQueueDrag([source, secondSource], targetKey, 1);
        Require(
            Placement(windows["fixture-3"]).SlotCount == 4 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip - 18) < 1e-6,
            "a two-capsule transfer did not widen the insertion gap");
        controller.RestoreDeepCapsuleQueuePreview(targetKey);

        // 插入短线：同一 (队列, index, carriedCount) 的重复下发不得重启滑动动画。
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        var outline = Field(controller, "_edgeCapsuleQueueDropTargetOutline");
        Require(outline != null, "drop-target outline was not created");
        Require(
            (int)Part(outline!, "InsertionLineIndex") == 1,
            "the insertion line did not track the insertion index");
        Require(
            (int)Part(outline!, "InsertionLineSlideCount") == 0,
            "the first appearance must not slide");
        Require(
            Math.Abs((double)Part(outline!, "InsertionLineGapDip") - 10) < 1e-6,
            "a single carried capsule did not use the base insertion gap");

        var slidesAtOne = (int)Part(outline!, "InsertionLineSlideCount");
        var topAtOne = (double)Part(outline!, "InsertionLineTopTargetDip");

        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        Require(
            (int)Part(outline!, "InsertionLineSlideCount") == slidesAtOne,
            "repeating the same insertion slot restarted a slide animation");

        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 0);
        Require(
            (int)Part(outline!, "InsertionLineSlideCount") == slidesAtOne + 1,
            "changing the insertion index did not slide the line");
        var topAtZero = (double)Part(outline!, "InsertionLineTopTargetDip");
        Require(
            topAtZero < topAtOne,
            "the insertion line did not move toward the earlier slot");

        // carriedCount 变大时，插入间隙加宽，短线随之下移。
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 2, 0);
        Require(
            Math.Abs((double)Part(outline!, "InsertionLineGapDip") - 18) < 1e-6,
            "a two-capsule block did not widen the insertion gap");
        Require(
            (double)Part(outline!, "InsertionLineTopTargetDip") > topAtZero,
            "a wider insertion gap did not push the line lower");

        controller.EndEdgeCapsuleQueueDropTargetOutline();

        // 高频评估早退：同一 (target, index, carriedCount) 不重复计算描边布局。
        var layouts = (int)Part(controller, "DropTargetLayoutUpdateCount");
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        var layoutsAfterFirst = (int)Part(controller, "DropTargetLayoutUpdateCount");
        Require(
            layoutsAfterFirst == layouts + 1,
            "the first outline show did not compute its layout");
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        Require(
            (int)Part(controller, "DropTargetLayoutUpdateCount") == layoutsAfterFirst,
            "repeating the same outline target recomputed its layout");
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 0);
        Require(
            (int)Part(controller, "DropTargetLayoutUpdateCount") == layoutsAfterFirst + 1,
            "changing the outline index did not recompute its layout");
        controller.EndEdgeCapsuleQueueDropTargetOutline();

        // 磁吸候选按手势缓存：相同 anchor 的重复评估不再重建候选或测量标题。
        Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry was not resolved");
        var anchorY = geometry.WorkArea.Top + 320 * geometry.DpiScaleY;
        var anchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, anchorY),
            anchorY);
        var builds = (int)Part(controller, "MagnetCandidateBuildCount");
        controller.EvaluateDeepCapsuleQueueDrag([source], anchor);
        var buildsAfterFirst = (int)Part(controller, "MagnetCandidateBuildCount");
        Require(
            buildsAfterFirst == builds + 1,
            "the first magnet evaluation did not build its candidates");
        controller.EvaluateDeepCapsuleQueueDrag([source], anchor);
        Require(
            (int)Part(controller, "MagnetCandidateBuildCount") == buildsAfterFirst,
            "repeating the same magnet evaluation rebuilt its candidates");
        controller.ForgetDeepCapsuleQueueDrag();
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
