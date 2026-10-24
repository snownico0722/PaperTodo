using System.Diagnostics;
using System.Reflection;
using PaperTodo;

/// <summary>
/// P1：上一条胶囊仍占用共享拖拽租约时，第二次拖动越过阈值后松手不得退化成点击。
///
/// 覆盖：等待期间 reducer 保持 PendingClick、750ms 兜底时间戳不被后续移动重置、
/// 松手返回 true 且纸片保持折叠、租约释放后仍能正常起拖。
/// </summary>
internal static class EdgeCapsulePendingDragChecks
{
    private const BindingFlags Private =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        await Until(
            () => windows.Values.All(window => window.IsDeepCapsuleSlotVisible),
            "pending-drag fixture slots did not become visible");

        var paper = controller.State.Papers.Single(candidate => candidate.Id == "fixture-0");
        var window = windows[paper.Id];
        Require(
            WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var geometry),
            "pending-drag fixture monitor geometry was unavailable");

        var start = new DeviceScreenPoint(
            geometry.WorkArea.Left + 40,
            geometry.WorkArea.Top + 40);
        var moved = new DeviceScreenPoint(start.X, start.Y + 200);

        var shape = new EdgeCapsuleFloatingShape(
            true,
            EdgeCapsuleSurfaceKind.FloatingFree,
            200,
            46,
            46,
            12,
            true);
        var options = (EdgeCapsuleDragWindowOptions)Program.Call(
            window,
            "CreateDeepCapsuleFloatingDragHostOptions",
            shape)!;

        // 占住共享租约，模拟上一条手势仍在 docking handoff。
        var lease = EdgeCapsuleDragWindow.Rent(options);
        try
        {
            Program.Invoke(window, "OnEdgeCapsulePointerPressed", start);
            Require(
                Gesture(window) == EdgeCapsuleGestureState.PendingClick,
                "press did not create a pending click");

            Require(
                (bool)Program.Call(window, "OnEdgeCapsulePointerMoved", moved, true)!,
                "a threshold move must be consumed while waiting for the lease");
            Require(
                Gesture(window) == EdgeCapsuleGestureState.PendingClick,
                "waiting for the lease must keep the reducer in PendingClick");
            Require(
                (bool)Program.Part(window, "HasDeepCapsulePendingReorderDrag"),
                "crossing the threshold during a lease wait was not recorded");
            Require(
                (bool)Field(window, "_deepCapsulePointerClickRevoked")!,
                "crossing the threshold did not revoke the click");

            var pendingStart = (DeviceScreenPoint)Field(
                window,
                "_deepCapsulePendingReorderDragStart")!;
            Require(
                pendingStart == moved,
                "the lease wait recorded the wrong drag start");
            var timestamp = (long)Field(
                window,
                "_deepCapsulePendingReorderDragRequestTimestamp")!;

            await Task.Delay(50);
            var movedAgain = new DeviceScreenPoint(moved.X, moved.Y + 8);
            Require(
                (bool)Program.Call(window, "OnEdgeCapsulePointerMoved", movedAgain, true)!,
                "a later move must stay consumed while waiting for the lease");
            Require(
                (DeviceScreenPoint)Field(
                    window,
                    "_deepCapsulePendingReorderDragStart")! == pendingStart,
                "a later move rewrote the pending drag start");
            Require(
                (long)Field(
                    window,
                    "_deepCapsulePendingReorderDragRequestTimestamp")! == timestamp,
                "the 750 ms fallback clock was reset by a later move");

            Require(
                (bool)Program.Call(window, "OnEdgeCapsulePointerReleased", movedAgain)!,
                "a revoked click release must return true so the host releases capture");
            Require(
                Gesture(window) == EdgeCapsuleGestureState.Idle,
                "release did not finish the revoked gesture");
            Require(
                Field(window, "_deepCapsulePendingReorderDragStart") == null,
                "release left the pending retry behind");
            Require(
                !(bool)Field(window, "_deepCapsulePointerClickRevoked")!,
                "release left the click-revoked flag behind");
            Require(
                paper.IsCollapsed,
                "a revoked drag must not expand the paper");
            Require(
                !window.HasExpandedPaperSurface,
                "a revoked drag activated the paper surface");
        }
        finally
        {
            lease.ReturnToPool();
        }

        // 租约释放后，同一入口必须仍能正常起拖。
        var retryStart = new DeviceScreenPoint(start.X + 1, start.Y + 1);
        var retryMoved = new DeviceScreenPoint(retryStart.X, retryStart.Y + 200);
        Program.Invoke(window, "OnEdgeCapsulePointerPressed", retryStart);
        Require(
            Gesture(window) == EdgeCapsuleGestureState.PendingClick,
            "the retry press did not create a pending click");
        Require(
            (bool)Program.Call(window, "OnEdgeCapsulePointerMoved", retryMoved, true)!,
            "the retry move was not consumed");
        Require(
            Gesture(window) is EdgeCapsuleGestureState.DockedReordering
                or EdgeCapsuleGestureState.FloatingTransfer,
            "a released lease did not allow the drag to start");
        Require(
            Field(window, "_deepCapsulePendingReorderDragStart") == null,
            "a successful drag start left the pending retry behind");

        Program.Invoke(window, "CancelDeepCapsuleReorderDrag", true);
        Require(
            Gesture(window) == EdgeCapsuleGestureState.Idle,
            "cancel did not finish the retry drag");
        controller.ArrangeDeepCapsules(animate: false);
        await Until(
            () => windows.Values.All(candidate => candidate.IsDeepCapsuleSlotVisible),
            "slots did not return after the retry drag was cancelled");
    }

    private static EdgeCapsuleGestureState Gesture(PaperWindow window)
    {
        var presenter = Program.Part(window, "_edgeCapsule");
        var modelProperty = presenter.GetType().GetProperty(
            "Model",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var model = (EdgeCapsuleModel)modelProperty.GetValue(presenter)!;
        return model.State.Gesture;
    }



    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target);


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
