using System.Diagnostics;
using PaperTodo;

/// <summary>
/// 验证拖放提交不会把运行期静止避让位移写回持久 margin。
/// target 应保存不含避让的基准位置；目标下方被求解器下推的队列应保留原偏好，
/// 由提交后的静止避让按新成员数复现屏幕位置。
/// </summary>
internal static class EdgeCapsuleRestingDeltaPersistChecks
{
    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows,
        StateStore store)
    {
        await Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "resting-delta fixture slots did not become visible");

        var source = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
        var targetPaper = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        var targetKey = controller.QueueKeyForPaper(targetPaper);
        var qaKey = controller.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-0"));
        var qcKey = controller.QueueKeyForPaper(
            controller.State.Papers.Single(paper => paper.Id == "fixture-2"));

        Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "primary monitor geometry was not resolved");

        // 预期值完全由当前 gap / 胶囊高度推导，避免依赖固定 DIP。
        var gap = controller.DeepCapsuleGap;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(gap);
        var capsuleHeight = PaperLayoutDefaults.CapsuleHeight;
        var qaBottom = 60 + capsuleHeight;
        var qbDelta = Math.Max(0, qaBottom + gap - 90);
        var qbTop = 90 + qbDelta;
        var qbBottom = qbTop + capsuleHeight + slotHeight;
        var qcDelta = Math.Max(0, qbBottom + gap - 130);

        var anchorY = geometry.WorkArea.Top + 100 * geometry.DpiScaleY;
        var anchor = new EdgeCapsuleDropAnchor(
            new DeviceScreenPoint(geometry.WorkArea.Right - 10, anchorY),
            anchorY);

        Require(
            controller.CommitDeepCapsuleQueueDrop(source, anchor, targetKey, 1),
            "the resting-delta insertion should commit");
        Require(
            controller.QueueKeyForPaper(source) == targetKey,
            "the carried capsule did not join the target queue");

        // target 保存基准 90；qc 被求解器下推但偏好仍是 130；qa 不受影响。
        Require(
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - 90) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qcKey] - 130) < 1e-6 &&
            Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[qaKey] - 60) < 1e-6,
            "a transient resting offset was persisted as the queue's start margin");

        await Until(
            () => Placement(windows["fixture-1"]).SlotCount == 2 &&
                  Placement(windows["fixture-3"]).SlotCount == 2,
            "the committed target queue did not adopt the final slot layout");

        // 保存的是偏好，但提交后的静止避让仍应复现预览位置。
        Require(
            Math.Abs(Placement(windows["fixture-1"]).TopOffsetDip - qbDelta) < 1e-6 &&
            Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip - qcDelta) < 1e-6,
            "the committed layout did not reproduce the preview through resting avoidance");

        controller.SaveNow(sync: true);
        var saved = store.Load();
        Require(
            Math.Abs(saved.DeepCapsuleQueueStartTopMargins[targetKey] - 90) < 1e-6 &&
            Math.Abs(saved.DeepCapsuleQueueStartTopMargins[qcKey] - 130) < 1e-6 &&
            Math.Abs(saved.DeepCapsuleQueueStartTopMargins[qaKey] - 60) < 1e-6,
            "the persisted file contains a transient resting offset");
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
