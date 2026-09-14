using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void DetachedPointerAdmissionChecks()
    {
        // Keep this direct reducer assertion first. Running this new test DLL against the old
        // product DLL must fail normally before the Presenter could invoke Debug.Fail/FailFast.
        var detachedSample = EdgeCapsuleReducer.Reduce(
            EdgeCapsuleModel.Initial, EdgeCapsuleIntent.PointerSampled(true));
        Check(detachedSample.Accepted && !detachedSample.Model.PointerOverSurface,
            "A detached model accepts a stale physical hit without restoring pointer ownership");
        Check(detachedSample.Model == EdgeCapsuleModel.Initial,
            "Sampling a stale hit does not manufacture placement, visual, preview or gesture state");
        Check(EdgeCapsuleReducer.Reduce(EdgeCapsuleModel.Initial,
                EdgeCapsuleIntent.PointerSampled(false)).Accepted,
            "Detached outside sampling remains accepted");

        var model = EdgeCapsuleModel.Initial;
        void Apply(EdgeCapsuleIntent intent)
        {
            var result = EdgeCapsuleReducer.Reduce(model, intent);
            Check(result.Accepted, "Pointer lifecycle intent accepted: " + intent.GetType().Name);
            model = result.Model;
        }
        var point = new DeviceScreenPoint(2526, 116);
        Apply(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false));
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.PointerOverSurface && model.State.Visual == EdgeCapsuleVisualState.Hovered,
            "A docked hit still owns hover");
        Apply(EdgeCapsuleIntent.PeerReorderStarted());
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(!model.PointerOverSurface, "Peer reorder keeps its existing pointer exclusion");
        Apply(EdgeCapsuleIntent.PeerReorderFinished());
        Apply(EdgeCapsuleIntent.PointerPressed(point));
        Apply(EdgeCapsuleIntent.DockedReorderStarted(point, "detached-pointer-check", 0, 97));
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.PointerOverSurface && model.State.Gesture == EdgeCapsuleGestureState.DockedReordering,
            "Docked reorder sampling remains attached");
        Apply(EdgeCapsuleIntent.FloatingTransferStarted(point));
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.PointerOverSurface && model.State.Slot != EdgeCapsuleSlotState.None &&
            model.State.Gesture == EdgeCapsuleGestureState.FloatingTransfer,
            "A floating transfer retains its queue slot and physical sampling");
        Apply(EdgeCapsuleIntent.FloatingReorderStarted());
        Apply(EdgeCapsuleIntent.PointerSampled(false));
        Check(!model.PointerOverSurface, "Floating reorder can sample outside");
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.PointerOverSurface, "Floating reorder can sample inside again");
        Apply(EdgeCapsuleIntent.Detached());
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.State.Slot == EdgeCapsuleSlotState.None && !model.Placement.IsPlaced &&
            model.DragSession == null && !model.PointerOverSurface,
            "Detaching a floating gesture discards subsequent stale surface hits");
        Apply(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Expanded, false));
        Apply(EdgeCapsuleIntent.PointerSampled(true));
        Check(model.PointerOverSurface && model.State.Visual == EdgeCapsuleVisualState.Active,
            "Reattachment immediately restores normal expanded-slot sampling");

        Check(!EdgeCapsuleReducer.Reduce(EdgeCapsuleModel.Initial,
                EdgeCapsuleIntent.PointerPressed(point)).Accepted,
            "A detached capsule still rejects beginning a pointer gesture");
        var invalidPlacement = EdgeCapsuleModel.Initial with { Placement = new(0, 0, 1) };
        Check(!EdgeCapsuleReducer.Reduce(invalidPlacement, EdgeCapsuleIntent.PointerSampled(true)).Accepted,
            "The strict structural invariant still rejects an unrelated detached/placed contradiction");

        DetachedPresenterStaleFrameCheck(resolveStaleCoverAfterHide: false);
        DetachedPresenterStaleFrameCheck(resolveStaleCoverAfterHide: true);
        Console.WriteLine("PASS detached-pointer-admission-and-stale-frame-flush");
    }

    private static void DetachedPresenterStaleFrameCheck(bool resolveStaleCoverAfterHide)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var presenter = new EdgeCapsulePresenter();
        var monitor = new MonitorGeometry("detached-pointer-check",
            new DeviceScreenRect(0, 0, 2560, 1440), 1, 1);
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Right,
            NormalTopDip: 97, MasterTopDip: 0, RestingWidthDip: 100,
            MaximumCloseWidthDip: 28, HeightDip: 40, PreviewWidthDip: 256,
            PreviewHeightDip: 168, CloseSegmentActsAsContent: false,
            RestingContentOpacity: 1, ForcedContentOpacity: null,
            HostCapacityWidthDip: 284, HostCapacityHeightDip: 360);
        DeviceScreenPoint? pointer = null;
        var staleFrame = EdgeCapsulePresentationFrame.Hidden;
        var resolveStale = false;
        var receivedDirty = EdgeCapsuleDirty.None;
        var appliedFrames = new List<EdgeCapsulePresentationFrame>();
        EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty)
        {
            receivedDirty = dirty;
            return presenter.Reconcile(dirty, () => layout, () => pointer,
                frame => resolveStale ? staleFrame : frame,
                frame => { appliedFrames.Add(frame); return true; });
        }

        try
        {
            Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach the actual regression Presenter");
            Check(presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true)).Accepted,
                "Open the regression preview through the real reducer");
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Preview));
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, Reconcile);
            staleFrame = presenter.AppliedPresentation;
            pointer = new DeviceScreenPoint(2526, 116);
            Check(staleFrame.Visible && staleFrame.IsHitTestVisible &&
                EdgeCapsuleGeometry.Contains(staleFrame.InteractiveBounds, pointer.Value),
                "The real Presenter has an applied visible/hittable frame under the captured failure pointer");

            Check(presenter.Dispatch(EdgeCapsuleIntent.Detached()).Accepted,
                "The lifecycle detaches the model before hiding its previously applied frame");
            Check(presenter.State.Slot == EdgeCapsuleSlotState.None && !presenter.PointerOverSurface &&
                presenter.AppliedPresentation == staleFrame,
                "Reproduction holds detached model plus unchanged old applied frame without reflection");
            resolveStale = resolveStaleCoverAfterHide;
            // Match the captured heap's dirty=5: pending Pointer survives a Presentation Flush.
            presenter.Invalidate(EdgeCapsuleDirty.Pointer, dispatcher, Reconcile);
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Retraction));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, Reconcile);
            Check(receivedDirty == (EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Pointer) &&
                (int)receivedDirty == 5, "Flush executes the captured Presentation|Pointer dirty combination");
            Check(presenter.State.Slot == EdgeCapsuleSlotState.None && !presenter.Placement.IsPlaced &&
                !presenter.PointerOverSurface && presenter.LastPointerSample == pointer,
                "Stale hits are physically sampled but cannot contaminate the detached model");
            Check(!presenter.AppliedPresentation.Visible && !presenter.AppliedPresentation.IsHitTestVisible &&
                !presenter.HasActiveTransition && appliedFrames.Any(frame => !frame.Visible),
                "The actual Presenter reaches its hidden endpoint without assertion or a replacement transition");

            // The same Presenter must admit the next genuine queue interaction; this is not a
            // permanent input mute or a property value fabricated by the fixture.
            resolveStale = false;
            Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Reattach the same Presenter");
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Placement));
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Pointer,
                dispatcher, Reconcile);
            Check(presenter.PointerOverSurface && presenter.State.Visual == EdgeCapsuleVisualState.Hovered,
                "The reattached Presenter processes ordinary inside-pointer hover again");
        }
        finally
        {
            presenter.CancelTransition();
            presenter.ClearDeferredWork();
        }
    }
}
