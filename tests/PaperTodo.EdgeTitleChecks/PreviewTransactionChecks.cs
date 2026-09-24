using System.Diagnostics;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void QueuedPreviewTransactions()
    {
        ProxyInputReadiness();
        SharedFrameRenderingLiveness();
        var dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        {
            var monitor = new MonitorGeometry("transaction-test",
                new DeviceScreenRect(0, 0, 2560, 1440), scale, scale);
            var presenters = new[] { new EdgeCapsulePresenter(), new EdgeCapsulePresenter() };
            var callbacks = new Func<EdgeCapsuleDirty, EdgeCapsuleDirty>[2];
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                var presenter = presenters[i];
                Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(index, 0, 2),
                    EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach test presenter");
                callbacks[i] = dirty => presenter.Reconcile(dirty,
                    () => new EdgeCapsuleLayoutSnapshot(monitor, edge,
                        40 + index * 44 + presenter.Placement.TopOffsetDip, 0,
                        100, 28, 40, 300, 360, false, 1, null, 328, 360),
                    () => null, frame => frame, _ => true);
                presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
                presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
                    dispatcher, callbacks[i]);
            }
            try
            {
                foreach (var (open, sampleEndMs) in new[]
                    { (true, 200), (false, 200), (true, 40), (false, 200) })
                {
                    var before = presenters.Select(p => p.AppliedPresentation).ToArray();
                    // The real regression: input was queued at Send before the controller queued
                    // its commit. That input must not apply the newly changed Preview model early.
                    presenters[0].InvalidateBeforeNextRender(EdgeCapsuleDirty.Pointer |
                        EdgeCapsuleDirty.Measure, dispatcher, callbacks[0]);
                    using var ownerDeferral = presenters[0].DeferReconcileToVisualTransaction();
                    using var peerDeferral = presenters[1].DeferReconcileToVisualTransaction();
                    presenters[0].Dispatch(EdgeCapsuleIntent.PreviewChanged(open));
                    presenters[1].Dispatch(EdgeCapsuleIntent.QueuePlacementChanged(
                        new(1, 0, 2, open ? 320 : 0)));
                    presenters[0].InvalidateBeforeNextRender(EdgeCapsuleDirty.Pointer,
                        dispatcher, callbacks[0]);
                    DrainTransactionChecksDispatcher();
                    for (var i = 0; i < 2; i++)
                        Check(presenters[i].AppliedPresentation == before[i],
                            "Pending queue model must not escape through an earlier input callback");

                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < 2; i++)
                    {
                        presenters[i].BeginNativeBatchApply();
                        presenters[i].RequestPresentation(EdgeCapsuleMotion.Animate(
                            EdgeCapsuleTransitionReason.Preview, 200), rebaseActiveTransition: true);
                        presenters[i].Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
                            dispatcher, callbacks[i], start);
                        Check(presenters[i].NativeBatchApplyStatus == EdgeCapsuleNativeBatchApplyStatus.Ready,
                            "Coordinated Flush can consume deferred work");
                        presenters[i].CompleteNativeBatchApplySuccess();
                        Check(presenters[i].AppliedPresentation.Bounds == before[i].Bounds,
                            "Both members begin at their existing geometry");
                    }
                    // Desktop publication may take a frame while the UI thread cannot render.
                    // Its elapsed time must not consume either member's animation duration.
                    start += Stopwatch.Frequency * 15 / 1000;
                    for (var i = 0; i < presenters.Length; i++)
                    {
                        var presenter = presenters[i];
                        presenter.RebaseActiveTransitionStart(start);
                        presenter.Flush(EdgeCapsuleDirty.Frame, dispatcher, callbacks[i], start);
                        Check(presenter.AppliedPresentation.Bounds == before[i].Bounds,
                            "Publication time must not consume either animation's first frame");
                    }
                    ownerDeferral.Dispose();
                    peerDeferral.Dispose();
                    for (var ms = 0; ms <= sampleEndMs; ms += 5)
                    {
                        var now = start + Stopwatch.Frequency * ms / 1000;
                        for (var i = 0; i < 2; i++)
                            presenters[i].Flush(EdgeCapsuleDirty.Frame, dispatcher, callbacks[i], now);
                        Check(presenters[1].AppliedPresentation.Bounds.Top >=
                            presenters[0].AppliedPresentation.Bounds.Bottom,
                            $"Preview and follower must not overlap: open={open}, ms={ms}, dpi={scale}");
                    }
                }

                var version = presenters[0].AppliedPresentationVersion;
                using var first = presenters[0].DeferReconcileToVisualTransaction();
                using var successor = presenters[0].DeferReconcileToVisualTransaction();
                presenters[0].Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
                presenters[0].Invalidate(EdgeCapsuleDirty.Measure, dispatcher, callbacks[0]);
                first.Dispose();
                DrainTransactionChecksDispatcher();
                Check(presenters[0].AppliedPresentationVersion == version,
                    "Releasing an earlier transaction cannot release a staged successor");
                successor.Dispose();
                DrainTransactionChecksDispatcher();
                Check(presenters[0].AppliedPresentationVersion > version,
                    "Cancellation must resume dirty work instead of freezing the presenter");
            }
            finally
            {
                foreach (var presenter in presenters)
                {
                    presenter.CancelTransition();
                    presenter.ClearDeferredWork();
                }
            }
        }
    }

    private static void DrainTransactionChecksDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
