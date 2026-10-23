using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using PaperTodo;

/// <summary>
/// 自由队列拖拽期交互的控制器原语检查。
///
/// 覆盖目标开槽与离开后的恢复、描边/磁吸缓存失效、整队搬移的取消/拒绝/异常回滚、关闭自由
/// 队列后的身份折叠、预览 session 稳定性、同队列重排取整、拆分与隐藏成员 margin、断连显示器
/// 以及浮空宿主异常关闭。这里直接调用控制器原语验证 placement，不需要驱动真实指针或等待动画。
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
        var targetKey = controller.QueueKeyForPaper(targetPaper);

        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-3"]).Index == 1,
            "target queue did not start with its true slot order");

        // 浮空拖回自身队列：默认仍拒绝同队列目标；显式允许时源队列按插入 index 开槽。
        // 这样松手前就能看到与提交一致的槽位，而不是只有源队列收拢。
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

        // 同 key/index 但重新生成了拖放计划时，描边不得复用旧布局。
        var layoutsBeforeRegeneratedPlan = (int)Part(controller, "DropTargetLayoutUpdateCount");
        Require(
            controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1),
            "the regenerated plan fixture was infeasible");
        controller.ShowEdgeCapsuleQueueDropTargetOutline(targetKey, source.Id, 1, 1);
        Require(
            (int)Part(controller, "DropTargetLayoutUpdateCount") == layoutsBeforeRegeneratedPlan + 1,
            "a regenerated drop layout did not invalidate the outline cache");
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

        // FQ-05：影响候选几何的外部输入变化必须使缓存失效。
        var targetMarginBefore = controller.State.DeepCapsuleQueueStartTopMargins[targetKey];
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = targetMarginBefore + 1;
        controller.EvaluateDeepCapsuleQueueDrag([source], anchor);
        Require(
            (int)Part(controller, "MagnetCandidateBuildCount") == buildsAfterFirst + 1,
            "changing a queue margin did not rebuild the magnet candidates");
        controller.State.DeepCapsuleQueueStartTopMargins[targetKey] = targetMarginBefore;

        var targetQueueIdBefore = targetPaper.CapsuleQueueId;
        targetPaper.CapsuleQueueId = "qexternal";
        controller.EvaluateDeepCapsuleQueueDrag([source], anchor);
        Require(
            (int)Part(controller, "MagnetCandidateBuildCount") == buildsAfterFirst + 2,
            "changing queue membership did not rebuild the magnet candidates");
        targetPaper.CapsuleQueueId = targetQueueIdBefore;

        var gapBefore = controller.State.DeepCapsuleGapSize;
        controller.State.DeepCapsuleGapSize = DeepCapsuleGapSizes.Wide;
        controller.EvaluateDeepCapsuleQueueDrag([source], anchor);
        Require(
            (int)Part(controller, "MagnetCandidateBuildCount") == buildsAfterFirst + 3,
            "changing the capsule gap did not rebuild the magnet candidates");
        controller.State.DeepCapsuleGapSize = gapBefore;

        controller.ClaimEdgeCapsuleQueueDragOwnership(source.Id);
        controller.ForgetDeepCapsuleQueueDrag(source.Id);
        controller.ReleaseEdgeCapsuleQueueDragOwnership();

        // 空间不足：qa 顶到 8、qb 顶到 40，qa 无法再向上让位。
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
            controller.QueueKeyForPaper(source) == qaKey,
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
            controller.QueueKeyForPaper(source) == targetKey,
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

        // H1：整队搬移在取消/异常路径必须释放快照与 controller 的 source 集合，
        // 否则后续 Arrange 会永久 detach 整条队列，且整队拖拽再也无法开始。
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(source, out var transferSnapshot),
            "the whole-queue transfer fixture did not start");
        var sourceWindow = windows[source.Id];
        SetField(sourceWindow, "_deepCapsuleQueueTransferSnapshot", transferSnapshot);
        Invoke(sourceWindow, "CancelDeepCapsuleReorderDrag", false);

        Require(
            !controller.IsPaperInTransferringEdgeQueue(source),
            "cancelling a drag left the source queue in the transfer set");
        Require(
            Field(sourceWindow, "_deepCapsuleQueueTransferSnapshot") == null,
            "cancelling a drag left the transfer snapshot behind");
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(source, out var retrySnapshot),
            "a later whole-queue transfer was rejected after the cancel");
        controller.AbandonEdgeCapsuleQueueTransfer(retrySnapshot);
        controller.ArrangeDeepCapsules(animate: false);
        await Until(
            () => windows[source.Id].IsDeepCapsuleSlotVisible,
            "the source queue did not return after the transfer cancel");

        // M5：整队提交拿到 None（输入退化）时必须回滚并释放冻结集合，
        // 而不是退回原始光标位置硬放一个可能与现有队列重叠的新队列。
        var marginsBeforeRejectedTransfer = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(targetPaper, out var rejectedTransfer),
            "the infeasible whole-queue transfer fixture did not start");
        var rejectedAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(
                geometry.WorkArea.Right - 10,
                geometry.WorkArea.Top + 100 * geometry.DpiScaleY),
            double.NaN);
        Require(
            !controller.CommitEdgeCapsuleQueueTransfer(rejectedTransfer, rejectedAnchor),
            "a whole-queue transfer without a finite drop centre must be rejected");
        Require(
            !controller.IsPaperInTransferringEdgeQueue(targetPaper),
            "a rejected whole-queue transfer left its source frozen");
        Require(
            controller.State.Papers
                .Where(paper => paper.Id is "fixture-2" or "fixture-3")
                .All(paper => controller.QueueKeyForPaper(paper) == targetKey),
            "a rejected whole-queue transfer changed queue membership");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Count ==
                marginsBeforeRejectedTransfer.Count &&
            marginsBeforeRejectedTransfer.All(pair =>
                controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                    pair.Key, out var margin) &&
                Math.Abs(margin - pair.Value) < 1e-6),
            "a rejected whole-queue transfer changed queue margins");
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(targetPaper, out var retryTransfer),
            "a rejected whole-queue transfer stranded the transfer gate");
        controller.AbandonEdgeCapsuleQueueTransfer(retryTransfer);

        // FQ-04：整队拖拽期间外部移动非首个 carried 成员时，提交必须释放事务并拒绝，
        // 且不能用旧快照覆盖外部合法修改。
        var externalMember = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(targetPaper, out var externalTransfer),
            "the external-mutation transfer fixture did not start");
        var externalQueueIdBefore = externalMember.CapsuleQueueId;
        externalMember.CapsuleQueueId = "qexternal";
        var marginsBeforeExternalCommit = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        Require(
            !controller.CommitEdgeCapsuleQueueTransfer(externalTransfer, commitAnchor),
            "a transfer whose non-first member moved externally must be rejected");
        Require(
            !controller.IsPaperInTransferringEdgeQueue(targetPaper),
            "a rejected transfer left its source queue frozen");
        Require(
            controller.QueueKeyForPaper(externalMember).Contains("#qexternal", StringComparison.Ordinal),
            "a rejected transfer rolled back an external membership change");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Count ==
                marginsBeforeExternalCommit.Count &&
            marginsBeforeExternalCommit.All(pair =>
                controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                    pair.Key, out var margin) &&
                Math.Abs(margin - pair.Value) < 1e-6),
            "a rejected transfer changed queue margins");
        externalMember.CapsuleQueueId = externalQueueIdBefore;
        controller.ArrangeDeepCapsules(animate: false);

        // 用户取消同样不得覆盖拖拽期间的外部修改：序列不一致时只释放事务。
        Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(targetPaper, out var abandonedTransfer),
            "the abandon transfer fixture did not start");
        externalMember.CapsuleQueueId = "qexternal";
        controller.AbandonEdgeCapsuleQueueTransfer(abandonedTransfer);
        Require(
            controller.QueueKeyForPaper(externalMember).Contains("#qexternal", StringComparison.Ordinal),
            "cancelling a transfer overwrote an external membership change");
        Require(
            !controller.IsPaperInTransferringEdgeQueue(targetPaper),
            "an abandoned transfer left its source queue frozen");
        externalMember.CapsuleQueueId = externalQueueIdBefore;
        controller.ArrangeDeepCapsules(animate: false);

        // H2：关闭自由队列后，同一显示器同侧必须回到单队列身份，主胶囊也只能有一个；
        // 实例 id 与 margin 保留，重新开启后恢复原自由队列布局。
        var sourceInstanceMargin = controller.DeepCapsuleStartTopMarginFor(source);
        controller.State.DeepCapsuleQueueStartTopMargins["|right"] = 96;
        controller.State.UseCapsuleFreeQueues = false;
        controller.State.UseCapsuleCollapseAll = true;
        controller.ArrangeDeepCapsules(animate: false);

        var masters = (Dictionary<string, MasterCapsuleWindow>?)Field(controller, "_masterCapsules")
            ?? throw new InvalidOperationException("master capsule map was missing");
        Require(
            masters.Count == 1 && masters.ContainsKey("|right"),
            "disabling free queues did not collapse the edge into one master queue");
        Require(
            controller.QueueKeyForPaper(source) == "|right",
            "disabling free queues kept an instance queue identity");
        Require(
            controller.VisibleDeepCapsuleCountForQueue(source) == 5,
            "disabling free queues did not merge every capsule on the edge");
        Require(
            controller.MasterCapsuleForQueue("", EdgeCapsuleEdge.Right) != null,
            "the bare-edge master capsule was not reachable by the master lookup");
        Require(
            Math.Abs(controller.DeepCapsuleStartTopMarginFor(source) - 96) < 1e-6,
            "the merged queue did not read the bare-edge start margin");

        controller.State.UseCapsuleCollapseAll = false;
        controller.State.UseCapsuleFreeQueues = true;
        controller.ArrangeDeepCapsules(animate: false);

        Require(masters.Count == 0, "re-enabling free queues did not retire the master capsule");
        Require(
            controller.QueueKeyForPaper(source).Contains('#'),
            "re-enabling free queues did not restore the instance queue identity");
        Require(
            !string.IsNullOrEmpty(source.CapsuleQueueId),
            "re-enabling free queues lost the persisted instance id");
        Require(
            Math.Abs(controller.DeepCapsuleStartTopMarginFor(source) - sourceInstanceMargin) < 1e-6,
            "re-enabling free queues did not restore the instance start margin");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(targetKey),
            "the instance start margin was discarded while free queues were disabled");

        // M1：布局 session 必须写回并保持引用稳定，否则每次 Arrange 都会以
        // previous:null 重建，丢失同队列 owner 转移的连续性。
        var ownerWindow = windows[source.Id];
        await Until(
            () => ownerWindow.CanEnterEdgeCapsulePreview,
            "the owner capsule did not settle before the preview session check");
        var previewSize = new EdgeCapsulePreviewSize(300, 200);
        var basePlan = (EdgeCapsuleQueuePlan?)Call(controller, "BuildCurrentEdgeCapsuleQueuePlan")
            ?? throw new InvalidOperationException("the current queue plan was not built");
        var previewSession = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            null,
            controller.QueueKeyForPaper(source),
            source.Id,
            previewSize,
            PaperLayoutDefaults.CapsuleHeight,
            controller.DeepCapsuleGap)
            ?? throw new InvalidOperationException("the preview session was not created");
        SetField(controller, "_edgeCapsulePreviewSession", previewSession);
        var previewContent = new Border
        {
            Width = previewSize.ContentSize.Width,
            Height = previewSize.ContentSize.Height
        };
        var previewRequest = new EdgeCapsulePreviewRequest(previewSize, previewContent);
        Require(
            ownerWindow.SetEdgeCapsulePreviewOpen(previewRequest, animate: false),
            "the preview session fixture did not open");
        Require(ownerWindow.IsEdgeCapsulePreviewOpen, "the owner is not in preview state");

        var rebuildsAfterOpen = controller.PreviewSessionRebuildCount;
        controller.ArrangeDeepCapsules(animate: false);
        var storedAfterFirst = Field(controller, "_edgeCapsulePreviewSession");
        controller.ArrangeDeepCapsules(animate: false);
        var storedAfterSecond = Field(controller, "_edgeCapsulePreviewSession");
        Require(
            controller.PreviewSessionRebuildCount == rebuildsAfterOpen,
            "a stable preview session was rebuilt on Arrange");
        Require(
            ReferenceEquals(storedAfterFirst, storedAfterSecond),
            "a stable preview session was replaced on Arrange");
        var storedSession = (EdgeCapsulePreviewLayoutSession?)storedAfterSecond;
        Require(
            storedSession != null && storedSession.EdgePaperIds.Count == 5,
            "the edge-wide paper ids were not written back to the preview session");

        controller.CloseEdgeCapsulePreviewForDrag(ownerWindow);
        Require(
            Field(controller, "_edgeCapsulePreviewSession") == null,
            "closing the preview did not clear the layout session");
        await Until(
            () => !ownerWindow.IsEdgeCapsulePreviewOpen,
            "the preview did not close after the session check");

        // M2：贴边同队列重排的预览与提交必须使用同一取整规则（0.5 槽边界）。
        Require(
            Placement(windows["fixture-2"]).Index == 0 &&
            Placement(windows["fixture-0"]).Index == 1 &&
            Placement(windows["fixture-3"]).Index == 2,
            "the queue did not settle in its expected order before the reorder check");

        var qbKey = controller.QueueKeyForPaper(source);
        var qbMargin = controller.DeepCapsuleStartTopMarginForQueue(
            "", EdgeCapsuleEdge.Right, "qb");
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

        // M2 提交侧：贴边 NewQueue 必须让源队列收拢，而不是保留空槽。
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

        // M6：qb 仍有成员，拆分单个成员不得删除它的 margin。
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                qbKey, out var qbMarginAfterSplit) &&
            Math.Abs(qbMarginAfterSplit - qbMarginBeforeSplit) < 1e-6,
            "splitting one member from a multi-member queue discarded its start margin");

        // M6：单成员队列在同一侧放下时复用原实例 id，并原地更新它的 margin，
        // 不得生成新的孤立 key。
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
            single.CapsuleQueueId == singleQueueId,
            "a single-member queue changed identity when dropped in place");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Keys
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(marginKeysBeforeSingle),
            "a single-member same-edge drop created an orphan queue margin");
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

        // M6：跨边拆分后源队列为空，旧 margin 必须删除，新边使用新的实例 id。
        var oldSingleKey = controller.QueueKeyForPaper(single);
        var leftAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Left + 10, singleBottomCenterY),
            singleBottomCenterY);
        Require(
            controller.CommitDeepCapsuleQueueDrop(single, leftAnchor),
            "the cross-edge new-queue drop was rejected");
        Require(
            controller.QueueKeyForPaper(single) != oldSingleKey &&
            controller.QueueKeyForPaper(single).Contains("|left#", StringComparison.Ordinal),
            "a cross-edge split did not assign a new instance id on the target edge");
        Require(
            !controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(oldSingleKey),
            "a vacated queue's start margin was left behind after a cross-edge split");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(
                controller.QueueKeyForPaper(single)),
            "the cross-edge new queue did not persist its start margin");

        // M6：隐藏成员仍属于源队列，不能因为可见成员被拆走就删除它的 margin；
        // 同时源队列仍有成员，拆分必须生成新 id。
        var leftKey = controller.QueueKeyForPaper(single);
        var leftQueueId = single.CapsuleQueueId;
        var leftMargin = controller.State.DeepCapsuleQueueStartTopMargins[leftKey];
        controller.State.Papers.Add(new PaperData
        {
            Id = "fixture-hidden",
            Type = PaperTypes.Note,
            IsVisible = false,
            CapsuleMonitorDeviceName = single.CapsuleMonitorDeviceName,
            CapsuleSide = single.CapsuleSide,
            CapsuleQueueId = leftQueueId
        });
        var hiddenAnchorY = geometry.WorkArea.Top + 120 * geometry.DpiScaleY;
        var hiddenAnchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Left + 10, hiddenAnchorY),
            hiddenAnchorY);
        Require(
            controller.CommitDeepCapsuleQueueDrop(single, hiddenAnchor),
            "the drop beside a queue with a hidden member was rejected");
        Require(
            controller.QueueKeyForPaper(single) != leftKey &&
            single.CapsuleQueueId != leftQueueId,
            "splitting the only visible member reused an id although a hidden member remained");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(
                leftKey, out var keptMargin) &&
            Math.Abs(keptMargin - leftMargin) < 1e-6,
            "a queue that still has a hidden member lost its start margin");
        Require(
            controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(
                controller.QueueKeyForPaper(single)),
            "the new queue beside a hidden member did not persist its start margin");

        // FQ-01：显示器断开后仍保留持久队列身份与 margin；空 id 队列不会与主屏合并。
        var cacheField = typeof(WindowWorkAreaHelper).GetField(
            "_cachedMonitors", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalMonitorCache = cacheField.GetValue(null)!;
        const string disconnectedMonitor = "PAPERTODO-FIXTURE-FREE-QUEUE-DISCONNECT";
        try
        {
            var entries = ((System.Collections.IEnumerable)originalMonitorCache).Cast<object>().ToArray();
            var entryType = entries[0].GetType();
            var primaryEntry = entries.First(entry => (bool)Value(entry, "IsPrimary"));
            var fakeEntry = Activator.CreateInstance(
                entryType,
                Value(primaryEntry, "Handle"),
                disconnectedMonitor,
                Value(primaryEntry, "WorkArea"),
                false,
                Value(primaryEntry, "DpiScaleX"),
                Value(primaryEntry, "DpiScaleY"))!;
            var connected = Array.CreateInstance(entryType, entries.Length + 1);
            for (var i = 0; i < entries.Length; i++)
            {
                connected.SetValue(entries[i], i);
            }

            connected.SetValue(fakeEntry, entries.Length);

            var transferA = controller.State.Papers.Single(paper => paper.Id == "fixture-2");
            var transferB = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
            foreach (var member in new[] { transferA, transferB })
            {
                member.CapsuleMonitorDeviceName = disconnectedMonitor;
                member.CapsuleSide = DeepCapsuleSides.Right;
                member.CapsuleQueueId = "qdisp";
            }

            var persistentKey = disconnectedMonitor + "|right#qdisp";
            controller.State.DeepCapsuleQueueStartTopMargins[persistentKey] = 120;
            cacheField.SetValue(null, connected);
            controller.ArrangeDeepCapsules(animate: false);

            Require(
                controller.QueueKeyForPaper(transferA) == persistentKey,
                "free queue identity did not keep the persistent monitor while connected");
            Require(
                Math.Abs(controller.DeepCapsuleStartTopMarginFor(transferA) - 120) < 1e-6,
                "the persistent queue margin was not readable while connected");
            Require(
                controller.TryBeginEdgeCapsuleQueueTransfer(transferA, out var disconnectTransfer),
                "the disconnect transfer fixture did not start");

            // 模拟运行中拔掉副屏。
            cacheField.SetValue(null, originalMonitorCache);
            Require(
                controller.QueueKeyForPaper(transferA) == persistentKey,
                "disconnecting a monitor discarded the free queue identity");
            Require(
                controller.IsPaperInTransferringEdgeQueue(transferA),
                "a disconnecting monitor made a transferring member invisible to the freeze set");
            Require(
                Math.Abs(controller.DeepCapsuleStartTopMarginFor(transferA) - 120) < 1e-6,
                "disconnecting a monitor fell back to the primary queue margin");
            controller.ArrangeDeepCapsules(animate: false);

            controller.AbandonEdgeCapsuleQueueTransfer(disconnectTransfer);
            Require(
                Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[persistentKey] - 120) < 1e-6 &&
                !controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qdisp"),
                "cancelling after a disconnect rewrote the queue margin");

            // 空 id 队列：断连副屏与主屏同侧必须保持不同身份。
            transferA.CapsuleMonitorDeviceName = disconnectedMonitor;
            transferA.CapsuleQueueId = "";
            transferB.CapsuleMonitorDeviceName = "";
            transferB.CapsuleQueueId = "";
            Require(
                controller.QueueKeyForPaper(transferA) != controller.QueueKeyForPaper(transferB),
                "empty-id queues on a disconnected monitor collapsed into the primary queue");
            Require(
                controller.QueueKeyForPaper(transferA) == disconnectedMonitor + "|right",
                "a disconnected empty-id queue lost its persisted edge identity");

            // 断连显示器折叠到主屏 live edge 后，单成员 Split 仍必须复用持久 id 并保留原 monitor tag。
            var splitPaper = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
            splitPaper.CapsuleMonitorDeviceName = disconnectedMonitor;
            splitPaper.CapsuleSide = DeepCapsuleSides.Left;
            splitPaper.CapsuleQueueId = "qsplit";
            controller.State.DeepCapsuleQueueStartTopMargins[disconnectedMonitor + "|left#qsplit"] = 160;
            controller.ArrangeDeepCapsules(animate: false);
            var disconnectSplitAnchorY = geometry.WorkArea.Top + 500 * geometry.DpiScaleY;
            var disconnectSplitAnchor = new EdgeCapsuleDropAnchor(
                new DeviceScreenPoint(geometry.WorkArea.Left + 10, disconnectSplitAnchorY),
                disconnectSplitAnchorY);
            Require(
                controller.CommitDeepCapsuleQueueDrop(splitPaper, disconnectSplitAnchor),
                "the disconnected same-edge split was rejected");
            Require(
                splitPaper.CapsuleMonitorDeviceName == disconnectedMonitor &&
                splitPaper.CapsuleQueueId == "qsplit",
                "a same-live-edge split rewrote the persisted monitor or queue identity");
            Require(
                controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(
                    disconnectedMonitor + "|left#qsplit"),
                "a same-live-edge split did not persist the margin under the persistent key");
        }
        finally
        {
            cacheField.SetValue(null, originalMonitorCache);
        }

        // FQ-04 防御：targetMembers 的 anchor 恰好属于 moving 集合时，整块仍必须被插入，
        // 不能因为循环里的 moving skip 而让纸片从 State.Papers 中消失。
        var papersBeforeAnchorGuard = controller.State.Papers;
        try
        {
            var moving = new[]
            {
                controller.State.Papers.Single(paper => paper.Id == "fixture-2"),
                controller.State.Papers.Single(paper => paper.Id == "fixture-3")
            };
            var anchorTargetMembers = new List<PaperData>
            {
                moving[0],
                controller.State.Papers.Single(paper => paper.Id == "fixture-0"),
                controller.State.Papers.Single(paper => paper.Id == "fixture-1")
            };
            Call(controller, "InsertPaperBesideQueues", moving, anchorTargetMembers, 0);
            var expectedIds = papersBeforeAnchorGuard
                .Select(paper => paper.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var actualIds = controller.State.Papers
                .Select(paper => paper.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            Require(
                expectedIds.SequenceEqual(actualIds),
                "an anchor inside the moving set dropped part of the carried block");
        }
        finally
        {
            controller.State.Papers = papersBeforeAnchorGuard;
            controller.ArrangeDeepCapsules(animate: false);
        }

        // FQ-10：FloatingTransfer 阶段宿主异常关闭必须取消手势，不能留下 arrange gate、
        // 浮动宿主引用或鼠标光标等永久脏状态。
        var recoveryWindow = windows["fixture-0"];
        Require(
            !recoveryWindow.IsDeepCapsuleReorderDragInProgress,
            "the host-close fixture started with an active drag");
        var presenter = Field(recoveryWindow, "_edgeCapsule")!;
        var modelProperty = presenter.GetType().GetProperty(
            "Model", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var model = (EdgeCapsuleModel)modelProperty.GetValue(presenter)!;
        var recoveryPoint = new DeviceScreenPoint(
            geometry.WorkArea.Left + 40, geometry.WorkArea.Top + 40);
        modelProperty.SetValue(presenter, model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.FloatingTransfer },
            DragSession = EdgeCapsuleDragSession.Begin(recoveryPoint)
        });

        var hostShape = new EdgeCapsuleFloatingShape(
            true,
            EdgeCapsuleSurfaceKind.FloatingFree,
            200,
            46,
            46,
            12,
            true);
        var options = (EdgeCapsuleDragWindowOptions)Call(
            recoveryWindow, "CreateDeepCapsuleFloatingDragHostOptions", hostShape)!;
        var host = EdgeCapsuleDragWindow.Rent(options);
        SetField(recoveryWindow, "_deepCapsuleFloatingDragHost", host);
        try
        {
            Invoke(
                recoveryWindow,
                "OnDeepCapsuleFloatingDragHostUnexpectedlyClosed",
                host,
                EventArgs.Empty);
            Require(
                !recoveryWindow.IsDeepCapsuleReorderDragInProgress,
                "an unexpected host close during FloatingTransfer left the drag active");
            Require(
                Field(recoveryWindow, "_deepCapsuleFloatingDragHost") == null,
                "an unexpected host close left the floating host reference behind");
            Require(
                Mouse.OverrideCursor == null,
                "an unexpected host close left the drag cursor overridden");
            Require(
                !(bool)Call(controller, "HasDeepCapsuleReorderDragInProgress")!,
                "an unexpected host close left the controller's arrange gate closed");
        }
        finally
        {
            if (Field(recoveryWindow, "_deepCapsuleFloatingDragHost") is EdgeCapsuleDragWindow pendingHost)
            {
                pendingHost.ReturnToPool();
            }
        }
    }

    private static object? Call(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, Private)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        return method.Invoke(target, args);
    }

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, Private)
         ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, Private)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        _ = method.Invoke(target, args);
    }

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Part(window, "_edgeCapsule")).Placement;

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target);

    private static object Value(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);

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
