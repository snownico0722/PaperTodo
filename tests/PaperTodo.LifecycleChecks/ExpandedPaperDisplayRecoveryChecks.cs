using System.Reflection;
using System.Windows.Threading;
using PaperTodo;

internal static class ExpandedPaperDisplayRecoveryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Prepare(AppState state)
    {
        state.EnableAnimations = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        state.ShowDeepCapsuleWhileExpanded = true;
        state.CollapseExpandedDeepCapsuleOnClick = true;
    }

    internal static async Task Run(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var window = windows["fixture-0"];
        var paper = controller.State.Papers.Single(p => p.Id == window.PaperId);
        var monitors = WindowWorkAreaHelper.ConnectedMonitorGeometries();
        Require(monitors.Count > 0 && window.IsDeepCapsuleSlotVisible,
            "edge fixture must have a connected monitor and a real capsule host");

        var sampleMonitor = monitors[0];
        var onScreenSample = new DeviceScreenPoint(
            sampleMonitor.WorkArea.Left + 1,
            sampleMonitor.WorkArea.Top + 1);
        var offScreenSample = new DeviceScreenPoint(
            monitors.Max(m => m.WorkArea.Right) + 1,
            sampleMonitor.WorkArea.Top + 1);
        Require(ExpandedPaperRecoveryPolicy.IsWithinConnectedWorkArea(onScreenSample, monitors),
            "a point inside a connected monitor work area was rejected");
        Require(!ExpandedPaperRecoveryPolicy.IsWithinConnectedWorkArea(offScreenSample, monitors),
            "an off-screen point was accepted as a visible sampling location");

        window.ActivateFromEdgeShortcut();
        await Dispatcher.CurrentDispatcher.InvokeAsync(
            static () => { }, DispatcherPriority.Render);
        Require(paper.IsVisible && !paper.IsCollapsed && window.IsVisible &&
            window.IsDeepCapsuleSlotVisible,
            "expanded paper must keep an active edge capsule");

        Require(WindowNative.TryGetWindowDeviceBounds(window, out var original) &&
            ExpandedPaperRecoveryPolicy.HasReachablePixels(original, monitors),
            "paper must start on a reachable monitor");

        var rightEdge = monitors.Max(m => m.WorkArea.Right);
        var offscreen = new DeviceScreenRect(
            rightEdge + 2048, original.Top,
            rightEdge + 2048 + original.Width, original.Top + original.Height);
        void ParkOffscreen()
        {
            Require(WindowNative.TrySetWindowDeviceBounds(window, offscreen),
                "could not simulate an HWND displaced by a monitor disconnect");
            Require(WindowNative.TryGetWindowDeviceBounds(window, out var actual) &&
                !ExpandedPaperRecoveryPolicy.HasReachablePixels(actual, monitors),
                "off-screen displacement did not take effect");
        }

        // Active capsule, expanded logical paper, but the real paper HWND is inaccessible.
        ParkOffscreen();
        window.ActivateFromEdgeShortcut();
        var hasRetrievedBounds = WindowNative.TryGetWindowDeviceBounds(window, out var retrieved);
        var clickRecovered = paper.IsVisible && !paper.IsCollapsed &&
            window.IsDeepCapsuleSlotVisible && hasRetrievedBounds &&
            ExpandedPaperRecoveryPolicy.HasReachablePixels(retrieved, monitors);
        if (!clickRecovered)
        {
            var directRecovery = window.RecoverInaccessibleExpandedSurface(monitors);
            var hasDirectBounds = WindowNative.TryGetWindowDeviceBounds(window, out var afterDirect);
            var presenter = (EdgeCapsulePresenter)typeof(PaperWindow)
                .GetField("_edgeCapsule", Private)!.GetValue(window)!;
            Console.WriteLine($"EDGE_RESCUE_DIAGNOSTIC clickRect={retrieved} " +
                $"clickBounds={hasRetrievedBounds} wpfVisible={window.IsVisible} " +
                $"collapsed={paper.IsCollapsed} paperVisible={paper.IsVisible} " +
                $"edgeHostVisible={window.IsDeepCapsuleSlotVisible} " +
                $"windowState={window.WindowState} slot={presenter.State.Slot} " +
                $"wpfOpacity={window.Opacity:F3} directRecovery={directRecovery} " +
                $"directBounds={hasDirectBounds} directRect={afterDirect}");
        }
        Require(clickRecovered,
            "clicking an active capsule failed to retrieve its off-screen expanded HWND");

        // Simulate display topology settling without requiring a real DP hot-unplug in CI.
        ParkOffscreen();
        typeof(AppController).GetMethod("RefreshAfterDisplayMetricsChanged", Private)!
            .Invoke(controller, null);
        Require(WindowNative.TryGetWindowDeviceBounds(window, out var restored) &&
            ExpandedPaperRecoveryPolicy.HasReachablePixels(restored, monitors) &&
            paper.IsVisible && !paper.IsCollapsed,
            "display-change reconciliation left the expanded paper inaccessible");

        // Deliberate partly off-screen placement must never be recentered. Keep 64 DIP
        // reachable and convert that overlap through the destination monitor's DPI.
        var monitor = monitors[0];
        var area = monitor.WorkArea;
        var overlapWidth = (int)Math.Ceiling(64 * monitor.DpiScaleX);
        var topInset = (int)Math.Ceiling(64 * monitor.DpiScaleY);
        var partial = new DeviceScreenRect(
            area.Right - overlapWidth, area.Top + topInset,
            area.Right - overlapWidth + restored.Width, area.Top + topInset + restored.Height);
        Require(WindowNative.TrySetWindowDeviceBounds(window, partial),
            "could not stage partial visibility");
        Require(ExpandedPaperRecoveryPolicy.HasReachablePixels(partial, monitors),
            "the partial-window threshold rejected usable placement");

        // Exercise the same 64-DIP overlap at 200% so the test does not only pass on
        // the runner's current DPI configuration.
        var highDpiMonitor = monitor with { DpiScaleX = 2, DpiScaleY = 2 };
        var highDpiOverlap = (int)Math.Ceiling(64 * highDpiMonitor.DpiScaleX);
        var highDpiInset = (int)Math.Ceiling(64 * highDpiMonitor.DpiScaleY);
        var highDpiPartial = new DeviceScreenRect(
            area.Right - highDpiOverlap, area.Top + highDpiInset,
            area.Right - highDpiOverlap + restored.Width,
            area.Top + highDpiInset + restored.Height);
        Require(ExpandedPaperRecoveryPolicy.HasReachablePixels(
                highDpiPartial, new[] { highDpiMonitor }),
            "the partial-window threshold rejected a 64-DIP overlap at 200% DPI");

        typeof(AppController).GetMethod("RefreshAfterDisplayMetricsChanged", Private)!
            .Invoke(controller, null);
        Require(WindowNative.TryGetWindowDeviceBounds(window, out var unchanged) &&
            unchanged == partial,
            "display-change reconciliation unexpectedly moved a partially visible paper");

        Console.WriteLine("PASS expanded edge: explicit rescue, display reconnect, partial placement");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
