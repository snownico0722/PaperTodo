using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Sole owner of desired model, target plan, transition, applied frame and deferred work. Both
/// deferred invalidation and synchronous Flush execute the same reconcile callback.
/// </summary>
internal sealed class EdgeCapsulePresenter
{
    private readonly record struct PresentationResult(
        bool Applied,
        bool NeedsNextFrame);

    private EdgeCapsuleDirty _dirty;
    private bool _reconcileScheduled;
    private int _reconcileGeneration;
    private EdgeCapsuleFrameScheduler? _frameScheduler;
    private bool _frameSchedulerActive;
    private Dispatcher? _dispatcher;
    private Func<EdgeCapsuleDirty, EdgeCapsuleDirty>? _reconcile;
    private EdgeCapsuleLayoutSnapshot? _layoutSnapshot;
    private bool _hasFramePointerOverride;
    private DeviceScreenPoint? _framePointerOverride;
    private EdgeCapsuleMotion _pendingMotion =
        EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);

    private EdgeCapsuleModel Model { get; set; } = EdgeCapsuleModel.Initial;
    public EdgeCapsuleState State => Model.State;
    public EdgeCapsuleDragSession? DragSession => Model.DragSession;
    public EdgeCapsulePlacement Placement => Model.Placement;
    public bool ContextMenuOpen => Model.ContextMenuOpen;
    private EdgeCapsulePresentationPlan TargetPlan { get; set; } =
        EdgeCapsulePresentationPlan.Hidden;
    private EdgeCapsuleTargetPresentation TargetPresentation => TargetPlan.Docked;
    public EdgeCapsuleFloatingShape FloatingShape => TargetPlan.Floating;
    public EdgeCapsulePresentationFrame AppliedPresentation { get; private set; } =
        EdgeCapsulePresentationFrame.Hidden;
    private EdgeCapsuleTransition? Transition { get; set; }

    public EdgeCapsuleDispatchResult Dispatch(
        EdgeCapsuleIntent intent,
        [CallerMemberName] string reason = "")
    {
        var result = EdgeCapsuleReducer.Reduce(Model, intent);
        if (!result.Accepted)
        {
            Debug.Fail($"{result.Error} ({reason})");
            return result;
        }
        Model = result.Model;
        return result;
    }

    public EdgeCapsuleCaptureAction HandleCaptureLost(bool leftButtonPressed)
    {
        switch (State.Gesture)
        {
            case EdgeCapsuleGestureState.FloatingTransfer:
                return EdgeCapsuleCaptureAction.IgnoreExpectedTransfer;
            case EdgeCapsuleGestureState.PendingClick:
                Dispatch(EdgeCapsuleIntent.PointerInteractionFinished());
                return EdgeCapsuleCaptureAction.None;
            case EdgeCapsuleGestureState.DockedReordering:
            case EdgeCapsuleGestureState.FloatingReordering:
                return leftButtonPressed
                    ? EdgeCapsuleCaptureAction.Recapture
                    : EdgeCapsuleCaptureAction.CancelDrag;
            default:
                return EdgeCapsuleCaptureAction.None;
        }
    }

    public void RequestPresentation(EdgeCapsuleMotion motion)
    {
        // An explicit Snap owns the batch. Otherwise Animate outranks passive Preserve; measure
        // and display refreshes cannot downgrade an interaction transition already requested.
        if (motion.Kind == EdgeCapsuleMotionKind.Snap ||
            _pendingMotion.Kind == EdgeCapsuleMotionKind.Preserve ||
            (motion.Kind == EdgeCapsuleMotionKind.Animate &&
                _pendingMotion.Kind != EdgeCapsuleMotionKind.Snap))
        {
            _pendingMotion = motion;
        }
    }

    public EdgeCapsuleDirty Reconcile(
        EdgeCapsuleDirty dirty,
        Func<EdgeCapsuleLayoutSnapshot> captureLayout,
        Func<DeviceScreenPoint?> capturePointer,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long? nowTimestamp = null)
    {
        var remaining = EdgeCapsuleDirty.None;
        var now = nowTimestamp ?? Stopwatch.GetTimestamp();
        var pointer = _hasFramePointerOverride
            ? _framePointerOverride
            : capturePointer();

        // The fixed host is larger than the current visible surface. Every transition frame
        // resamples the real physical interactive rectangle, so transparent reserve pixels never
        // become hover intent and a shrinking surface can retarget in either direction.
        if ((dirty & EdgeCapsuleDirty.Frame) != 0)
        {
            dirty |= EdgeCapsuleDirty.Pointer;
        }
        if ((dirty & EdgeCapsuleDirty.Pointer) != 0 && SamplePointer(pointer))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Pointer));
            dirty |= EdgeCapsuleDirty.Presentation;
        }

        if ((dirty & EdgeCapsuleDirty.Measure) != 0)
        {
            if (State.Gesture is
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingTransfer or
                EdgeCapsuleGestureState.FloatingReordering)
            {
                remaining |= EdgeCapsuleDirty.Measure;
            }
            else
            {
                _layoutSnapshot = captureLayout();
                RequestPresentation(EdgeCapsuleMotion.Preserve(
                    EdgeCapsuleTransitionReason.Measure));
                dirty |= EdgeCapsuleDirty.Presentation;
            }
        }

        if ((dirty & (EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Frame)) == 0)
        {
            return remaining;
        }

        var layout = _layoutSnapshot ?? captureLayout();
        _layoutSnapshot = layout;
        var result = ReconcilePresentation(layout, apply, now);
        if (!result.Applied)
        {
            remaining |= EdgeCapsuleDirty.Presentation;
            return remaining;
        }
        if (result.NeedsNextFrame)
        {
            remaining |= EdgeCapsuleDirty.Frame;
        }

        // The frame just committed can change the physical hit rectangle. Re-evaluate once from
        // that exact frame; if intent changes, retarget from the frame already on screen.
        if (SamplePointer(pointer))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Pointer));
            var retarget = ReconcilePresentation(layout, apply, now);
            if (!retarget.Applied)
            {
                remaining |= EdgeCapsuleDirty.Presentation;
            }
            if (retarget.NeedsNextFrame)
            {
                remaining |= EdgeCapsuleDirty.Frame;
            }
        }

        return remaining;
    }

    private PresentationResult ReconcilePresentation(
        EdgeCapsuleLayoutSnapshot layout,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long nowTimestamp)
    {
        var plan = EdgeCapsuleTargetPlanner.Calculate(Model, layout);
        var targetChanged = plan != TargetPlan;
        var motionMustRebase = _pendingMotion.Kind == EdgeCapsuleMotionKind.Snap &&
            Transition.HasValue;
        if (targetChanged || motionMustRebase)
        {
            Transition = EdgeCapsuleTransitionPolicy.Create(
                AppliedPresentation,
                plan.Docked,
                _pendingMotion,
                Transition.HasValue,
                nowTimestamp,
                Stopwatch.Frequency);
            TargetPlan = plan;
        }

        _pendingMotion = EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.State);
        var sample = Transition is { } active
            ? EdgeCapsuleTransitionPolicy.Sample(active, nowTimestamp)
            : new EdgeCapsuleTransitionSample(TargetPresentation.ToFrame(), true);
        var shouldApply = sample.Frame != AppliedPresentation || targetChanged;
        var applied = !shouldApply || apply(sample.Frame);
        if (applied && shouldApply)
        {
            AppliedPresentation = sample.Frame;
        }

        if (!applied)
        {
            return new PresentationResult(false, Transition.HasValue);
        }

        if (sample.IsComplete)
        {
            Transition = null;
        }

        var retractionCompleted = sample.IsComplete && State.Slot is (
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded);
        if (retractionCompleted)
        {
            var reduction = Dispatch(EdgeCapsuleIntent.RetractionCompleted());
            if (reduction.Accepted)
            {
                TargetPlan = EdgeCapsulePresentationPlan.Hidden;
                var hidden = EdgeCapsulePresentationFrame.Hidden;
                if (apply(hidden))
                {
                    AppliedPresentation = hidden;
                }
            }
        }

        return new PresentationResult(true, Transition.HasValue);
    }

    public void Invalidate(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);
        Schedule();
    }

    public void Flush(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);
        _reconcileGeneration++;
        _reconcileScheduled = false;
        RunReconcile();
    }

    public void ClearDeferredWork()
    {
        _dirty = EdgeCapsuleDirty.None;
        _reconcileScheduled = false;
        _reconcileGeneration++;
        StopFrameScheduler();
    }

    public void CancelTransition()
    {
        Transition = null;
        StopFrameScheduler();
    }

    public void ResetPresentation()
    {
        CancelTransition();
        TargetPlan = EdgeCapsulePresentationPlan.Hidden;
        AppliedPresentation = EdgeCapsulePresentationFrame.Hidden;
        _layoutSnapshot = null;
        _pendingMotion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);
        ClearDeferredWork();
    }

    public void Reset()
    {
        Dispatch(EdgeCapsuleIntent.ResetModel());
        ResetPresentation();
    }

    private bool SamplePointer(DeviceScreenPoint? pointer)
    {
        var over = pointer.HasValue &&
            AppliedPresentation.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(
                AppliedPresentation.InteractiveBounds,
                pointer.Value);
        return Dispatch(EdgeCapsuleIntent.PointerSampled(over)).Changed;
    }

    private void Configure(
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        if (_dispatcher != null && !ReferenceEquals(_dispatcher, dispatcher))
        {
            StopFrameScheduler();
            _frameScheduler = null;
            _layoutSnapshot = null;
        }

        _dispatcher = dispatcher;
        _reconcile = reconcile;
    }

    private void Schedule()
    {
        if (_reconcileScheduled || _dispatcher == null || _reconcile == null)
        {
            return;
        }

        _reconcileScheduled = true;
        var generation = ++_reconcileGeneration;
        _dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (generation != _reconcileGeneration)
                {
                    return;
                }
                _reconcileScheduled = false;
                RunReconcile();
            }),
            DispatcherPriority.Loaded);
    }

    private void RunReconcile()
    {
        if (_reconcile == null)
        {
            return;
        }
        var dirty = _dirty;
        _dirty = EdgeCapsuleDirty.None;
        var remaining = _reconcile(dirty);
        var needsFrame = (remaining & EdgeCapsuleDirty.Frame) != 0;
        _dirty |= remaining & ~EdgeCapsuleDirty.Frame;
        if (needsFrame)
        {
            StartFrameScheduler();
        }
        else if (!Transition.HasValue)
        {
            StopFrameScheduler();
        }
    }

    private void StartFrameScheduler()
    {
        if (_dispatcher == null || _reconcile == null)
        {
            return;
        }
        if (_frameSchedulerActive)
        {
            return;
        }

        _frameScheduler ??= EdgeCapsuleFrameScheduler.For(_dispatcher);
        _frameSchedulerActive = true;
        _frameScheduler.Activate(this);
    }

    private void StopFrameScheduler()
    {
        if (!_frameSchedulerActive)
        {
            return;
        }

        _frameSchedulerActive = false;
        _frameScheduler?.Deactivate(this);
    }

    internal bool UsesSharedFrameScheduler(EdgeCapsuleFrameScheduler scheduler) =>
        _frameSchedulerActive && ReferenceEquals(_frameScheduler, scheduler);

    internal bool AdvanceSharedFrame(
        EdgeCapsuleFrameScheduler scheduler,
        DeviceScreenPoint? pointer)
    {
        if (!UsesSharedFrameScheduler(scheduler) ||
            _dispatcher == null ||
            _reconcile == null ||
            !Transition.HasValue)
        {
            StopFrameScheduler();
            return false;
        }

        // Merge queued work into this render tick and invalidate its stale dispatcher callback.
        // Flush, deferred invalidation and animation still execute the same RunReconcile path.
        _dirty |= EdgeCapsuleDirty.Frame;
        _reconcileGeneration++;
        _reconcileScheduled = false;
        _hasFramePointerOverride = true;
        _framePointerOverride = pointer;
        try
        {
            RunReconcile();
        }
        finally
        {
            _framePointerOverride = null;
            _hasFramePointerOverride = false;
        }
        return UsesSharedFrameScheduler(scheduler) && Transition.HasValue;
    }
}
