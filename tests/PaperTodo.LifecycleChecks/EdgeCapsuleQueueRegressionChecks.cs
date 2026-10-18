using System.Reflection;
using PaperTodo;

/// <summary>
/// 自由队列审查条目 FQ-12 / FQ-14 / FQ-15 的行为回归。
///
/// 这些检查只调用控制器原语，不驱动真实指针，因此可以在无交互会话中稳定运行。
/// </summary>
internal static class EdgeCapsuleQueueRegressionChecks
{
    private const BindingFlags Private =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Program.Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "free-queue regression fixture slots did not become visible");

        MarginGuard(controller);
        OwnerIsolation(controller);
        CommitRollback(controller);
    }

    /// <summary>
    /// FQ-15：自由队列开启时 SetDeepCapsuleStartTopMargin 写的是裸 edge key，而渲染读的是
    /// 持久实例 key（"|side#id"）——写入既无视觉反馈，又留下永远读不到的陈旧裸 margin。
    /// 守卫必须挡住 free 场景。
    ///
    /// 同时锁住反向护栏：主胶囊模式（free 关闭）必须**仍然能写**。守卫若误加
    /// UseCapsuleCollapseAll（该字段默认 true，且主胶囊仅在它为 true 时才存在），
    /// 会让本方法在生产中永远立即返回——这条断言专门防它被重新写错。
    /// </summary>
    private static void MarginGuard(AppController controller)
    {
        var bareRightKey = "|" + DeepCapsuleSides.Right;

        controller.State.UseCapsuleFreeQueues = true;
        controller.State.DeepCapsuleQueueStartTopMargins.Remove(bareRightKey);
        controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, 123);
        Program.Require(
            !controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(bareRightKey),
            "free-queue mode wrote a bare edge margin that no free queue can ever read back");

        // 反向护栏：master 模式下必须照常写入裸 key（slot 0 归主胶囊，key 语义正确）。
        controller.State.UseCapsuleFreeQueues = false;
        controller.State.UseCapsuleCollapseAll = true;
        controller.State.DeepCapsuleQueueStartTopMargins.Remove(bareRightKey);
        controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, 123);
        Program.Require(
            controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(bareRightKey, out var written),
            "master mode stopped writing its start margin — the guard must not block collapse-all");
        Program.Require(Math.Abs(written - 123) < 0.5, "master margin was not applied verbatim");
        controller.State.DeepCapsuleQueueStartTopMargins.Remove(bareRightKey);

        // 还原夹具状态：后续 commit 回滚检查需要自由队列处于开启态。
        controller.State.UseCapsuleFreeQueues = true;
        controller.State.UseCapsuleCollapseAll = false;
    }

    /// <summary>
    /// FQ-12：共享磁吸 / 描边状态是 controller 级单例。拖拽进行中，另一张无关纸片因
    /// 隐藏 / 关闭 / 折叠调用清理时，早退分支不得把 owner 建立的 held key 与描边擦掉。
    /// </summary>
    private static void OwnerIsolation(AppController controller)
    {
        var owner = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var bystander = controller.State.Papers.Single(paper => paper.Id == "fixture-3");

        controller.BeginDeepCapsuleReorderDrag(owner);
        SetHeldQueueKey(controller, controller.QueueKeyForPaper(owner));
        Program.Require(HeldQueueKey(controller).Length > 0, "owner drag did not latch a held queue key");

        // 无关纸片清理：必须被 owner 校验拦下。
        controller.EndDeepCapsuleQueueMagnetPreview(bystander.Id);
        Program.Require(
            HeldQueueKey(controller).Length > 0,
            "an unrelated paper's cancel wiped the live gesture's held queue key");

        // 无关纸片的 Forget 同样必须被拦下。
        controller.ForgetDeepCapsuleQueueDrag(bystander.Id);
        Program.Require(
            HeldQueueKey(controller).Length > 0,
            "an unrelated paper's Forget dropped the live gesture's held queue key");

        // owner 自己清理：必须真正生效，否则共享状态永远不释放。
        controller.EndDeepCapsuleQueueMagnetPreview(owner.Id);
        Program.Require(
            HeldQueueKey(controller).Length == 0,
            "the owner's own teardown no longer clears the held queue key");

        // 非手势状态下 owner 已释放，后续无关清理不应误判为「有 owner」。
        controller.EndDeepCapsuleQueueMagnetPreview(bystander.Id);
    }

    /// <summary>
    /// FQ-14（残余）：整队提交在「改身份 → 重排 State.Papers → 持久化 margin → SaveNow」
    /// 区间内抛异常时，必须同时回滚 State.Papers 顺序与目标 / 邻队 / 源队列 margin，
    /// 而不只是恢复成员身份（CancelEdgeCapsuleQueueTransfer 的既有能力）。
    /// </summary>
    private static void CommitRollback(AppController controller)
    {
        Program.Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry is unavailable for the commit rollback fixture");

        // qa = [fixture-0, fixture-1]，qc = [fixture-4]。把 qa 整队并入 qc，
        // 落点用真实工作区坐标，保证磁吸走到 MergeTransferredQueueInto 的 mutation 区间。
        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var sourceKey = controller.QueueKeyForPaper(source);
        var targetKey = controller.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-4"));

        Program.Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(source, out var snapshot) && snapshot != null,
            "whole-queue transfer did not start");

        var carriedIds = snapshot.PaperIds.ToArray();
        Program.Require(carriedIds.Length > 0, "whole-queue snapshot carried no members");

        var orderBefore = controller.State.Papers.Select(paper => paper.Id).ToArray();
        var marginsBefore = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var identityBefore = carriedIds.ToDictionary(
            id => id,
            id =>
            {
                var paper = controller.State.Papers.Single(p => p.Id == id);
                return (paper.CapsuleMonitorDeviceName, paper.CapsuleSide, paper.CapsuleQueueId);
            },
            StringComparer.Ordinal);

        var dropY = geometry.WorkArea.Top + 470 * geometry.DpiScaleY;
        var anchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, dropY),
            dropY);

        // 在 commit 的 mutation 区间中途注入异常。
        controller.EdgeCapsuleQueueTransferCommitFailureInjector =
            () => throw new InvalidOperationException("injected commit failure");

        var committed = controller.CommitEdgeCapsuleQueueTransfer(snapshot, anchor, targetKey, -1);
        controller.EdgeCapsuleQueueTransferCommitFailureInjector = null;

        Program.Require(!committed, "an injected mid-commit failure still reported a successful commit");

        Program.Require(
            controller.State.Papers.Select(paper => paper.Id).SequenceEqual(orderBefore),
            "State.Papers order was not rolled back after a mid-commit failure");
        Program.Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Count == marginsBefore.Count &&
            marginsBefore.All(pair => controller.State.DeepCapsuleQueueStartTopMargins
                .TryGetValue(pair.Key, out var now) && Math.Abs(now - pair.Value) < 0.001),
            "queue start margins were not rolled back after a mid-commit failure");
        foreach (var pair in identityBefore)
        {
            var paper = controller.State.Papers.Single(p => p.Id == pair.Key);
            Program.Require(
                paper.CapsuleMonitorDeviceName == pair.Value.Item1 &&
                paper.CapsuleSide == pair.Value.Item2 &&
                paper.CapsuleQueueId == pair.Value.Item3,
                $"carried member {pair.Key} kept a partial identity after a mid-commit failure");
        }
        Program.Require(
            !controller.IsEdgeCapsuleQueueTransferActive,
            "the transfer freeze was not released after a mid-commit failure");
        Program.Require(
            controller.QueueKeyForPaper(source) == sourceKey,
            "the source queue key changed after a mid-commit failure");
    }

    private static string HeldQueueKey(AppController controller) =>
        (string)HeldQueueKeyField(controller).GetValue(controller)!;

    private static void SetHeldQueueKey(AppController controller, string value) =>
        HeldQueueKeyField(controller).SetValue(controller, value);

    private static FieldInfo HeldQueueKeyField(AppController controller) =>
        controller.GetType().GetField("_deepCapsuleDragHeldQueueKey", Private)
        ?? throw new InvalidOperationException(
            "AppController is missing the held-queue-key field the FQ-12 check reads");
}
