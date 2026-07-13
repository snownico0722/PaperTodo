using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace PaperTodo;

internal enum EdgeCapsuleSlotState
{
    None,
    CollapsedDocked,
    ExpandedReserved,
    RetractedCollapsed,
    RetractedExpanded,
    RetractingCollapsed,
    RetractingExpanded
}

internal enum EdgeCapsuleVisualState
{
    Resting,
    Hovered,
    Active
}

internal enum EdgeCapsuleGestureState
{
    Idle,
    PendingClick,
    DockedReordering,
    FloatingTransfer,
    FloatingReordering
}

internal enum EdgeCapsuleOpenOrigin
{
    Normal,
    EdgeSlot
}

internal enum EdgeCapsuleCaptureAction
{
    None,
    IgnoreExpectedTransfer,
    Recapture,
    CancelDrag
}

[Flags]
internal enum EdgeCapsuleDirty
{
    None = 0,
    Presentation = 1 << 0,
    Measure = 1 << 1,
    Pointer = 1 << 2,
    Frame = 1 << 3
}

internal readonly record struct EdgeCapsuleState(
    EdgeCapsuleSlotState Slot,
    EdgeCapsuleVisualState Visual,
    EdgeCapsuleGestureState Gesture,
    EdgeCapsuleOpenOrigin OpenOrigin)
{
    public static EdgeCapsuleState Initial => new(
        EdgeCapsuleSlotState.None,
        EdgeCapsuleVisualState.Resting,
        EdgeCapsuleGestureState.Idle,
        EdgeCapsuleOpenOrigin.Normal);
}

internal sealed class EdgeCapsuleDragSession
{
    public EdgeCapsuleDragSession(DeviceScreenPoint pointerDownScreenPosition)
    {
        PointerDownScreenPosition = pointerDownScreenPosition;
        LastScreenPosition = pointerDownScreenPosition;
    }

    public DeviceScreenPoint PointerDownScreenPosition { get; }
    public DeviceScreenPoint LastScreenPosition { get; set; }
    public string StartMonitorDeviceName { get; set; } = "";
    public double DockedPointerOffsetY { get; set; }
    public int PreviewIndex { get; set; } = -1;
}

internal readonly record struct EdgeCapsuleModel(
    EdgeCapsuleState State,
    EdgeCapsulePlacement Placement,
    EdgeCapsuleDragSession? DragSession,
    bool ContextMenuOpen,
    double? DockedDragTopDipOverride)
{
    public static EdgeCapsuleModel Initial => new(
        EdgeCapsuleState.Initial,
        EdgeCapsulePlacement.None,
        null,
        false,
        null);
}

internal enum EdgeCapsuleEventKind
{
    SetSlot,
    SetVisual,
    SetGesture,
    SetOpenOrigin,
    SetPlacement,
    SetContextMenuOpen,
    BeginPointerInteraction,
    FinishPointerInteraction,
    SetDockedDragTop,
    ClearDockedDragTop,
    Reset
}

internal readonly record struct EdgeCapsuleEvent(
    EdgeCapsuleEventKind Kind,
    EdgeCapsuleSlotState Slot = EdgeCapsuleSlotState.None,
    EdgeCapsuleVisualState Visual = EdgeCapsuleVisualState.Resting,
    EdgeCapsuleGestureState Gesture = EdgeCapsuleGestureState.Idle,
    EdgeCapsuleOpenOrigin OpenOrigin = EdgeCapsuleOpenOrigin.Normal,
    EdgeCapsulePlacement Placement = default,
    bool Flag = false,
    double Value = 0,
    DeviceScreenPoint Point = default)
{
    public static EdgeCapsuleEvent SlotChanged(EdgeCapsuleSlotState value) =>
        new(EdgeCapsuleEventKind.SetSlot, Slot: value);

    public static EdgeCapsuleEvent VisualChanged(EdgeCapsuleVisualState value) =>
        new(EdgeCapsuleEventKind.SetVisual, Visual: value);

    public static EdgeCapsuleEvent GestureChanged(EdgeCapsuleGestureState value) =>
        new(EdgeCapsuleEventKind.SetGesture, Gesture: value);

    public static EdgeCapsuleEvent OpenOriginChanged(EdgeCapsuleOpenOrigin value) =>
        new(EdgeCapsuleEventKind.SetOpenOrigin, OpenOrigin: value);

    public static EdgeCapsuleEvent PlacementChanged(EdgeCapsulePlacement value) =>
        new(EdgeCapsuleEventKind.SetPlacement, Placement: value);

    public static EdgeCapsuleEvent ContextMenuChanged(bool open) =>
        new(EdgeCapsuleEventKind.SetContextMenuOpen, Flag: open);

    public static EdgeCapsuleEvent PointerStarted(DeviceScreenPoint point) =>
        new(EdgeCapsuleEventKind.BeginPointerInteraction, Point: point);

    public static EdgeCapsuleEvent PointerFinished() =>
        new(EdgeCapsuleEventKind.FinishPointerInteraction);

    public static EdgeCapsuleEvent DockedDragTopChanged(double topDip) =>
        new(EdgeCapsuleEventKind.SetDockedDragTop, Value: topDip);

    public static EdgeCapsuleEvent DockedDragTopCleared() =>
        new(EdgeCapsuleEventKind.ClearDockedDragTop);

    public static EdgeCapsuleEvent ResetModel() => new(EdgeCapsuleEventKind.Reset);
}

internal readonly record struct EdgeCapsuleReduction(
    bool Accepted,
    EdgeCapsuleModel Model,
    string? Error = null);

internal static class EdgeCapsuleReducer
{
    public static EdgeCapsuleReduction Reduce(EdgeCapsuleModel model, EdgeCapsuleEvent input)
    {
        var next = input.Kind switch
        {
            EdgeCapsuleEventKind.SetSlot => SetSlot(model, input.Slot),
            EdgeCapsuleEventKind.SetVisual => SetVisual(model, input.Visual),
            EdgeCapsuleEventKind.SetGesture => SetGesture(model, input.Gesture),
            EdgeCapsuleEventKind.SetOpenOrigin => Accept(model with
            {
                State = model.State with { OpenOrigin = input.OpenOrigin }
            }),
            EdgeCapsuleEventKind.SetPlacement => Accept(model with
            {
                Placement = input.Placement.Normalize()
            }),
            EdgeCapsuleEventKind.SetContextMenuOpen => Accept(model with
            {
                ContextMenuOpen = input.Flag
            }),
            EdgeCapsuleEventKind.BeginPointerInteraction => BeginPointer(model, input.Point),
            EdgeCapsuleEventKind.FinishPointerInteraction => Accept(model with
            {
                State = model.State with { Gesture = EdgeCapsuleGestureState.Idle },
                DragSession = null,
                DockedDragTopDipOverride = null
            }),
            EdgeCapsuleEventKind.SetDockedDragTop => Accept(model with
            {
                DockedDragTopDipOverride = input.Value
            }),
            EdgeCapsuleEventKind.ClearDockedDragTop => Accept(model with
            {
                DockedDragTopDipOverride = null
            }),
            EdgeCapsuleEventKind.Reset => Accept(EdgeCapsuleModel.Initial),
            _ => Reject(model, $"Unknown edge-capsule event {input.Kind}.")
        };

        if (next.Accepted && !HasStructuralInvariants(next.Model))
        {
            return Reject(model, $"Edge-capsule event {input.Kind} violated a structural invariant.");
        }
        return next;
    }

    private static EdgeCapsuleReduction SetSlot(
        EdgeCapsuleModel model,
        EdgeCapsuleSlotState state)
    {
        if (!CanTransitionSlot(model.State.Slot, state))
        {
            return Reject(model, $"Illegal slot transition {model.State.Slot} -> {state}.");
        }
        if (state is EdgeCapsuleSlotState.RetractedCollapsed or EdgeCapsuleSlotState.RetractedExpanded &&
            model.State.Visual != EdgeCapsuleVisualState.Resting)
        {
            return Reject(model, "A retracted capsule must be Resting.");
        }
        return Accept(model with { State = model.State with { Slot = state } });
    }

    private static EdgeCapsuleReduction SetVisual(
        EdgeCapsuleModel model,
        EdgeCapsuleVisualState state)
    {
        if (model.State.Slot is EdgeCapsuleSlotState.RetractedCollapsed or EdgeCapsuleSlotState.RetractedExpanded &&
            state != EdgeCapsuleVisualState.Resting)
        {
            return Reject(model, "A retracted capsule cannot become Hovered or Active.");
        }
        return Accept(model with { State = model.State with { Visual = state } });
    }

    private static EdgeCapsuleReduction SetGesture(
        EdgeCapsuleModel model,
        EdgeCapsuleGestureState state)
    {
        if (!CanTransitionGesture(model.State.Gesture, state))
        {
            return Reject(model, $"Illegal gesture transition {model.State.Gesture} -> {state}.");
        }
        if (state != EdgeCapsuleGestureState.Idle && model.DragSession == null)
        {
            return Reject(model, $"Gesture {state} requires a drag session.");
        }
        return Accept(model with { State = model.State with { Gesture = state } });
    }

    private static EdgeCapsuleReduction BeginPointer(
        EdgeCapsuleModel model,
        DeviceScreenPoint point)
    {
        if (model.State.Gesture != EdgeCapsuleGestureState.Idle)
        {
            return Reject(model, $"Cannot begin pointer interaction from {model.State.Gesture}.");
        }
        return Accept(model with
        {
            State = model.State with { Gesture = EdgeCapsuleGestureState.PendingClick },
            DragSession = new EdgeCapsuleDragSession(point),
            DockedDragTopDipOverride = null
        });
    }

    private static bool HasStructuralInvariants(EdgeCapsuleModel model) =>
        (model.State.Gesture == EdgeCapsuleGestureState.Idle) == (model.DragSession == null) &&
        (model.State.Slot is not (
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractedExpanded) ||
            model.State.Visual == EdgeCapsuleVisualState.Resting);

    private static bool CanTransitionSlot(EdgeCapsuleSlotState from, EdgeCapsuleSlotState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            EdgeCapsuleSlotState.None => to is
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractedExpanded,
            EdgeCapsuleSlotState.CollapsedDocked => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractedExpanded or
                EdgeCapsuleSlotState.RetractingCollapsed,
            EdgeCapsuleSlotState.ExpandedReserved => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractedExpanded or
                EdgeCapsuleSlotState.RetractingExpanded,
            EdgeCapsuleSlotState.RetractedCollapsed => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.RetractedExpanded or
                EdgeCapsuleSlotState.RetractingCollapsed,
            EdgeCapsuleSlotState.RetractedExpanded => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractingExpanded,
            EdgeCapsuleSlotState.RetractingCollapsed => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.ExpandedReserved,
            EdgeCapsuleSlotState.RetractingExpanded => to is
                EdgeCapsuleSlotState.None or
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.RetractedExpanded,
            _ => false
        };
    }

    private static bool CanTransitionGesture(EdgeCapsuleGestureState from, EdgeCapsuleGestureState to)
    {
        if (from == to || to == EdgeCapsuleGestureState.Idle)
        {
            return true;
        }

        return from switch
        {
            EdgeCapsuleGestureState.Idle => to == EdgeCapsuleGestureState.PendingClick,
            EdgeCapsuleGestureState.PendingClick => to == EdgeCapsuleGestureState.DockedReordering,
            EdgeCapsuleGestureState.DockedReordering => to == EdgeCapsuleGestureState.FloatingTransfer,
            EdgeCapsuleGestureState.FloatingTransfer => to == EdgeCapsuleGestureState.FloatingReordering,
            EdgeCapsuleGestureState.FloatingReordering => false,
            _ => false
        };
    }

    private static EdgeCapsuleReduction Accept(EdgeCapsuleModel model) => new(true, model);
    private static EdgeCapsuleReduction Reject(EdgeCapsuleModel model, string error) =>
        new(false, model, error);
}

internal readonly record struct EdgeCapsulePresentationResult(
    bool Applied,
    bool NeedsNextFrame,
    bool RetractionCompleted);

/// <summary>
/// The sole owner and orchestrator for desired state, target layout, transition and applied frame.
/// PaperWindow supplies environment facts and one Host.Apply callback; it cannot write a parallel
/// requested/applied geometry or animation progress.
/// </summary>
internal sealed class EdgeCapsulePresenter
{
    private EdgeCapsuleDirty _dirty;
    private bool _reconcileScheduled;
    private int _reconcileGeneration;
    private DispatcherTimer? _frameTimer;
    private Dispatcher? _dispatcher;
    private Func<EdgeCapsuleDirty, EdgeCapsuleDirty>? _reconcile;
    private EdgeCapsuleMotion _pendingMotion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);

    public EdgeCapsuleModel Model { get; private set; } = EdgeCapsuleModel.Initial;
    public EdgeCapsuleState State => Model.State;
    public EdgeCapsuleDragSession? DragSession => Model.DragSession;
    public EdgeCapsulePlacement Placement => Model.Placement;
    public bool ContextMenuOpen => Model.ContextMenuOpen;
    public EdgeCapsuleTargetPresentation TargetPresentation { get; private set; } =
        EdgeCapsuleTargetPresentation.Hidden;
    public EdgeCapsulePresentationFrame AppliedPresentation { get; private set; } =
        EdgeCapsulePresentationFrame.Hidden;
    public EdgeCapsuleTransition? Transition { get; private set; }

    public bool Dispatch(
        EdgeCapsuleEvent input,
        [CallerMemberName] string reason = "")
    {
        var reduction = EdgeCapsuleReducer.Reduce(Model, input);
        if (!reduction.Accepted)
        {
            Debug.Fail($"{reduction.Error} ({reason})");
            return false;
        }
        Model = reduction.Model;
        return true;
    }

    public EdgeCapsuleCaptureAction HandleCaptureLost(bool leftButtonPressed)
    {
        switch (State.Gesture)
        {
            case EdgeCapsuleGestureState.FloatingTransfer:
                return EdgeCapsuleCaptureAction.IgnoreExpectedTransfer;
            case EdgeCapsuleGestureState.PendingClick:
                Dispatch(EdgeCapsuleEvent.PointerFinished());
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
        // Several semantic events can coalesce before one deferred reconcile. A passive measure
        // or display refresh must not downgrade a requested animation, while an explicit snap
        // (drag/capture/lifecycle hand-off) must remain authoritative regardless of call order.
        if (motion.Kind == EdgeCapsuleMotionKind.Snap ||
            _pendingMotion.Kind == EdgeCapsuleMotionKind.Preserve ||
            (motion.Kind == EdgeCapsuleMotionKind.Animate &&
                _pendingMotion.Kind != EdgeCapsuleMotionKind.Snap))
        {
            _pendingMotion = motion;
        }
    }

    public EdgeCapsulePresentationResult ReconcilePresentation(
        EdgeCapsuleLayoutSnapshot layout,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long? nowTimestamp = null)
    {
        var now = nowTimestamp ?? Stopwatch.GetTimestamp();
        var target = EdgeCapsuleTargetPlanner.Calculate(Model, layout);
        var targetChanged = target != TargetPresentation;
        var motionMustRebase = _pendingMotion.Kind == EdgeCapsuleMotionKind.Snap &&
            Transition.HasValue;
        if (targetChanged || motionMustRebase)
        {
            var transition = EdgeCapsuleTransitionPolicy.Create(
                AppliedPresentation,
                target,
                _pendingMotion,
                Transition.HasValue,
                now,
                Stopwatch.Frequency);
            TargetPresentation = target;
            Transition = transition;
        }

        _pendingMotion = EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.State);
        var sample = Transition is { } active
            ? EdgeCapsuleTransitionPolicy.Sample(active, now)
            : new EdgeCapsuleTransitionSample(TargetPresentation.ToFrame(), true);
        var shouldApply = sample.Frame != AppliedPresentation || targetChanged;
        var applied = !shouldApply || apply(sample.Frame);
        if (applied && shouldApply)
        {
            AppliedPresentation = sample.Frame;
        }

        if (!applied)
        {
            return new EdgeCapsulePresentationResult(false, Transition.HasValue, false);
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
            var slotReduction = EdgeCapsuleReducer.Reduce(
                Model,
                EdgeCapsuleEvent.SlotChanged(EdgeCapsuleSlotState.None));
            if (slotReduction.Accepted)
            {
                Model = slotReduction.Model;
            }
            Dispatch(EdgeCapsuleEvent.PlacementChanged(EdgeCapsulePlacement.None));
            Dispatch(EdgeCapsuleEvent.DockedDragTopCleared());
            TargetPresentation = EdgeCapsuleTargetPresentation.Hidden;
            var hidden = EdgeCapsulePresentationFrame.Hidden;
            if (apply(hidden))
            {
                AppliedPresentation = hidden;
            }
        }

        return new EdgeCapsulePresentationResult(
            true,
            Transition.HasValue,
            retractionCompleted);
    }

    public void Invalidate(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dirty |= dirty;
        _dispatcher = dispatcher;
        _reconcile = reconcile;
        Schedule(dispatcher, reconcile);
    }

    public void Wake(
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dispatcher = dispatcher;
        _reconcile = reconcile;
        if (_dirty != EdgeCapsuleDirty.None)
        {
            Schedule(dispatcher, reconcile);
        }
    }

    public void ClearDeferredWork()
    {
        _dirty = EdgeCapsuleDirty.None;
        _reconcileScheduled = false;
        _reconcileGeneration++;
        StopFrameClock();
    }

    public void CancelTransition()
    {
        Transition = null;
        StopFrameClock();
    }

    public void ResetPresentation()
    {
        CancelTransition();
        TargetPresentation = EdgeCapsuleTargetPresentation.Hidden;
        AppliedPresentation = EdgeCapsulePresentationFrame.Hidden;
        _pendingMotion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);
        ClearDeferredWork();
    }

    public void Reset()
    {
        Dispatch(EdgeCapsuleEvent.ResetModel());
        ResetPresentation();
    }

    private void Schedule(
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        if (_reconcileScheduled)
        {
            return;
        }

        _reconcileScheduled = true;
        var generation = ++_reconcileGeneration;
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (generation != _reconcileGeneration)
                {
                    return;
                }
                _reconcileScheduled = false;
                var dirty = _dirty;
                _dirty = EdgeCapsuleDirty.None;
                var remaining = reconcile(dirty);
                var needsFrame = (remaining & EdgeCapsuleDirty.Frame) != 0;
                _dirty |= remaining & ~EdgeCapsuleDirty.Frame;
                if (needsFrame)
                {
                    StartFrameClock();
                }
                else if (!Transition.HasValue)
                {
                    StopFrameClock();
                }
            }),
            DispatcherPriority.Loaded);
    }

    private void StartFrameClock()
    {
        if (_dispatcher == null || _reconcile == null)
        {
            return;
        }
        if (_frameTimer == null)
        {
            _frameTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _frameTimer.Tick += (_, _) =>
            {
                if (_dispatcher == null || _reconcile == null || !Transition.HasValue)
                {
                    StopFrameClock();
                    return;
                }
                Invalidate(EdgeCapsuleDirty.Frame, _dispatcher, _reconcile);
            };
        }
        if (!_frameTimer.IsEnabled)
        {
            _frameTimer.Start();
        }
    }

    private void StopFrameClock()
    {
        _frameTimer?.Stop();
    }
}
