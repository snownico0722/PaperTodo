using System.Diagnostics;
using PaperTodo;

/// <summary>
/// 自由队列核心行为：跨队列合并、同队列重排、新建队列、提交拒绝与身份/margin。
/// 只验证控制器原语的结果，不驱动真实指针或动画。
/// </summary>
internal static class EdgeCapsuleQueuePreviewChecks
{
    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "free-queue preview fixture slots did not become visible");

        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var targetPaper = controller.State.Papers.Single(paper => paper.Id == "fixture-2");
        var targetKey = controller.QueueKeyForPaper(targetPaper);

        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-3"]).Index == 1,
            "target queue did not start with its true slot order");

        // 浮空拖回自身队列：默认拒绝同队列目标；显式允许时按插入 index 开槽。
        var sourceQueueKey = controller.QueueKeyForPaper(source);
        Require(
            !controller.PreviewDeepCapsuleQueueDrag([source], sourceQueueKey, 1),
            "a same-queue preview must stay opt-in");
        Require(
            controller.PreviewDeepCapsuleQueueDrag(
                [source], sourceQueueKey, 1, allowSourceQueueTarget: true),
            "the floating same-queue preview should be feasible");
        Require(
            Placement(windows["fixture-1"]).Index == 0 &&
            Placement(windows["fixture-1"]).SlotCount == 2,
            "the same-queue preview did not open a slot in the source queue");
        controller.RestoreDeepCapsuleQueuePreview(sourceQueueKey);
        Require(
            Placement(windows["fixture-1"]).Index == 1 &&
            Placement(windows["fixture-1"]).SlotCount == 2 &&
            Math.Abs(Placement(windows["fixture-1"]).TopOffsetDip) < 1e-6,
            "the same-queue preview did not restore the source queue");

        // 跨队列插入：源队列收拢、目标开槽、下方队列避让。
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
            Placement(windows["fixture-2"]).SlotCount == 2 &&
            Placement(windows["fixture-3"]).Index == 1 &&
            Placement(windows["fixture-3"]).SlotCount == 2 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6 &&
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "restore did not put the target edge back into its real layout");

        // Ctrl 整队：两个胶囊占两个完整槽位，下方队列按整块高度避让。
        var secondSource = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source, secondSource], targetKey, 1),
            "the two-capsule transfer preview should be feasible");
        Require(
            Placement(windows["fixture-3"]).Index == 3 &&
            Placement(windows["fixture-3"]).SlotCount == 4 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6 &&
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip - 70) < 1e-6,
            "a two-capsule transfer did not reserve its full block");
        controller.RestoreDeepCapsuleQueuePreview(targetKey);

        // 提交拒绝：没有合法布局时不得写入任何 margin 或身份。
        Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry was not resolved");
        var qaKey = controller.QueueKeyForPaper(source);
        var qcKey = controller.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-4"));
        controller.State.DeepCapsuleQueueStartTopMargins[qaKey] = 8;
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = 40;
        controller.State.DeepCapsuleQueueStartTopMargins[qcKey] = 450;
        controller.ArrangeDeepCapsules(animate: false);

        Require(
            !controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "an insertion with no legal layout must be rejected");
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
            controller.QueueKeyForPaper(source) == qaKey &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qaKey] - 8) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - 40) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qcKey] - 450) < 1e-6,
            "a rejected insertion must not persist any layout or identity change");

        // 可行提交：目标/邻队 margin 落盘，源胶囊加入目标队列。
        controller.State.DeepCapsuleQueueStartTopMargins[qaKey] = 48;
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = 320;
        controller.State.DeepCapsuleQueueStartTopMargins[qcKey] = 450;
        controller.ArrangeDeepCapsules(animate: false);

        Require(
            controller.CommitDeepCapsuleQueueDrop(source, commitAnchor, targetKey, 1),
            "a feasible insertion should commit");
        Require(
            controller.QueueKeyForPaper(source) == targetKey &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - 320) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qcKey] - 470) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qaKey] - 48) < 1e-6,
            "the committed layout was not persisted");
        await Until(
            () => Placement(windows["fixture-3"]).Index == 2 &&
                  Placement(windows["fixture-3"]).SlotCount == 3,
            "the committed target queue did not adopt the final slot layout");

        // 同队列重排：预览与提交共用 0.5 槽取整，且不得改写邻居 margin。
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-0"]).Index == 1 &&
            Placement(windows["fixture-3"]).Index == 2,
            "the queue did not settle in its expected order before the reorder check");

        var qbKey = controller.QueueKeyForPaper(source);
        var qbMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right, "qb");
        var gap = controller.DeepCapsuleGap;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(gap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(
            0, qbMargin, geometry.LocalWorkAreaDip, 3, gap);
        double DeviceCenterY(double slots) =>
            geometry.WorkArea.Top +
            (firstTop + slots * slotHeight + PaperLayoutDefaults.CapsuleHeight / 2.0) *
            geometry.DpiScaleY;

        Require(
            controller.DeepCapsuleQueueInsertIndexFor(qbKey, source.Id, DeviceCenterY(1.4)) == 1,
            "the insertion slot below half-way did not round down");
        var targetIndex = controller.DeepCapsuleQueueInsertIndexFor(
            qbKey, source.Id, DeviceCenterY(1.6));
        Require(targetIndex == 2, "the insertion slot past half-way did not round up");

        var reorderAnchorY = DeviceCenterY(1.6);
        var reorderAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, reorderAnchorY),
            reorderAnchorY);
        controller.PreviewDeepCapsuleReorder(source, targetIndex);
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-3"]).Index == 1,
            "the reorder preview did not place the peers at the target slots");
        var marginsBeforeReorder = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        Require(
            controller.CommitDeepCapsuleQueueDrop(
                source, reorderAnchor, qbKey, targetIndex, sameQueueReorderPreviewed: true),
            "the pinned same-queue reorder was rejected");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Count == marginsBeforeReorder.Count &&
            marginsBeforeReorder.All(pair =>
                controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                    pair.Key, out var margin) &&
                Math.Abs(margin - pair.Value) < 1e-6),
            "a same-queue reorder must not rewrite neighbour margins");
        await Until(
            () => Placement(windows["fixture-2"]).Index == 0 &&
                  Placement(windows["fixture-3"]).Index == 1 &&
                  Placement(windows[source.Id]).Index == 2,
            "the committed reorder did not match the preview slots");

        // 新建队列：源队列收拢，源队列 margin 保留。
        var qbMarginBeforeSplit = controller.State.DeepCapsuleQueueStartTopMargins[qbKey];
        var splitAnchorY = geometry.WorkArea.Top + 200 * geometry.DpiScaleY;
        var splitAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, splitAnchorY),
            splitAnchorY);
        Require(
            controller.CommitDeepCapsuleQueueDrop(source, splitAnchor),
            "the new-queue drop was rejected");
        Require(
            controller.QueueKeyForPaper(source) != qbKey,
            "the new-queue drop did not split the capsule out of its queue");
        await Until(
            () => Placement(windows["fixture-2"]).Index == 0 &&
                  Placement(windows["fixture-3"]).Index == 1,
            "the source queue did not close up after the split");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                qbKey, out var qbMarginAfterSplit) &&
            Math.Abs(qbMarginAfterSplit - qbMarginBeforeSplit) < 1e-6,
            "splitting one member from a multi-member queue discarded its start margin");

        // 单成员队列原地放下：复用实例 id 与 margin key。
        var single = controller.State.Papers.Single(paper => paper.Id == "fixture-4");
        var singleKey = controller.QueueKeyForPaper(single);
        var singleQueueId = single.CapsuleQueueId;
        var marginKeysBeforeSingle = controller.State.DeepCapsuleQueueStartTopMargins.Keys
            .ToHashSet(StringComparer.Ordinal);
        var singleArea = geometry.LocalWorkAreaDip;
        var singleBottomCenterY = geometry.WorkArea.Top +
            (singleArea.Height - 10) * geometry.DpiScaleY;
        var singleAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, singleBottomCenterY),
            singleBottomCenterY);
        Require(
            controller.CommitDeepCapsuleQueueDrop(single, singleAnchor),
            "the single-member same-edge drop was rejected");
        Require(
            controller.QueueKeyForPaper(single) == singleKey &&
            single.CapsuleQueueId == singleQueueId &&
            controller.State.DeepCapsuleQueueStartTopMargins.Keys
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(marginKeysBeforeSingle),
            "a single-member same-edge drop changed identity or created an orphan margin");
        var expectedSingleMargin = EdgeCapsuleLayout.NormalizeStartTopMargin(
            EdgeCapsuleLayout.MaxStartTopMarginForCount(
                1, singleArea, controller.DeepCapsuleGap),
            singleArea,
            1,
            controller.DeepCapsuleGap);
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                singleKey, out var singleMargin) &&
            Math.Abs(singleMargin - expectedSingleMargin) < 1e-6,
            "a reused queue identity did not remember its new drop height");
    }

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Program.Part(window, "_edgeCapsule")).Placement;

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
