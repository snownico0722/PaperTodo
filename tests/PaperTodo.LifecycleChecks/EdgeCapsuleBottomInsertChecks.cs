using System.Reflection;
using System.Windows;
using PaperTodo;

/// <summary>
/// 贴底目标队列扩容时的头插/尾插：磁吸距离必须使用插入预览的几何（top 随 margin 夹取上移），
/// 而 NewQueue 避让仍按当前占位。
/// </summary>
internal static class EdgeCapsuleBottomInsertChecks
{
    private const BindingFlags Private =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Program.Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "bottom-insert fixture slots did not become visible");

        Program.Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "bottom-insert fixture monitor geometry was unavailable");

        var area = geometry.LocalWorkAreaDip;
        var gap = controller.DeepCapsuleGap;
        var slot = EdgeCapsuleLayout.SlotHeight(gap);
        var height = PaperLayoutDefaults.CapsuleHeight;

        var source = new[] { "fixture-0", "fixture-1", "fixture-2" }
            .Select(id => controller.State.Papers.Single(paper => paper.Id == id))
            .ToArray();
        var target = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
        var targetKey = controller.QueueKeyForPaper(target);
        var margin = controller.DeepCapsuleStartTopMarginForQueueKey(targetKey);
        var currentTop = EdgeCapsuleLayout.TopForIndex(0, margin, area, 1, gap);
        var previewTop = EdgeCapsuleLayout.TopForIndex(0, margin, area, 4, gap);
        Program.Require(
            previewTop < currentTop,
            "the bottom-anchored target did not move upward when the carried members were inserted");

        double DeviceCenterY(double offsetDip) =>
            geometry.WorkArea.Top + (previewTop + offsetDip) * geometry.DpiScaleY;

        EdgeCapsuleDropAnchor Anchor(double offsetDip)
        {
            var y = DeviceCenterY(offsetDip);
            return new EdgeCapsuleDropAnchor(
                new DeviceScreenPoint(geometry.WorkArea.Right - 10, y),
                y);
        }

        var headAnchor = Anchor(height / 2.0);
        var tailAnchor = Anchor(slot + height / 2.0);

        var head = controller.EvaluateDeepCapsuleQueueDrag(
            source, headAnchor, excludeEntireSourceQueue: true);
        Program.Require(
            head.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
            head.TargetQueueKey == targetKey,
            "the preview's head slot did not stay inside the magnet");

        var tail = controller.EvaluateDeepCapsuleQueueDrag(
            source, tailAnchor, excludeEntireSourceQueue: true);
        Program.Require(
            tail.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
            tail.TargetQueueKey == targetKey,
            "the preview's tail slot did not stay inside the magnet");

        var excludedIds = source.Select(paper => paper.Id).ToArray();
        Program.Require(
            controller.DeepCapsuleQueueInsertIndexFor(
                targetKey,
                source[0].Id,
                headAnchor.CapsuleCenterYDevice,
                source.Length,
                excludedIds) == 0,
            "the head anchor did not resolve to insert index 0");
        Program.Require(
            controller.DeepCapsuleQueueInsertIndexFor(
                targetKey,
                source[0].Id,
                tailAnchor.CapsuleCenterYDevice,
                source.Length,
                excludedIds) == 1,
            "the tail anchor did not resolve to insert index 1");

        Program.Require(
            controller.PreviewDeepCapsuleQueueDrag(source, targetKey, 0),
            "the head-insertion preview was rejected");
        Program.Require(
            Placement(windows["fixture-3"]).Index == 3 &&
            Placement(windows["fixture-3"]).SlotCount == 4,
            "head insertion did not move the target member to the last slot");
        // 携带成员在真实拖拽中由浮空胶囊呈现，不会获得 docked placement；
        // 这里只验证目标队列剩下的成员被放进了正确的槽位，且整队确实上移到插入后的 top。
        Program.Require(
            Math.Abs(EffectiveTop(windows["fixture-3"], margin, area, gap) -
                     (previewTop + 3 * slot)) < 1e-6,
            "head insertion did not place the target member at the last slot of the raised queue");

        Program.Require(
            controller.PreviewDeepCapsuleQueueDrag(source, targetKey, 1),
            "the tail-insertion preview was rejected");
        Program.Require(
            Placement(windows["fixture-3"]).Index == 0 &&
            Placement(windows["fixture-3"]).SlotCount == 4,
            "tail insertion did not keep the target member in the first slot");
        Program.Require(
            Math.Abs(EffectiveTop(windows["fixture-3"], margin, area, gap) - previewTop) < 1e-6,
            "tail insertion did not raise the target queue to the insertion top");
    }

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Part(window, "_edgeCapsule")).Placement;

    private static double EffectiveTop(
        PaperWindow window,
        double margin,
        Rect area,
        double gap)
    {
        var placement = Placement(window);
        return EdgeCapsuleLayout.TopForIndex(
            placement.VisualIndex,
            margin,
            area,
            placement.SlotCount,
            gap) + placement.TopOffsetDip;
    }

    private static object Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);
}
