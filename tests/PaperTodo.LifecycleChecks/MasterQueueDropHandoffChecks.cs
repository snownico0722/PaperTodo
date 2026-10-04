using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

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
        var retired = false;

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
            if (host.IsVisible && TargetReady()) renderedWithCover = true;
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
            Require(controller.CommitMasterCapsuleQueueTransfer(snapshot, drop),
                "collapsed source transfer did not commit");
            Require(retired && withdrawn && targetReadyAtWithdrawal,
                "floating authority was withdrawn before the target master became opaque and laid out");
            Require(renderedWithCover,
                "target master did not cross a WPF render turn while the floating cover was retained");
            Require(controller.State.CapsuleCollapseAllActiveQueues.TryGetValue(targetKey, out var collapsed) && collapsed &&
                    controller.State.Papers.All(paper => paper.CapsuleSide == DeepCapsuleSides.Left),
                "drop handoff changed the committed queue membership or collapse state");
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
        withdrawn = targetReadyAtWithdrawal = renderedWithCover = false;
        try
        {
            host.ShowWithEntrance(drop, false, 1, 0);
            survivingSource.Hide();
            survivingSource.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            host.IsVisibleChanged += OnHostVisibilityChanged;
            CompositionTarget.Rendering += OnRendering;
            Require(controller.CommitMasterCapsuleQueueTransfer(sameQueue, drop) &&
                    ReferenceEquals(survivingSource, masters[targetKey]) && TargetReady() &&
                    host.IsVisible && renderedWithCover,
                "same-queue commit did not publish the surviving master under the floating cover");
            typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!
                .Invoke(survivingSource, null);
            typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(survivingSource, null);
            host.ReturnToPool();
            Require(withdrawn && targetReadyAtWithdrawal,
                "same-queue floating lease was withdrawn before the surviving master was ready");
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            host.IsVisibleChanged -= OnHostVisibilityChanged;
            typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!
                .Invoke(survivingSource, null);
            typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!.SetValue(survivingSource, null);
            host.ReturnToPool();
        }
        var nextLease = EdgeCapsuleDragWindow.Rent(options);
        try { Require(ReferenceEquals(host, nextLease), "completed drop stranded the shared floating HWND lease"); }
        finally { nextLease.ReturnToPool(); }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
