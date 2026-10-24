using PaperTodo;

/// <summary>自由队列核心回归：普通取消保持逐成员身份，提交异常完整回滚。</summary>
internal static class EdgeCapsuleQueueRegressionChecks
{
    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Program.Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "free-queue regression fixture slots did not become visible");

        CancelIdentityPreservation(controller);
        CommitRollback(controller);
        SingleCommitRollback(controller);
    }

    /// <summary>
    /// 普通取消没有修改业务数据，不能把被拖成员的显示器标记广播给整队。
    /// 这里构造“同一运行队列混合原始显示器标记”的场景（模拟运行中切换主屏），
    /// 旧实现会在取消后把两种标记统一成被拖成员的值。
    /// </summary>
    private static void CancelIdentityPreservation(AppController controller)
    {
        Program.Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry) &&
            geometry.DeviceName.Length > 0,
            "primary monitor geometry is unavailable for the cancel identity fixture");

        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var sibling = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        var sourceKey = controller.QueueKeyForPaper(source);

        sibling.CapsuleMonitorDeviceName = geometry.DeviceName;
        Program.Require(
            controller.QueueKeyForPaper(sibling) == sourceKey,
            "the mixed monitor tags did not normalize to one runtime queue");

        Program.Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(source, out var snapshot) &&
            snapshot != null,
            "the cancel identity fixture did not start");
        var transfer = snapshot!;
        Program.Require(
            transfer.MemberIdentities.Single(identity => identity.PaperId == source.Id)
                .MonitorDeviceName == "" &&
            transfer.MemberIdentities.Single(identity => identity.PaperId == sibling.Id)
                .MonitorDeviceName == geometry.DeviceName,
            "the transfer snapshot did not capture per-member monitor identities");

        controller.AbandonEdgeCapsuleQueueTransfer(transfer);

        Program.Require(
            source.CapsuleMonitorDeviceName == "" &&
            sibling.CapsuleMonitorDeviceName == geometry.DeviceName,
            "an ordinary cancel rewrote a member's monitor tag");
        Program.Require(
            controller.QueueKeyForPaper(source) == sourceKey &&
            controller.QueueKeyForPaper(sibling) == sourceKey,
            "an ordinary cancel rewrote the runtime queue identity");
        Program.Require(
            !controller.IsEdgeCapsuleQueueTransferActive,
            "an ordinary cancel left the queue transfer frozen");

        sibling.CapsuleMonitorDeviceName = "";
        controller.ArrangeDeepCapsules(animate: false);
    }

    /// <summary>
    /// FQ-14（残余）：整队提交在「改身份 → 重排 State.Papers → 持久化 margin → SaveNow」
    /// 区间内抛异常时，必须同时回滚 State.Papers 顺序与目标 / 邻队 / 源队列 margin，
    /// 而不只是恢复成员身份（RollbackEdgeCapsuleQueueTransfer 的统一能力）。
    /// </summary>
    private static void CommitRollback(AppController controller)
    {
        Program.Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry is unavailable for the commit rollback fixture");

        // qa = [fixture-0, fixture-1]，qc = [fixture-4]。把 qa 整队并入 qc，
        // 落点用真实工作区坐标，保证磁吸走到 MergeTransferredQueueInto 的 mutation 区间。
        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var sibling = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        var sourceKey = controller.QueueKeyForPaper(source);
        var targetKey = controller.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-4"));

        // 混合原始显示器标记：回滚必须逐成员恢复，而不是把 source 的 "" 写给整队。
        Program.Require(
            geometry.DeviceName.Length > 0,
            "primary monitor geometry has no device name for the rollback fixture");
        sibling.CapsuleMonitorDeviceName = geometry.DeviceName;
        Program.Require(
            controller.QueueKeyForPaper(sibling) == sourceKey,
            "the mixed monitor tags did not normalize to one runtime queue");

        Program.Require(
            controller.TryBeginEdgeCapsuleQueueTransfer(source, out var snapshot) && snapshot != null,
            "whole-queue transfer did not start");
        var transfer = snapshot!;

        var carriedIds = transfer.PaperIds.ToArray();
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

        var committed = controller.CommitEdgeCapsuleQueueTransfer(transfer, anchor, targetKey, -1);
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

        sibling.CapsuleMonitorDeviceName = "";
        controller.ArrangeDeepCapsules(animate: false);
    }

    /// <summary>单颗提交中途异常时，顺序、margin 与身份都必须完整回滚。</summary>
    private static void SingleCommitRollback(AppController controller)
    {
        Program.Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry is unavailable for the single commit rollback fixture");

        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-0");
        var target = controller.State.Papers.Single(paper => paper.Id == "fixture-4");
        var sourceKey = controller.QueueKeyForPaper(source);
        var targetKey = controller.QueueKeyForPaper(target);
        Program.Require(
            sourceKey != targetKey,
            "the single rollback fixture needs two distinct queues");

        var orderBefore = controller.State.Papers.Select(paper => paper.Id).ToArray();
        var marginsBefore = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var identityBefore = (
            source.CapsuleMonitorDeviceName,
            source.CapsuleSide,
            source.CapsuleQueueId);

        var dropY = geometry.WorkArea.Top + 470 * geometry.DpiScaleY;
        var anchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, dropY),
            dropY);

        controller.EdgeCapsuleQueueTransferCommitFailureInjector =
            () => throw new InvalidOperationException("injected single commit failure");

        var committed = controller.CommitDeepCapsuleQueueDrop(source, anchor, targetKey, 0);
        controller.EdgeCapsuleQueueTransferCommitFailureInjector = null;

        Program.Require(!committed, "an injected single-commit failure reported success");
        Program.Require(
            controller.State.Papers.Select(paper => paper.Id).SequenceEqual(orderBefore),
            "State.Papers order was not rolled back after a single-commit failure");
        Program.Require(
            controller.State.DeepCapsuleQueueStartTopMargins.Count == marginsBefore.Count &&
            marginsBefore.All(pair => controller.State.DeepCapsuleQueueStartTopMargins
                .TryGetValue(pair.Key, out var now) && Math.Abs(now - pair.Value) < 0.001),
            "queue start margins were not rolled back after a single-commit failure");
        Program.Require(
            source.CapsuleMonitorDeviceName == identityBefore.Item1 &&
            source.CapsuleSide == identityBefore.Item2 &&
            source.CapsuleQueueId == identityBefore.Item3,
            "the dragged capsule kept a partial identity after a single-commit failure");
        Program.Require(
            controller.QueueKeyForPaper(source) == sourceKey,
            "the dragged capsule left its source queue after a single-commit failure");

        controller.ArrangeDeepCapsules(animate: false);
    }
}
