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
        Require(paper.IsVisible && !paper.IsCollapsed && window.IsDeepCapsuleSlotVisible &&
            WindowNative.TryGetWindowDeviceBounds(window, out var retrieved) &&
            ExpandedPaperRecoveryPolicy.HasReachablePixels(retrieved, monitors),
            "clicking an active capsule failed to retrieve its off-screen expanded HWND");

        // Simulate display topology settling without requiring a real DP hot-unplug in CI.
        ParkOffscreen();
        typeof(AppController).GetMethod("RefreshAfterDisplayMetricsChanged", Private)!
            .Invoke(controller, null);
        Require(WindowNative.TryGetWindowDeviceBounds(window, out var restored) &&
            ExpandedPaperRecoveryPolicy.HasReachablePixels(restored, monitors) &&
            paper.IsVisible && !paper.IsCollapsed,
            "display-change reconciliation left the expanded paper inaccessible");

        // Deliberate partly off-screen placement must never be recentered.
        var area = monitors[0].WorkArea;
        var partial = new DeviceScreenRect(
            area.Right - 80, area.Top + 80,
            area.Right - 80 + restored.Width, area.Top + 80 + restored.Height);
        Require(WindowNative.TrySetWindowDeviceBounds(window, partial),
            "could not stage partial visibility");
        Require(ExpandedPaperRecoveryPolicy.HasReachablePixels(partial, monitors),
            "the partial-window threshold rejected usable placement");
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
