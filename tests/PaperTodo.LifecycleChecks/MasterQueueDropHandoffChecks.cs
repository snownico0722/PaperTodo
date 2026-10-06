using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class MasterQueueDropHandoffChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(AppController controller)
    {
        var masters = (IDictionary)typeof(AppController).GetField("_masterCapsules", Private)!
            .GetValue(controller)!;
        var sourceKey = "|" + DeepCapsuleSides.Right;
        var targetKey = "|" + DeepCapsuleSides.Left;
        var source = (MasterCapsuleWindow)masters[sourceKey]!;
        Require(!masters.Contains(targetKey) && controller.State.EnableAnimations &&
                controller.State.CapsuleCollapseAllActiveQueues[sourceKey],
            "drop handoff fixture needs a collapsed source and an empty animated target");
        Require(controller.TryCreateMasterQueueFloatingDragHostOptions(
                "", EdgeCapsuleEdge.Right, "▸", "5", out var options),
            "drop handoff fixture could not create floating options");
        Require(controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
                controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right), out var snapshot),
            "drop handoff fixture could not begin the source transfer");
        var host = EdgeCapsuleDragWindow.Rent(options);
        typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Private)!.SetValue(source, host);
        typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(source, snapshot);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "drop handoff fixture needs a real monitor");
        var drop = new DeviceScreenPoint(monitor.WorkArea.Left + 100, monitor.WorkArea.Top + 150);
        var withdrawn = false;
        var targetReadyAtWithdrawal = false;
        var renderedWithCover = false;
        var flewBeforeReveal = false;
        var retired = false;
        MasterCapsuleWindow? committedTarget = null;
        DeviceScreenRect floatingStartBounds = default;

        bool TargetReady()
        {
            var target = masters[targetKey] as MasterCapsuleWindow;
            return target is { IsVisible: true } && target.Opacity >= 0.999 &&
                target.IsMeasureValid && target.IsArrangeValid &&
                WindowNative.TryGetWindowDeviceBounds(target, out var bounds) &&
                !bounds.IsEmpty && Math.Abs(bounds.Left - monitor.WorkArea.Left) <= 1;
        }

        void OnHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (host.IsVisible) return;
            withdrawn = true;
            targetReadyAtWithdrawal = TargetReady();
        }

        void OnRendering(object? sender, EventArgs e)
        {
            if (!host.IsVisible) return;
            if (TargetReady()) renderedWithCover = true;
            if (!flewBeforeReveal &&
                committedTarget is { IsVisible: false } &&
                !floatingStartBounds.IsEmpty &&
                WindowNative.TryGetWindowDeviceBounds(host, out var current) &&
                (Math.Abs(current.Left - floatingStartBounds.Left) > 2 ||
                 Math.Abs(current.Top - floatingStartBounds.Top) > 2))
            {
                flewBeforeReveal = true;
            }
        }

        void OnSourceClosed(object? sender, EventArgs e) => retired = true;

        try
        {
            host.ShowWithEntrance(drop, false, 1, 0);
            source.Hide();
            source.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            host.IsVisibleChanged += OnHostVisibilityChanged;
            source.Closed += OnSourceClosed;
            CompositionTarget.Rendering += OnRendering;
            Require(WindowNative.TryGetWindowDeviceBounds(host, out floatingStartBounds),
                "drop handoff could not sample the floating start bounds");
            Require(controller.CommitMasterCapsuleQueueTransfer(
                    snapshot, drop, out committedTarget) &&
                    committedTarget != null,
                "collapsed source transfer did not commit with a handoff target");
            var target = committedTarget!;
            Require(retired && host.IsVisible && !withdrawn &&
                    !target.IsVisible,
                "committed target became visible before the floating return flight");
            Require(target.TryGetQueueTransferDockingTarget(
                    out var dockingTargetBounds, out var dockingTargetEdge) &&
                    dockingTargetEdge == EdgeCapsuleEdge.Left,
                "committed target did not expose a valid ordinary-docking anchor");
            var wallChrome = (int)Math.Round(
                EdgeCapsuleLayout.WindowChromeMargin * monitor.DpiScaleX,
                MidpointRounding.AwayFromZero);
            Require(Math.Abs(dockingTargetBounds.Left -
                        (monitor.WorkArea.Left - wallChrome)) <= 1,
                "master return target did not reuse the ordinary wall-side chrome geometry");
            Require(controller.State.CapsuleCollapseAllActiveQueues.TryGetValue(targetKey, out var collapsed) && collapsed &&
                    controller.State.Papers.All(paper => paper.CapsuleSide == DeepCapsuleSides.Left),
                "drop handoff changed the committed queue membership or collapse state");

            var released = false;
            BeginFloatingHandoff(source, host, target, () =>
            {
                typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!.Invoke(source, null);
                host.ReturnToPool();
                released = true;
            });
            Require(EdgeCapsuleDragWindow.HasActiveLease,
                "master return flight released the shared drag-host lease before completion");
            Require(!controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Left,
                    controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Left),
                    out _),
                "a second master transfer started while the shared drag host was still returning");
            PumpUntil(() => released, "cross-queue master return flight");
            Require(!EdgeCapsuleDragWindow.HasActiveLease,
                "master return flight left the shared drag-host lease active");
            Require(flewBeforeReveal,
                "master floating host did not move toward the docked target before reveal");
            Require(renderedWithCover && withdrawn && targetReadyAtWithdrawal,
                "floating authority was withdrawn before the target master became opaque and laid out");
            Require(TargetReady() && !host.IsVisible && !masters.Contains(sourceKey),
                "drop handoff did not leave one live target master with the source retired");
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            source.Closed -= OnSourceClosed;
            host.IsVisibleChanged -= OnHostVisibilityChanged;
            typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!.Invoke(source, null);
            typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(source, null);
            host.ReturnToPool();
        }

        // A same-queue drop keeps the source master rather than retiring it. The controller
        // must publish that still-hidden master too, before the caller returns the floating lease.
        var survivingSource = (MasterCapsuleWindow)masters[targetKey]!;
        Require(controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left,
                controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Left), out var sameQueue),
            "same-queue handoff did not begin");
        host = EdgeCapsuleDragWindow.Rent(options);
        typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Private)!.SetValue(survivingSource, host);
        typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(survivingSource, sameQueue);
        withdrawn = targetReadyAtWithdrawal = renderedWithCover = flewBeforeReveal = false;
        committedTarget = null;
        var compactBeforeFlight = controller.State.CompactMasterCapsule;
        var membershipBeforeFlight = controller.State.Papers.Select(paper =>
            (paper.Id, paper.CapsuleSide, paper.CapsuleMonitorDeviceName,
                paper.IsVisible, paper.IsCollapsed)).ToArray();
        DispatcherOperation? changeCompact = null;
        var changedDuringFlight = false;
        var sampledReveal = false;
        var alignedThroughoutReveal = true;
        var alignedAtRelease = false;

        string? HandoffPhase()
        {
            var animation = typeof(EdgeCapsuleDragWindow)
                .GetField("_dockingHandoffAnimation", Private)!.GetValue(host);
            return animation?.GetType().GetProperty("Phase")!.GetValue(animation)?.ToString();
        }

        bool CoverMatchesCurrentTarget()
        {
            if (masters[targetKey] is not MasterCapsuleWindow current ||
                !current.TryGetQueueTransferDockingTarget(out var anchor, out var edge) ||
                !WindowNative.TryGetWindowDeviceBounds(host, out var actualBounds)) return false;
            var expected = EdgeCapsuleGeometry.FloatingHandoffGeometry(
                actualBounds, anchor, edge, options.Shape.WindowWidthDip,
                options.Shape.WindowHeightDip, monitor.DpiScaleX, monitor.DpiScaleY);
            var currentWidth = (double)typeof(EdgeCapsuleDragWindow)
                .GetField("_currentSurfaceWidthDip", Private)!.GetValue(host)!;
            var surface = (FrameworkElement)typeof(EdgeCapsuleDragWindow)
                .GetField("_surface", Private)!.GetValue(host)!;
            return expected.IsUsable &&
                EdgeCapsuleGeometry.DeviceBoundsMatch(actualBounds, expected.HostTargetBounds, tolerance: 2) &&
                Math.Abs(currentWidth - expected.SurfaceTargetWidthDip) < 0.01 &&
                Math.Abs(surface.ActualWidth - expected.SurfaceTargetWidthDip) * monitor.DpiScaleX <= 1;
        }

        void OnCompactRendering(object? sender, EventArgs e)
        {
            if (!host.IsVisible || HandoffPhase() != "Reveal") return;
            sampledReveal = true;
            alignedThroughoutReveal &= CoverMatchesCurrentTarget();
        }

        try
        {
            host.ShowWithEntrance(drop, false, 1, 0);
            survivingSource.Hide();
            survivingSource.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            host.IsVisibleChanged += OnHostVisibilityChanged;
            CompositionTarget.Rendering += OnRendering;
            CompositionTarget.Rendering += OnCompactRendering;
            Require(controller.CommitMasterCapsuleQueueTransfer(
                    sameQueue, drop, out committedTarget) &&
                    ReferenceEquals(survivingSource, committedTarget) &&
                    ReferenceEquals(survivingSource, masters[targetKey]) &&
                    !survivingSource.IsVisible && host.IsVisible,
                "same-queue commit did not retain the hidden master for the return flight");
            Require(committedTarget!.TryGetQueueTransferDockingTarget(out var oldAnchor, out _),
                "same-queue compact handoff did not expose its initial anchor");
            var released = false;
            BeginFloatingHandoff(survivingSource, host, committedTarget!, () =>
            {
                // Sample before pooling resets the floating surface. The docked target alone
                // can be correct even when an obsolete-width cover fades above it.
                alignedAtRelease = CoverMatchesCurrentTarget();
                typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!
                    .Invoke(survivingSource, null);
                host.ReturnToPool();
                released = true;
            });
            changeCompact = survivingSource.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                changedDuringFlight = host.IsVisible && !survivingSource.IsVisible && HandoffPhase() == "Flight";
                controller.PublicSettings.Set("capsule.master_compact",
                    JsonSerializer.SerializeToElement(!compactBeforeFlight));
                Require(masters[targetKey] is MasterCapsuleWindow current &&
                        current.TryGetQueueTransferDockingTarget(out var newAnchor, out _) &&
                        !EdgeCapsuleGeometry.DeviceBoundsMatch(oldAnchor, newAnchor, tolerance: 1),
                    "compact setting did not change the in-flight docking anchor");
            }));
            PumpUntil(() => released, "same-queue master return flight");
            Require(changeCompact.Status == DispatcherOperationStatus.Completed && changedDuringFlight,
                "compact setting did not change through the public API during the master return flight");
            Require(sampledReveal && alignedThroughoutReveal && alignedAtRelease,
                "master floating cover used stale geometry after compact mode changed during flight");
            Require(renderedWithCover && withdrawn && targetReadyAtWithdrawal,
                "same-queue floating lease was withdrawn before the surviving master was ready");
            Require(!EdgeCapsuleDragWindow.HasActiveLease &&
                    controller.State.CapsuleCollapseAllActiveQueues[targetKey] &&
                    controller.State.Papers.Select(paper =>
                        (paper.Id, paper.CapsuleSide, paper.CapsuleMonitorDeviceName,
                            paper.IsVisible, paper.IsCollapsed)).SequenceEqual(membershipBeforeFlight),
                "compact handoff changed queue membership or collapse state, or retained the floating lease");
        }
        finally
        {
            changeCompact?.Abort();
            CompositionTarget.Rendering -= OnCompactRendering;
            CompositionTarget.Rendering -= OnRendering;
            host.IsVisibleChanged -= OnHostVisibilityChanged;
            typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!
                .Invoke(survivingSource, null);
            typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(survivingSource, null);
            host.ReturnToPool();
            controller.PublicSettings.Set("capsule.master_compact",
                JsonSerializer.SerializeToElement(compactBeforeFlight));
        }
        var nextLease = EdgeCapsuleDragWindow.Rent(options);
        try { Require(ReferenceEquals(host, nextLease), "completed drop stranded the shared floating HWND lease"); }
        finally { nextLease.ReturnToPool(); }
        RunReentrantShow(controller, masters);
    }

    private static void RunReentrantShow(AppController controller, IDictionary masters)
    {
        // The previous cases left a collapsed left queue. Keep one hidden member behind,
        // then show it through the real MCP presentation path during the destination render turn.
        var sourceKey = "|" + DeepCapsuleSides.Left;
        var targetKey = "|" + DeepCapsuleSides.Right;
        var hidden = controller.State.Papers[^1];
        controller.HidePaper(hidden);
        var source = (MasterCapsuleWindow)masters[sourceKey]!;
        Require(hidden.IsCollapsed && !hidden.IsVisible && !masters.Contains(targetKey),
            "reentrant handoff fixture needs a hidden collapsed source member and empty target");
        Require(controller.TryCreateMasterQueueFloatingDragHostOptions(
                "", EdgeCapsuleEdge.Left, "▸", "4", out var options),
            "reentrant handoff could not create floating options");
        Require(controller.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left,
                controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Left), out var snapshot),
            "reentrant handoff could not begin");
        var host = EdgeCapsuleDragWindow.Rent(options);
        typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Private)!.SetValue(source, host);
        typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(source, snapshot);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "reentrant handoff needs a monitor");
        var drop = new DeviceScreenPoint(monitor.WorkArea.Right - 100, monitor.WorkArea.Top + 150);
        DispatcherOperation? show = null;
        var showedUnderCover = false;
        try
        {
            host.ShowWithEntrance(drop, false, 1, 0);
            source.Hide();
            source.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            // McpApiHost dispatches at Normal, ahead of the shared Render boundary.
            show = source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                showedUnderCover = host.IsVisible;
                controller.PresentWorkspacePaper(hidden.Id, PaperPresentationAction.Show,
                    activate: false, PaperOperationContext.Mcp());
            }));
            Require(controller.CommitMasterCapsuleQueueTransfer(
                    snapshot, drop, out var handoffTarget) &&
                    handoffTarget != null && host.IsVisible,
                "reentrant handoff did not commit with a retained floating cover");
            var released = false;
            BeginFloatingHandoff(source, host, handoffTarget!, () =>
            {
                typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!.Invoke(source, null);
                host.ReturnToPool();
                released = true;
            });
            PumpUntil(() => released, "reentrant master return flight");
            Require(show.Status == DispatcherOperationStatus.Completed && showedUnderCover,
                "MCP show did not reenter while the floating cover was retained");
            Require(masters[sourceKey] is MasterCapsuleWindow { IsVisible: true } restored &&
                    !ReferenceEquals(restored, source),
                "an old queue plan removed or hid the source master restored during handoff");
            Require(hidden.IsVisible && hidden.CapsuleSide == DeepCapsuleSides.Left &&
                    snapshot.PaperIds.All(id => controller.State.Papers.Single(p => p.Id == id)
                        .CapsuleSide == DeepCapsuleSides.Right) &&
                    controller.State.CapsuleCollapseAllActiveQueues[sourceKey] &&
                    controller.State.CapsuleCollapseAllActiveQueues[targetKey],
                "handoff reentry changed membership or retained collapse state");
            Require(masters[targetKey] is MasterCapsuleWindow { IsVisible: true } && !host.IsVisible,
                "reentrant handoff lost the committed target or failed to release its cover");
        }
        finally
        {
            show?.Abort();
            typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!.Invoke(source, null);
            typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(source, null);
            host.ReturnToPool();
        }
    }

    private static void BeginFloatingHandoff(
        MasterCapsuleWindow source,
        EdgeCapsuleDragWindow host,
        MasterCapsuleWindow target,
        Action releaseCover) =>
        typeof(MasterCapsuleWindow)
            .GetMethod("BeginQueueTransferFloatingHandoff", Private)!
            .Invoke(source, new object[] { host, target, releaseCover });

    private static void PumpUntil(Func<bool> predicate, string context)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.ElapsedMilliseconds < 5000)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Require(predicate(), context + " timed out");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
