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

        // 源队列 qa 由 [0, 1] 撤走 0 后收拢为 [1]；目标 qb 按完整槽位展开为 3 槽，
        // fixture-3 下移一整槽；下方 qc 被求解器推到 470（+20）。
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "the normal insertion preview should be feasible");
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 3 &&
            Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip) < 1e-6,
            "preview did not keep the first target member in place");
        Require(
            Placement(windows["fixture-3"]).Index == 2 &&
            Placement(windows["fixture-3"]).SlotCount == 3 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6,
            "preview did not reserve a full slot below the insertion point");
        Require(
            Placement(windows["fixture-1"]).Index == 0 &&
            Placement(windows["fixture-1"]).SlotCount == 1,
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
            Placement(windows["fixture-3"]).SlotCount == 2 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6,
            "restore left the target queue's opened slot behind");
        Require(
            Placement(windows["fixture-1"]).Index == 0,
            "restore disturbed the collapsed source queue");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "restore left the neighbouring queue displaced");

        // 重新进入同一目标必须能再次展开；交互层若残留 preview key，会在这里暴露。
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "re-entering the same target should be feasible");
        Require(
            Placement(windows["fixture-3"]).Index == 2 &&
            Placement(windows["fixture-3"]).SlotCount == 3,
            "re-entering the same target did not reserve its slot again");

        // Ctrl 整队：两个胶囊占两个完整槽位，qb bottom=516，qc 被推到 520（+70）。
        var secondSource = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source, secondSource], targetKey, 1),
            "the two-capsule transfer preview should be feasible");
        Require(
            Placement(windows["fixture-3"]).Index == 3 &&
            Placement(windows["fixture-3"]).SlotCount == 4 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6,
            "a two-capsule transfer did not reserve both slots");
        Require(
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip - 70) < 1e-6,
            "a two-capsule transfer did not push the queue below by the full block");
        controller.RestoreDeepCapsuleQueuePreview(targetKey);

        // 插入短线：同一 (队列, index, carriedCount) 的重复下发不得重启滑动动画。
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "the outline preview should be feasible");
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
            !(bool)Part(outline!, "InsertionLineIsWarning"),
            "a feasible insertion must not use the warning color");

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

        // 空间不足：qa 顶到 8、qb 顶到 40，qa 无法再向上让位。
        var qaKey = AppController.QueueKeyForPaper(source);
        var qcKey = AppController.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-4"));
        controller.State.DeepCapsuleQueueStartTopMargins[qaKey] = 8;
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = 40;
        controller.State.DeepCapsuleQueueStartTopMargins[qcKey] = 450;
        controller.ArrangeDeepCapsules(animate: false);

        Require(
            !controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "an insertion with no legal layout must be rejected");
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        var warningOutline = Field(controller, "_edgeCapsuleQueueDropTargetOutline");
        Require(
            warningOutline != null &&
            (bool)Part(warningOutline, "InsertionLineIsWarning"),
            "an infeasible insertion must draw the warning-colored line");
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-2"]).SlotCount == 2 &&
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 2,
            "an infeasible preview must not expand the target queue");

        var commitAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(
                geometry.WorkArea.Right - 10,
                geometry.WorkArea.Top + 100 * geometry.DpiScaleY),
            geometry.WorkArea.Top + 100 * geometry.DpiScaleY);
        Require(
            !controller.CommitDeepCapsuleQueueDrop(source, commitAnchor, targetKey, 1),
            "an infeasible insertion must be rejected on commit");
        Require(
            AppController.QueueKeyForPaper(source) == qaKey,
            "a rejected capsule must stay in its source queue");
        Require(
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qaKey] - 8) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - 40) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qcKey] - 450) < 1e-6,
            "a rejected insertion must not persist any layout change");

        controller.EndEdgeCapsuleQueueDropTargetOutline();
        controller.State.DeepCapsuleQueueStartTopMargins[qaKey] = 48;
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = 320;
        controller.State.DeepCapsuleQueueStartTopMargins[qcKey] = 450;
        controller.ArrangeDeepCapsules(animate: false);

        // 可行提交：把避让后的队列 top 持久化，Arrange 后与预览一致。
        Require(
            controller.CommitDeepCapsuleQueueDrop(source, commitAnchor, targetKey, 1),
            "a feasible insertion should commit");
        Require(
            AppController.QueueKeyForPaper(source) == targetKey,
            "the committed capsule did not join the target queue");
        Require(
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - 320) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qcKey] - 470) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qaKey] - 48) < 1e-6,
            "the committed layout was not persisted");
        await Until(
            () => Placement(windows["fixture-3"]).Index == 2 &&
                  Placement(windows["fixture-3"]).SlotCount == 3,
            "the committed target queue did not adopt the final slot layout");
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
