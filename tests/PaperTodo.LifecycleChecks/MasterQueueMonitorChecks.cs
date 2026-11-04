using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using PaperTodo;

internal static class MasterQueueMonitorChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
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

            // A vertical gesture also restores the key captured before a disconnect, not
            // whichever queue the old monitor resolves to when its master is closed.
            var primaryMargin = controller.State.DeepCapsuleQueueStartTopMargins[fallbackKey];
            foreach (var paper in controller.State.Papers) paper.CapsuleMonitorDeviceName = SourceMonitor;
            cacheField.SetValue(null, connected);
            var verticalSourceKey = controller.MasterCapsuleQueueKey(SourceMonitor, EdgeCapsuleEdge.Right);
            controller.State.DeepCapsuleQueueStartTopMargins.Remove(verticalSourceKey);
            controller.SetDeepCapsuleStartTopMargin(SourceMonitor, EdgeCapsuleEdge.Right, 91);
            cacheField.SetValue(null, originalCache);
            controller.RestoreMasterCapsuleQueueStartTopMargin(verticalSourceKey,
                hadStartTopMargin: false, startTopMargin: EdgeCapsuleLayout.StartTopMargin);
            Require(!controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(sourceKey) &&
                    Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[fallbackKey] - primaryMargin) < 0.01,
                "vertical cancellation after disconnect changed the fallback queue's anchor");

            RunReconnect(controller, cacheField, connected, originalCache, primary);
            RunPendingDisconnects(controller, cacheField, connected, originalCache, primary);
            RunStaleBeginRejection(controller, cacheField, connected, originalCache, primary);
            RunFreeQueuePendingDisconnect(controller, cacheField, connected, originalCache, primary);
        }
        finally
        {
            cacheField.SetValue(null, originalCache);
            if (pending is { } snapshot) controller.CancelMasterCapsuleQueueTransfer(snapshot);
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
        }
    }

    private static void RunReconnect(AppController controller, FieldInfo cacheField,
        Array connected, object disconnected, MonitorGeometry primary)
    {
        var targetKey = "|" + DeepCapsuleSides.Right;
        var order = controller.State.Papers.Select(paper => paper.Id).ToArray();
        // The pressed primary queue contains both its own papers and fallback papers from a
        // disconnected display. Reconnecting that display must not split the captured block.
        for (var i = 0; i < controller.State.Papers.Count; i++)
            controller.State.Papers[i].CapsuleMonitorDeviceName = i < 2 ? SourceMonitor : "";
        cacheField.SetValue(null, disconnected);
        controller.State.CapsuleCollapseAllActiveQueues[targetKey] = true;
        controller.ArrangeDeepCapsules(animate: false);
        Require(controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
                controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right), out var snapshot),
            "fallback queue reconnect transfer did not begin");
        try
        {
            Require(snapshot.SourceQueueKey == targetKey && snapshot.PaperIds.SequenceEqual(order),
                "fallback queue did not capture the complete mixed-monitor source");
            cacheField.SetValue(null, connected);
            var drop = new DeviceScreenPoint(primary.WorkArea.Right - 100, primary.WorkArea.Top + 180);
            Require(controller.CommitMasterCapsuleQueueTransfer(snapshot, drop),
                "same-primary-edge transfer after reconnect did not commit");
            Require(controller.State.Papers.Select(paper => paper.Id).SequenceEqual(order) &&
                    controller.State.Papers.All(paper => paper.CapsuleMonitorDeviceName == "" &&
                        paper.CapsuleSide == DeepCapsuleSides.Right),
                "same-key drop after reconnect split the captured queue across monitors");
            Require(controller.State.CapsuleCollapseAllActiveQueues.TryGetValue(targetKey, out var collapsed) &&
                    collapsed && Masters(controller).Count == 1 && Masters(controller).Contains(targetKey),
                "reconnect drop lost the source collapse state or left a second live master");
            Require(MasterCapsuleQueueTransferPolicy.TryResolveTarget(drop, "", EdgeCapsuleEdge.Right,
                    order.Length + 1, controller.DeepCapsuleGap, out var target) &&
                    Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[targetKey] - target.StartTopMargin) < 0.01,
                "reconnect drop lost the captured block's drop height");
        }
        finally { controller.CancelMasterCapsuleQueueTransfer(snapshot); }
    }

    private static void RunPendingDisconnects(AppController controller, FieldInfo cacheField,
        Array connected, object disconnected, MonitorGeometry primary)
    {
        var sourceKey = SourceMonitor + "|" + DeepCapsuleSides.Right;
        var primaryKey = "|" + DeepCapsuleSides.Right;
        foreach (var horizontal in new[] { false, true })
        foreach (var hadPrimaryMargin in new[] { false, true })
        foreach (var hadSourceMargin in new[] { false, true })
        {
            controller.State.CapsuleCollapseAllActiveQueues.Clear();
            for (var i = 0; i < controller.State.Papers.Count; i++)
                controller.State.Papers[i].CapsuleMonitorDeviceName = i < 2 ? SourceMonitor : "";
            if (hadSourceMargin) controller.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 73;
            else controller.State.DeepCapsuleQueueStartTopMargins.Remove(sourceKey);
            if (hadPrimaryMargin) controller.State.DeepCapsuleQueueStartTopMargins[primaryKey] = 137;
            else controller.State.DeepCapsuleQueueStartTopMargins.Remove(primaryKey);
            cacheField.SetValue(null, connected);
            controller.ArrangeDeepCapsules(animate: false);
            cacheField.SetValue(null, connected);
            var source = (MasterCapsuleWindow)Masters(controller)[sourceKey]!;
            var expectedPrimaryMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
            Require(controller.TryCreateMasterQueueFloatingDragHostOptions(SourceMonitor,
                    EdgeCapsuleEdge.Right, "▾", "2", out var options), "pending disconnect drag options");
            var host = EdgeCapsuleDragWindow.Rent(options);
            host.ReturnToPool();
            var (pill, session, start) = PressMaster(source);
            Require((string)Value(session, "SourceQueueKey") == sourceKey,
                "MouseDown did not capture the connected secondary queue");

            cacheField.SetValue(null, disconnected);
            var pointer = new DeviceScreenPoint(
                start.X + (horizontal ? EdgeCapsuleLayout.CrossQueueDragUnlockDistance + 20 : 0) * primary.DpiScaleX,
                start.Y + 47 * primary.DpiScaleY);
            // Supply only the pointer sample to the same continuation used by PreviewMouseMove;
            // the real MouseDown owns the session/capture, and no OS mouse state is synthesized.
            Require((bool)typeof(MasterCapsuleWindow).GetMethod("ContinueMasterDrag", Private)!
                    .Invoke(source, [pointer, session])!, "stale master gesture was not consumed");
            Require(!pill.IsMouseCaptured && Field(source, "_dragSession") == null,
                "disconnect before the drag threshold did not cancel the pressed gesture");
            RaiseLeftButton(pill, UIElement.PreviewMouseLeftButtonUpEvent);
            Require(controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(primaryKey) == hadPrimaryMargin &&
                    Math.Abs(controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) -
                        expectedPrimaryMargin) < 0.01 &&
                    controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(sourceKey) == hadSourceMargin &&
                    (!hadSourceMargin || Math.Abs(controller.State.DeepCapsuleQueueStartTopMargins[sourceKey] - 73) < 0.01),
                "stale master gesture changed a primary/source anchor or its absence");
            Require(controller.State.CapsuleCollapseAllActiveQueues.Count == 0 &&
                    controller.State.Papers.Take(2).All(paper => paper.CapsuleMonitorDeviceName == SourceMonitor) &&
                    controller.State.Papers.Skip(2).All(paper => paper.CapsuleMonitorDeviceName == ""),
                "cancelled disconnect gesture toggled collapse or changed queue membership");
            RequireTransferReleased(controller, options, host);
        }
    }

    private static void RunStaleBeginRejection(AppController controller, FieldInfo cacheField,
        Array connected, object disconnected, MonitorGeometry primary)
    {
        cacheField.SetValue(null, connected);
        controller.ArrangeDeepCapsules(animate: false);
        cacheField.SetValue(null, connected);
        var source = (MasterCapsuleWindow)Masters(controller)[SourceMonitor + "|" + DeepCapsuleSides.Right]!;
        Require(controller.TryCreateMasterQueueFloatingDragHostOptions(SourceMonitor,
                EdgeCapsuleEdge.Right, "▾", "2", out var options),
            "stale Begin fixture drag options");
        var host = EdgeCapsuleDragWindow.Rent(options);
        host.ReturnToPool();
        var (pill, session, start) = PressMaster(source);
        cacheField.SetValue(null, disconnected);

        // This used to enter Begin and retire the detached master synchronously.
        // A stale physical edge must now be rejected before renting the floating
        // host or starting a transfer; the real continuation then cancels its press.
        Require(!(bool)typeof(MasterCapsuleWindow).GetMethod("TryRunQueueTransfer", Private)!
                .Invoke(source, [start, session])!,
            "disconnected master incorrectly entered its old queue transfer");
        Require(!host.IsVisible && Field(controller, "_masterCapsuleQueueTransfer") == null &&
                Field(source, "_queueTransferSnapshot") == null,
            "rejecting a stale Begin acquired a floating lease or left a transaction");
        var move = new DeviceScreenPoint(start.X, start.Y + 47 * primary.DpiScaleY);
        Require((bool)typeof(MasterCapsuleWindow).GetMethod("ContinueMasterDrag", Private)!
                .Invoke(source, [move, session])!,
            "stale master gesture was not consumed after rejected Begin");
        Require(!pill.IsMouseCaptured && Field(source, "_dragSession") == null,
            "rejected stale Begin left the old master gesture captured");
        RaiseLeftButton(pill, UIElement.PreviewMouseLeftButtonUpEvent);
        RequireTransferReleased(controller, options, host);
    }

    private static void RunFreeQueuePendingDisconnect(AppController controller, FieldInfo cacheField,
        Array connected, object disconnected, MonitorGeometry primary)
    {
        var priorFreeMode = controller.State.UseCapsuleFreeQueues;
        var priorPapers = controller.State.Papers.Select(p =>
            (Paper: p, Monitor: p.CapsuleMonitorDeviceName, Side: p.CapsuleSide, Id: p.CapsuleQueueId)).ToArray();
        var priorMargins = new Dictionary<string, double>(
            controller.State.DeepCapsuleQueueStartTopMargins, StringComparer.Ordinal);
        var priorCollapse = new Dictionary<string, bool>(
            controller.State.CapsuleCollapseAllActiveQueues, StringComparer.Ordinal);
        try
        {
            controller.State.UseCapsuleFreeQueues = true;
            const string sourceId = "qa";
            const string primaryId = "qb";
            var sourceKey = SourceMonitor + "|" + DeepCapsuleSides.Right + "#" + sourceId;
            var primaryKey = "|" + DeepCapsuleSides.Right + "#" + primaryId;
            foreach (var horizontal in new[] { false, true })
            {
                controller.State.CapsuleCollapseAllActiveQueues.Clear();
                for (var i = 0; i < controller.State.Papers.Count; i++)
                {
                    var paper = controller.State.Papers[i];
                    paper.CapsuleMonitorDeviceName = i < 2 ? SourceMonitor : "";
                    paper.CapsuleSide = DeepCapsuleSides.Right;
                    paper.CapsuleQueueId = i < 2 ? sourceId : primaryId;
                }
                controller.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 73;
                controller.State.DeepCapsuleQueueStartTopMargins[primaryKey] = 137;
                cacheField.SetValue(null, connected);
                controller.ArrangeDeepCapsules(animate: false);
                cacheField.SetValue(null, connected);
                var source = (MasterCapsuleWindow)Masters(controller)[sourceKey]!;
                var (pill, session, start) = PressMaster(source);
                Require((string)Value(session, "SourceQueueKey") == sourceKey,
                    "free master captured its own instance before monitor disconnect");

                cacheField.SetValue(null, disconnected);
                var pointer = new DeviceScreenPoint(
                    start.X + (horizontal ? EdgeCapsuleLayout.CrossQueueDragUnlockDistance + 20 : 0) * primary.DpiScaleX,
                    start.Y + 47 * primary.DpiScaleY);
                Require((bool)typeof(MasterCapsuleWindow).GetMethod("ContinueMasterDrag", Private)!
                        .Invoke(source, [pointer, session])!,
                    "stale free master gesture was not consumed");
                Require(!pill.IsMouseCaptured && Field(source, "_dragSession") == null,
                    "disconnected free master kept ownership of a stale pressed gesture");
                RaiseLeftButton(pill, UIElement.PreviewMouseLeftButtonUpEvent);
                Require(controller.State.DeepCapsuleQueueStartTopMargins[sourceKey] == 73 &&
                        controller.State.DeepCapsuleQueueStartTopMargins[primaryKey] == 137 &&
                        controller.State.CapsuleCollapseAllActiveQueues.Count == 0 &&
                        Field(controller, "_masterCapsuleQueueTransfer") == null &&
                        controller.State.Papers.Take(2).All(p => p.CapsuleMonitorDeviceName == SourceMonitor),
                    "stale free master gesture modified an unrelated anchor, membership or collapse state");
            }
        }
        finally
        {
            cacheField.SetValue(null, disconnected);
            controller.State.UseCapsuleFreeQueues = priorFreeMode;
            foreach (var row in priorPapers)
            {
                row.Paper.CapsuleMonitorDeviceName = row.Monitor;
                row.Paper.CapsuleSide = row.Side;
                row.Paper.CapsuleQueueId = row.Id;
            }
            controller.State.DeepCapsuleQueueStartTopMargins = priorMargins;
            controller.State.CapsuleCollapseAllActiveQueues = priorCollapse;
            controller.ArrangeDeepCapsules(animate: false);
        }
    }

    private static (FrameworkElement Pill, object Session, DeviceScreenPoint Start) PressMaster(
        MasterCapsuleWindow master)
    {
        var pill = (FrameworkElement)Field(master, "_pill")!;
        // CaptureMouse synchronizes the physical mouse and can emit a move immediately. That
        // unrelated sample has no real pressed button in this fixture; supply our move below.
        void IgnoreCaptureMove(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent == Mouse.PreviewMouseMoveEvent ||
                e.StagingItem.Input.RoutedEvent == Mouse.MouseMoveEvent) e.Cancel();
        }
        InputManager.Current.PreProcessInput += IgnoreCaptureMove;
        try { RaiseLeftButton(pill, UIElement.PreviewMouseLeftButtonDownEvent); }
        finally { InputManager.Current.PreProcessInput -= IgnoreCaptureMove; }
        var session = Field(master, "_dragSession");
        Require(pill.IsMouseCaptured && session != null, "real master MouseDown did not capture a session");
        return (pill, session!, (DeviceScreenPoint)Value(session!, "StartScreenPosition"));
    }

    private static void RaiseLeftButton(FrameworkElement pill, RoutedEvent routedEvent) =>
        pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = routedEvent });

    private static void RequireTransferReleased(AppController controller,
        EdgeCapsuleDragWindowOptions options, EdgeCapsuleDragWindow expectedHost)
    {
        Require(Field(controller, "_masterCapsuleQueueTransfer") == null, "master transfer gate was stranded");
        var lease = EdgeCapsuleDragWindow.Rent(options);
        try { Require(ReferenceEquals(lease, expectedHost), "master transfer stranded its pooled HWND lease"); }
        finally { lease.ReturnToPool(); }
        Require(controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
                controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right), out var next),
            "cancelled master gesture blocked the next Begin");
        controller.CancelMasterCapsuleQueueTransfer(next);
    }

    private static IDictionary Masters(AppController controller) => (IDictionary)Field(controller, "_masterCapsules")!;

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, Private)!.GetValue(instance);

    private static object Value(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
