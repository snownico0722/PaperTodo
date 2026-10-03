using System.Collections;
using System.Reflection;
using PaperTodo;

internal static class MasterQueueMonitorChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const string SourceMonitor = "PAPERTODO-FIXTURE-DISCONNECT";

    internal static void RunFloatingZOrder(AppController controller)
    {
        var masters = (IDictionary)typeof(AppController).GetField("_masterCapsules",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        var master = (MasterCapsuleWindow)masters["|" + DeepCapsuleSides.Right]!;
        var hostField = typeof(MasterCapsuleWindow).GetField("_floatingDragHost",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = controller.State.ExperimentalDockedCapsulesNonTopmost;
        EdgeCapsuleDragWindow? host = null;
        try
        {
            controller.State.ExperimentalDockedCapsulesNonTopmost = true;
            controller.RefreshFloatingSurfaceZOrder();
            Require(controller.TryCreateMasterQueueFloatingDragHostOptions(
                    "", EdgeCapsuleEdge.Right, "▾", "5", out var options),
                "floating z-order fixture did not create drag options");
            host = EdgeCapsuleDragWindow.Rent(options);
            Require(!host.Topmost, "fixture did not start with docked non-topmost options");
            hostField.SetValue(master, host);
            controller.RefreshFloatingSurfaceZOrder();
            Require(!master.Topmost && host.Topmost,
                "master floating drag inherited docked-only non-topmost policy");
        }
        finally
        {
            hostField.SetValue(master, null);
            host?.ReturnToPool();
            controller.State.ExperimentalDockedCapsulesNonTopmost = previous;
            controller.RefreshFloatingSurfaceZOrder();
        }
    }

    internal static void Run(AppController controller)
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var primary),
            "disconnect fixture needs a real primary monitor");
        var cacheField = typeof(WindowWorkAreaHelper).GetField("_cachedMonitors", PrivateStatic)!;
        var originalCache = cacheField.GetValue(null)!;
        var entries = ((IEnumerable)originalCache).Cast<object>().ToArray();
        var entryType = entries[0].GetType();
        var primaryEntry = entries.First(entry => (bool)Value(entry, "IsPrimary"));
        var fakeEntry = Activator.CreateInstance(entryType,
            Value(primaryEntry, "Handle"), SourceMonitor,
            Value(primaryEntry, "WorkArea"), false,
            Value(primaryEntry, "DpiScaleX"), Value(primaryEntry, "DpiScaleY"))!;
        var connected = Array.CreateInstance(entryType, entries.Length + 1);
        for (var i = 0; i < entries.Length; i++) connected.SetValue(entries[i], i);
        connected.SetValue(fakeEntry, entries.Length);

        var sourceKey = SourceMonitor + "|" + DeepCapsuleSides.Right;
        var fallbackKey = "|" + DeepCapsuleSides.Right;
        const double originalMargin = 73;
        MasterCapsuleQueueTransferSnapshot? pending = null;
        try
        {
            // Add only a cached name/identity, using the real screen's geometry and HWND monitor
            // handle. This exercises connected -> disconnected resolution without changing the
            // machine's display settings or requiring a second physical monitor.
            foreach (var paper in controller.State.Papers)
            {
                paper.CapsuleMonitorDeviceName = SourceMonitor;
                paper.CapsuleSide = DeepCapsuleSides.Right;
            }
            controller.State.DeepCapsuleQueueStartTopMargins[sourceKey] = originalMargin;
            cacheField.SetValue(null, connected);
            controller.ArrangeDeepCapsules(animate: false);
            cacheField.SetValue(null, connected);
            Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    SourceMonitor, EdgeCapsuleEdge.Right, originalMargin, out var cancelSnapshot),
                "disconnect cancellation transfer did not start");
            pending = cancelSnapshot;
            cacheField.SetValue(null, originalCache);
            controller.CancelMasterCapsuleQueueTransfer(cancelSnapshot);
            pending = null;
            Require(controller.State.Papers.All(paper => paper.CapsuleMonitorDeviceName == SourceMonitor),
                "cancelling after disconnect changed the persisted monitor tag");
            Require(controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(sourceKey, out var restored) &&
                    Math.Abs(restored - originalMargin) < 0.01 &&
                    !controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(fallbackKey),
                "disconnect cancellation restored the source anchor into the fallback queue");
            Require(controller.State.CapsuleCollapseAllActiveQueues.Count == 0,
                "disconnect cancellation leaked temporary retraction into the fallback queue");

            cacheField.SetValue(null, connected);
            controller.ArrangeDeepCapsules(animate: false);
            cacheField.SetValue(null, connected);
            Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    SourceMonitor, EdgeCapsuleEdge.Right, originalMargin, out var commitSnapshot),
                "disconnect commit transfer did not start");
            pending = commitSnapshot;
            cacheField.SetValue(null, originalCache);
            var drop = new DeviceScreenPoint(
                primary.WorkArea.Left + primary.WorkArea.Width * 3 / 4.0,
                primary.WorkArea.Top + primary.WorkArea.Height / 3.0);
            Require(controller.CommitMasterCapsuleQueueTransfer(commitSnapshot, drop),
                "disconnect same-edge transfer did not commit");
            pending = null;
            var targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(primary.DeviceName);
            Require(controller.State.Papers.All(paper =>
                    paper.CapsuleMonitorDeviceName == targetMonitor &&
                    paper.CapsuleSide == DeepCapsuleSides.Right),
                "same-edge drop after disconnect retained the disconnected source monitor tags");
            Require(!controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(sourceKey) &&
                    controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(fallbackKey),
                "disconnect commit did not migrate the stable source anchor");
            Require(controller.State.CapsuleCollapseAllActiveQueues.Count == 0,
                "disconnect commit leaked temporary retraction into the fallback queue");
        }
        finally
        {
            cacheField.SetValue(null, originalCache);
            if (pending is { } snapshot) controller.CancelMasterCapsuleQueueTransfer(snapshot);
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
        }
    }

    private static object Value(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
