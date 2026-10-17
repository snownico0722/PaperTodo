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

        // 重新进入同一目标必须能再次开槽；交互层若残留 preview key，会在这里暴露。
        controller.PreviewDeepCapsuleQueueDrag([source], targetKey, 1);
        Require(
            Placement(windows["fixture-3"]).Index == 2 &&
            Placement(windows["fixture-3"]).SlotCount == 3,
            "re-entering the same target did not open its slot again");
    }

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Part(window, "_edgeCapsule")).Placement;

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
