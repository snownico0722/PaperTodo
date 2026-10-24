using System.Diagnostics;
using System.Reflection;
using PaperTodo;

/// <summary>
/// P2：自由队列常规排列的跨队列静止避让。
///
/// 覆盖：同侧默认队列与 qb 重叠时整队下推、已清空的 qc 不动、持久 margin 不被改写；
/// 以及副屏断开后队列几何回退主屏时加入同一 live edge 的避让链。
/// </summary>
internal static class EdgeCapsuleRestingLayoutChecks
{
    private const BindingFlags Private =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "rest-avoidance fixture slots did not become visible");

        Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "rest-avoidance fixture monitor geometry was unavailable");

        var area = geometry.LocalWorkAreaDip;
        var gap = controller.DeepCapsuleGap;
        var compact = PaperLayoutDefaults.CapsuleHeight;
        var defaultMargin = controller.DeepCapsuleStartTopMarginForQueueKey("|right");
        var qbMargin = controller.DeepCapsuleStartTopMarginForQueueKey("|right#qb");
        var defaultBottom =
            EdgeCapsuleLayout.TopForIndex(1, defaultMargin, area, 2, gap) + compact;
        var qbTop = EdgeCapsuleLayout.TopForIndex(0, qbMargin, area, 2, gap);
        var expected = defaultBottom + gap - qbTop;
        Require(expected > 1, "the rest-avoidance fixture was expected to overlap");

        var marginsBefore = controller.State.DeepCapsuleQueueStartTopMargins
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var identities = new[] { "fixture-0", "fixture-1", "fixture-2", "fixture-3", "fixture-4" }
            .ToDictionary(
                id => id,
                id => QueueIdentity(
                    controller,
                    controller.State.Papers.Single(paper => paper.Id == id)),
                StringComparer.Ordinal);

        controller.ArrangeDeepCapsules(animate: false);

        Require(
            Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip - expected) < 1e-6 &&
            Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip - expected) < 1e-6,
            "the overlapping queue was not shifted uniformly below its neighbour");
        Require(
            Math.Abs(Placement(windows["fixture-0"]).TopOffsetDip) < 1e-6 &&
            Math.Abs(Placement(windows["fixture-4"]).TopOffsetDip) < 1e-6,
            "rest avoidance moved queues that were already clear");
        RequireMarginsUnchanged(controller, marginsBefore);
        Require(
            identities.All(pair =>
                QueueIdentity(controller, controller.State.Papers.Single(paper => paper.Id == pair.Key)) ==
                pair.Value),
            "rest avoidance rewrote a queue identity");

        // 断屏变体：持久身份与 live edge 必须分离。
        var cacheField = typeof(WindowWorkAreaHelper).GetField(
            "_cachedMonitors",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalCache = cacheField.GetValue(null)!;
        const string disconnectedMonitor = "PAPERTODO-FIXTURE-REST-AVOIDANCE";
        var disconnectedKey = disconnectedMonitor + "|right#qdisp";
        try
        {
            var entries = ((System.Collections.IEnumerable)originalCache)
                .Cast<object>()
                .ToArray();
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

            controller.State.DeepCapsuleQueueStartTopMargins[disconnectedKey] = 60;
            foreach (var id in new[] { "fixture-2", "fixture-3" })
            {
                var paper = controller.State.Papers.Single(candidate => candidate.Id == id);
                paper.CapsuleMonitorDeviceName = disconnectedMonitor;
                paper.CapsuleSide = DeepCapsuleSides.Right;
                paper.CapsuleQueueId = "qdisp";
            }

            // 副屏仍连接：不同 live edge，不得与主屏队列互相推动。
            cacheField.SetValue(null, connected);
            controller.ArrangeDeepCapsules(animate: false);
            Require(
                Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip) < 1e-6 &&
                Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip) < 1e-6,
                "queues on different connected monitors must not avoid each other");
            Require(
                controller.QueueKeyForPaper(
                    controller.State.Papers.Single(paper => paper.Id == "fixture-2")) ==
                disconnectedKey,
                "a connected monitor discarded the persistent queue identity");

            // 断开副屏：几何回退主屏，同一 live edge 必须重新参与避让。
            cacheField.SetValue(null, originalCache);
            controller.ArrangeDeepCapsules(animate: false);
            Require(
                Math.Abs(Placement(windows["fixture-2"]).TopOffsetDip - expected) < 1e-6 &&
                Math.Abs(Placement(windows["fixture-3"]).TopOffsetDip - expected) < 1e-6,
                "a disconnected queue did not join the primary edge avoidance chain");
            Require(
                controller.QueueKeyForPaper(
                    controller.State.Papers.Single(paper => paper.Id == "fixture-2")) ==
                disconnectedKey,
                "disconnecting a monitor discarded the persistent queue identity");
            Require(
                Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[disconnectedKey] - 60) < 1e-6,
                "rest avoidance rewrote the disconnected queue margin");
        }
        finally
        {
            cacheField.SetValue(null, originalCache);
            foreach (var id in new[] { "fixture-2", "fixture-3" })
            {
                var paper = controller.State.Papers.Single(candidate => candidate.Id == id);
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = DeepCapsuleSides.Right;
                paper.CapsuleQueueId = "qb";
            }

            controller.ArrangeDeepCapsules(animate: false);
        }
    }

    private static void RequireMarginsUnchanged(
        AppController controller,
        IReadOnlyDictionary<string, double> before)
    {
        var after = controller.State.DeepCapsuleQueueStartTopMargins;
        Require(
            after.Count == before.Count &&
            before.All(pair =>
                after.TryGetValue(pair.Key, out var value) &&
                Math.Abs(value - pair.Value) < 1e-6),
            "rest avoidance rewrote the persisted restart margins");
    }

    private static string QueueIdentity(AppController controller, PaperData paper) =>
        controller.QueueKeyForPaper(paper) + "|" +
        (paper.CapsuleMonitorDeviceName ?? "") + "|" +
        paper.CapsuleSide + "|" +
        paper.CapsuleQueueId;

    private static EdgeCapsulePlacement Placement(PaperWindow window) =>
        ((EdgeCapsulePresenter)Program.Part(window, "_edgeCapsule")).Placement;

    private static object Value(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target) ??
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
