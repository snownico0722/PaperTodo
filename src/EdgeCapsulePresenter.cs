using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace PaperTodo;

internal enum EdgeCapsuleNativeBatchApplyStatus
{
    Ready,
    Deferred,
    Failed
}

internal readonly record struct EdgeCapsuleNativeBatchGroup(
    string MonitorDeviceName,
    EdgeCapsuleEdge Edge,
    int WallDeviceX,
    int FallbackIdentity = 0,
    long TransactionGroupId = 0);

/// <summary>
/// Sole owner of desired model, target plan, transition, applied frame and deferred work. Both
/// deferred invalidation and synchronous Flush execute the same reconcile callback.
/// </summary>
internal sealed class EdgeCapsulePresenter
{
    // Give a temporarily invalid HWND real retry opportunities at every refresh rate, while
    // bounding both high-refresh call volume and total retry lifetime. The failure count includes
    // the initial failed apply, so 3 means at least two genuine retries before time can expire.
    private const int MinimumApplyFailureCountBeforeTimeout = 3;
    private const int MaximumApplyFailureCount = 6;
    private const int MaximumApplyRetryMilliseconds = 120;
    private static readonly long MaximumApplyRetryTimestampTicks =
        Math.Max(
            1,
            (long)Math.Ceiling(
                Stopwatch.Frequency *
                MaximumApplyRetryMilliseconds / 1000.0));
    private const EdgeCapsuleDirty PresentationWorkMask =
        EdgeCapsuleDirty.Presentation |
        EdgeCapsuleDirty.Measure |
        EdgeCapsuleDirty.Frame |
        EdgeCapsuleDirty.ApplyRetry |
        EdgeCapsuleDirty.DisplayMetrics;

    private readonly record struct PresentationResult(
        bool Applied,
        bool NeedsNextFrame);

    private sealed class RenderReconcileRegistration
    {
        private EdgeCapsuleFrameScheduler? _scheduler;

        internal RenderReconcileRegistration(EdgeCapsuleFrameScheduler scheduler)
        {
            _scheduler = scheduler;
            scheduler.RegisterRenderReconcile();
        }

        internal void Complete()
        {
            var scheduler = _scheduler;
            _scheduler = null;
            scheduler?.CompleteRenderReconcile();
        }
    }

    private sealed class VisualTransactionDeferral(EdgeCapsulePresenter owner) : IDisposable
    {
        private EdgeCapsulePresenter? _owner = owner;

        public void Dispose()
        {
            var presenter = _owner;
            _owner = null;
            presenter?.ReleaseVisualTransactionDeferral();
        }
    }

    private int _visualTransactionDeferrals;

    internal IDisposable DeferReconcileToVisualTransaction()
    {
        _visualTransactionDeferrals++;
        // Retain dirty input/measure work, but retire its earlier Send/Render callback. Otherwise
        // it can see the newly staged Preview model before the queue installs its shared motion.
        CancelQueuedReconcile();
        return new VisualTransactionDeferral(this);
    }

    private void ReleaseVisualTransactionDeferral()
    {
        _visualTransactionDeferrals--;
        if (_visualTransactionDeferrals == 0 && _dirty != EdgeCapsuleDirty.None &&
            _dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher &&
            _reconcile is { } reconcile)
        {
            // Also drain unconsumed work after cancellation or a failed queue admission.
            QueueReconcile(EdgeCapsuleDirty.None, dispatcher, reconcile, beforeNextRender: false);
        }
    }

    private EdgeCapsuleDirty _dirty;
    private bool _reconcileScheduled;
    private DispatcherOperation? _reconcileOperation;
    private RenderReconcileRegistration? _reconcileRegistration;
    private int _reconcileGeneration;
    private EdgeCapsuleFrameScheduler? _frameScheduler;
    private bool _frameSchedulerActive;
    private Dispatcher? _dispatcher;
    private Func<EdgeCapsuleDirty, EdgeCapsuleDirty>? _reconcile;
    private long? _reconcileTimestampOverride;
    private EdgeCapsuleLayoutSnapshot? _layoutSnapshot;
    private EdgeCapsuleNativeBatchGroup _nativeBatchGroup;
    private long _nativeBatchTransactionGroupId;
    private bool _hasFramePointerOverride;
    private DeviceScreenPoint? _framePointerOverride;
    private int _forceApplyVersion;
    private int _appliedForceApplyVersion;
    private long _applyRetryStartedAtTimestamp;
    private int _applyFailureCount;
    private bool _applyRetryExhausted;
    private Action<bool>? _presentationSettleCallback;
    private Action? _nativeBatchApplyRejectedCallback;
    private Action? _nativeBatchApplyDeferredCallback;
    private EdgeCapsuleFrameScheduler? _advancingSharedFrameScheduler;
    private bool _nativeBatchApplyActive;
    private bool _nativeBatchApplySucceeded;
    private bool _nativeBatchApplyAttempted;
    private bool _nativeBatchApplyDeferred;
    private bool _nativeBatchDeferredCallbackScheduled;
    private int _nativeBatchDeferredRecoveryGeneration;
    private bool _nativeBatchRetryPending;
    private int _nativeBatchCommitVersion;
    private bool _rebasePendingTransition;
    private EdgeCapsuleMotion _pendingMotion =
        EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);

    internal string DiagnosticId { get; set; } = "<unassigned>";

    private EdgeCapsuleModel Model { get; set; } = EdgeCapsuleModel.Initial;
    public EdgeCapsuleState State => Model.State;
    public EdgeCapsuleDragSession? DragSession => Model.DragSession;
    public EdgeCapsulePlacement Placement => Model.Placement;
    public EdgeCapsulePreviewState Preview => Model.Preview;
    public bool PointerOverSurface => Model.PointerOverSurface;
    public bool ContextMenuOpen => Model.ContextMenuOpen;
    public bool PeerReorderActive => Model.PeerReorderActive;
    private EdgeCapsulePresentationPlan TargetPlan { get; set; } =
        EdgeCapsulePresentationPlan.Hidden;
    private EdgeCapsuleTargetPresentation TargetPresentation => TargetPlan.Docked;
    public EdgeCapsuleFloatingShape FloatingShape => TargetPlan.Floating;
    public EdgeCapsulePresentationFrame AppliedPresentation { get; private set; } =
        EdgeCapsulePresentationFrame.Hidden;
    public int AppliedPresentationVersion { get; private set; }
    public DeviceScreenPoint? LastPointerSample { get; private set; }
    internal bool HasActiveTransition => Transition.HasValue;

    internal EdgeCapsuleTargetPresentation PlanTargetPresentation(
        EdgeCapsuleLayoutSnapshot layout) =>
        EdgeCapsuleTargetPlanner.Calculate(Model, layout).Docked;
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

    public EdgeCapsuleCaptureAction HandleCaptureLost(EdgeCapsuleCaptureLoss captureLoss) =>
        Dispatch(EdgeCapsuleIntent.CaptureLost(captureLoss)).CaptureAction;

    public void RequestPresentation(
        EdgeCapsuleMotion motion,
        bool rebaseActiveTransition = false)
    {
        _rebasePendingTransition |= rebaseActiveTransition;

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

    internal void RebaseActiveTransitionStart(long startedAtTimestamp)
    {
        if (Transition is not { } active)
        {
            return;
        }

        // Queue endpoint settlement and the blocking cover publication have finished. No motion
        // has been published yet, so WPF and DirectComposition share this fresh animation start.
        Transition = active with
        {
            Start = AppliedPresentation,
            StartedAtTimestamp = startedAtTimestamp
        };
    }

    public void ForceApplyCurrentPresentation()
    {
        ResetApplyRetryWindow();
        unchecked
        {
            _forceApplyVersion++;
        }
    }

    public void NotifyWhenPresentationSettled(Action<bool> callback)
    {
        _presentationSettleCallback = callback;
    }

    public void ClearPresentationSettleNotification()
    {
        _presentationSettleCallback = null;
    }

    internal void SetNativeBatchApplyRejectedCallback(Action callback)
    {
        _nativeBatchApplyRejectedCallback = callback;
    }

    internal void SetNativeBatchApplyDeferredCallback(Action callback)
    {
        _nativeBatchApplyDeferredCallback = callback;
    }

    internal void JoinNativeBatchTransactionGroup(long groupId)
    {
        if (groupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }
        if (_nativeBatchTransactionGroupId == groupId)
        {
            StartFrameScheduler();
            return;
        }

        _nativeBatchTransactionGroupId = groupId;
        ResetApplyRetryWindow();
        StartFrameScheduler();
    }

    public EdgeCapsuleDirty Reconcile(
        EdgeCapsuleDirty dirty,
        Func<EdgeCapsuleLayoutSnapshot> captureLayout,
        Func<DeviceScreenPoint?> capturePointer,
        Func<EdgeCapsulePresentationFrame, EdgeCapsulePresentationFrame>
            resolvePresentedFrame,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long? nowTimestamp = null)
    {
        var remaining = EdgeCapsuleDirty.None;
        var now = nowTimestamp ??
            _reconcileTimestampOverride ??
            Stopwatch.GetTimestamp();
        var pointer = _hasFramePointerOverride
            ? _framePointerOverride
            : capturePointer();
        LastPointerSample = pointer;

        // Every transition frame resamples the physical interactive rectangle. The queue proxy
        // can expose its sampled visual frame to the controller while this Presenter's business
        // frame is already at the endpoint; neither path may treat transparent pixels as intent.
        if ((dirty & EdgeCapsuleDirty.Frame) != 0)
        {
            dirty |= EdgeCapsuleDirty.Pointer;
        }
        if ((dirty & EdgeCapsuleDirty.Pointer) != 0 &&
            SamplePointer(pointer, resolvePresentedFrame(AppliedPresentation)))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Pointer,
                EdgeCapsuleLayout.HorizontalResizeMilliseconds));
            dirty |= EdgeCapsuleDirty.Presentation;
        }

        if ((dirty & EdgeCapsuleDirty.Measure) != 0)
        {
            var displayMetrics = (dirty & EdgeCapsuleDirty.DisplayMetrics) != 0;
            if (State.Gesture is
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingTransfer or
                EdgeCapsuleGestureState.FloatingReordering)
            {
                remaining |= EdgeCapsuleDirty.Measure;
                if (displayMetrics)
                {
                    // Unlike a title-only measure, display geometry cannot be force-replayed from
                    // the previous drag snapshot. Keep this batch intact for gesture completion.
                    if (_nativeBatchApplyActive && _nativeBatchRetryPending)
                    {
                        _nativeBatchApplyDeferred = true;
                    }
                    Transition = null;
                    remaining |= EdgeCapsuleDirty.Presentation |
                        EdgeCapsuleDirty.DisplayMetrics;
                    return remaining;
                }
            }
            else
            {
                SetLayoutSnapshot(captureLayout());
                RequestPresentation(EdgeCapsuleMotion.Preserve(
                    displayMetrics
                        ? EdgeCapsuleTransitionReason.DisplayMetrics
                        : EdgeCapsuleTransitionReason.Measure));
                dirty |= EdgeCapsuleDirty.Presentation;
            }
        }

        if ((dirty & (EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Frame)) == 0)
        {
            return remaining;
        }

        var layout = _layoutSnapshot ?? captureLayout();
        SetLayoutSnapshot(layout);
        var result = ReconcilePresentation(layout, apply, now);
        if (!result.Applied)
        {
            remaining |= EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.ApplyRetry;
            return remaining;
        }
        if (result.NeedsNextFrame)
        {
            remaining |= EdgeCapsuleDirty.Frame;
        }

        // The frame just committed can change the physical hit rectangle. Re-evaluate once from
        // that exact frame; if intent changes, retarget from the frame already on screen.
        if (SamplePointer(pointer, resolvePresentedFrame(AppliedPresentation)))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Pointer,
                EdgeCapsuleLayout.HorizontalResizeMilliseconds));
            var retarget = ReconcilePresentation(layout, apply, now);
            if (!retarget.Applied)
            {
                remaining |= EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.ApplyRetry;
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
        var forceApplyVersion = _forceApplyVersion;
        var forceApply = _appliedForceApplyVersion != forceApplyVersion;
        var plan = EdgeCapsuleTargetPlanner.Calculate(Model, layout);
        var targetChanged = plan != TargetPlan;
        var motionMustRebase = Transition.HasValue &&
            (_pendingMotion.Kind == EdgeCapsuleMotionKind.Snap ||
                _rebasePendingTransition);
        if (targetChanged || motionMustRebase)
        {
            if (targetChanged)
            {
                ResetApplyRetryWindow();
            }
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
        _rebasePendingTransition = false;
        var sample = Transition is { } active
            ? EdgeCapsuleTransitionPolicy.Sample(active, nowTimestamp)
            : new EdgeCapsuleTransitionSample(
                EdgeCapsuleTransitionPolicy.ResolveSettledFrame(
                    AppliedPresentation,
                    TargetPresentation),
                true);
        var shouldApply = forceApply || sample.Frame != AppliedPresentation || targetChanged;
        var applied = !shouldApply || ApplyPresentationFrame(apply, sample.Frame);
        if (applied && shouldApply)
        {
            SetAppliedPresentation(sample.Frame);
            _appliedForceApplyVersion = forceApplyVersion;
            if (!_nativeBatchApplyActive)
            {
                ResetApplyRetryWindow();
                unchecked
                {
                    _nativeBatchCommitVersion++;
                }
            }
        }

        if (!applied)
        {
            RegisterApplyFailure(nowTimestamp);
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
                if (ApplyPresentationFrame(apply, hidden))
                {
                    SetAppliedPresentation(hidden);
                }
                else
                {
                    RegisterApplyFailure(nowTimestamp);
                    return new PresentationResult(false, Transition.HasValue);
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
        QueueReconcile(dirty, dispatcher, reconcile, beforeNextRender: false);
    }

    public void InvalidateBeforeNextRender(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        QueueReconcile(dirty, dispatcher, reconcile, beforeNextRender: true);
    }

    public void Flush(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile,
        long? nowTimestamp = null)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);
        CancelQueuedReconcile();
        RunReconcile(nowTimestamp, synchronousFlush: true);
    }

    public void ClearDeferredWork()
    {
        _dirty = EdgeCapsuleDirty.None;
        _nativeBatchApplyActive = false;
        _nativeBatchApplySucceeded = false;
        _nativeBatchApplyAttempted = false;
        _nativeBatchApplyDeferred = false;
        _nativeBatchDeferredCallbackScheduled = false;
        _nativeBatchDeferredRecoveryGeneration++;
        _nativeBatchRetryPending = false;
        _nativeBatchTransactionGroupId = 0;
        CancelQueuedReconcile();
        _presentationSettleCallback = null;
        ResetApplyRetryWindow();
        StopFrameScheduler();
    }

    public void CancelTransition()
    {
        Transition = null;
        if (_nativeBatchTransactionGroupId == 0 &&
            !_nativeBatchRetryPending)
        {
            StopFrameScheduler();
        }
    }

    public void ResetPresentation()
    {
        CancelTransition();
        TargetPlan = EdgeCapsulePresentationPlan.Hidden;
        SetAppliedPresentation(EdgeCapsulePresentationFrame.Hidden);
        LastPointerSample = null;
        _layoutSnapshot = null;
        _nativeBatchGroup = default;
        _appliedForceApplyVersion = _forceApplyVersion;
        _pendingMotion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);
        _rebasePendingTransition = false;
        ClearDeferredWork();
    }

    public void Reset()
    {
        Dispatch(EdgeCapsuleIntent.ResetModel());
        ResetPresentation();
    }

    private bool SamplePointer(
        DeviceScreenPoint? pointer,
        EdgeCapsulePresentationFrame presentedFrame)
    {
        var over = pointer.HasValue &&
            presentedFrame.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(
                presentedFrame.InteractiveBounds,
                pointer.Value);
        return Dispatch(EdgeCapsuleIntent.PointerSampled(over)).Changed;
    }

    private void SetAppliedPresentation(
        EdgeCapsulePresentationFrame presentation)
    {
        if (AppliedPresentation == presentation)
        {
            return;
        }

        AppliedPresentation = presentation;
        unchecked
        {
            AppliedPresentationVersion++;
        }
    }

    private bool ApplyPresentationFrame(
        Func<EdgeCapsulePresentationFrame, bool> apply,
        EdgeCapsulePresentationFrame frame)
    {
        var applied = apply(frame);
        if (_nativeBatchApplyActive)
        {
            _nativeBatchApplyAttempted = true;
        }
        if (!applied && _nativeBatchApplyActive)
        {
            _nativeBatchApplySucceeded = false;
        }
        return applied;
    }

    private void RegisterApplyFailure(long nowTimestamp)
    {
        if (_applyRetryExhausted)
        {
            return;
        }
        if (_applyRetryStartedAtTimestamp == 0)
        {
            _applyRetryStartedAtTimestamp = nowTimestamp;
        }
        if (_applyFailureCount < int.MaxValue)
        {
            _applyFailureCount++;
        }
    }

    private void ResetApplyRetryWindow()
    {
        _applyRetryStartedAtTimestamp = 0;
        _applyFailureCount = 0;
        _applyRetryExhausted = false;
    }

    private bool ApplyRetryExpired(long nowTimestamp)
    {
        if (_applyRetryExhausted ||
            _applyFailureCount >= MaximumApplyFailureCount)
        {
            return true;
        }
        if (_applyFailureCount < MinimumApplyFailureCountBeforeTimeout ||
            _applyRetryStartedAtTimestamp == 0)
        {
            return false;
        }
        return Math.Max(0, nowTimestamp - _applyRetryStartedAtTimestamp) >=
            MaximumApplyRetryTimestampTicks;
    }

    private void ExhaustApplyRetryWindow()
    {
        _applyRetryStartedAtTimestamp = 0;
        _applyRetryExhausted = true;
    }

    private void SetLayoutSnapshot(EdgeCapsuleLayoutSnapshot layout)
    {
        _layoutSnapshot = layout;
        var wallDeviceX = layout.Edge == EdgeCapsuleEdge.Left
            ? layout.Monitor.WorkArea.Left
            : layout.Monitor.WorkArea.Right;
        _nativeBatchGroup = new EdgeCapsuleNativeBatchGroup(
            layout.Monitor.DeviceName,
            layout.Edge,
            wallDeviceX);
    }

    private void CancelQueuedReconcile()
    {
        unchecked
        {
            _reconcileGeneration++;
        }
        _reconcileScheduled = false;

        var operation = _reconcileOperation;
        _reconcileOperation = null;
        var registration = _reconcileRegistration;
        _reconcileRegistration = null;

        if (operation is { Status: DispatcherOperationStatus.Pending })
        {
            _ = operation.Abort();
        }

        // If a callback is already executing, its captured registration remains the
        // exactly-once owner until its finally block. Pending/aborted/stale work is
        // superseded now so it cannot keep the shared Render barrier alive.
        if (operation is not { Status: DispatcherOperationStatus.Executing })
        {
            registration?.Complete();
        }
    }

    private void Configure(
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        if (_dispatcher != null && !ReferenceEquals(_dispatcher, dispatcher))
        {
            CancelQueuedReconcile();
            StopFrameScheduler();
            _frameScheduler = null;
            _layoutSnapshot = null;
            _nativeBatchGroup = default;
        }

        _dispatcher = dispatcher;
        _reconcile = reconcile;
    }

    private void QueueReconcile(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile,
        bool beforeNextRender)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);

        if (_dispatcher == null || _reconcile == null)
        {
            return;
        }

        if (_visualTransactionDeferrals > 0)
        {
            return;
        }

        if (_reconcileScheduled)
        {
            if (beforeNextRender &&
                _reconcileOperation is
                    { Status: DispatcherOperationStatus.Pending } pendingOperation &&
                pendingOperation.Priority != DispatcherPriority.Send)
            {
                // A real host input outranks an earlier passive Render invalidation. Promoting the
                // same operation preserves its render-batch registration; it drains at Send and
                // releases the shared render barrier before this composition pass.
                pendingOperation.Priority = DispatcherPriority.Send;
            }
            return;
        }

        var priority = beforeNextRender
            ? DispatcherPriority.Send
            : DispatcherPriority.Render;
        _reconcileScheduled = true;
        var generation = ++_reconcileGeneration;
        RenderReconcileRegistration? reconcileRegistration = null;
        if (!beforeNextRender)
        {
            _frameScheduler ??= EdgeCapsuleFrameScheduler.For(_dispatcher);
            reconcileRegistration =
                new RenderReconcileRegistration(_frameScheduler);
        }
        _reconcileRegistration = reconcileRegistration;

        DispatcherOperation? queuedOperation = null;
        queuedOperation = _dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    if (generation != _reconcileGeneration)
                    {
                        return;
                    }
                    _reconcileScheduled = false;
                    RunReconcile();
                }
                finally
                {
                    reconcileRegistration?.Complete();
                    if (ReferenceEquals(
                            _reconcileRegistration,
                            reconcileRegistration))
                    {
                        _reconcileRegistration = null;
                    }
                    if (ReferenceEquals(_reconcileOperation, queuedOperation))
                    {
                        _reconcileOperation = null;
                    }
                }
            }),
            priority);
        _reconcileOperation = queuedOperation;

        if (beforeNextRender)
        {
            // This is one physical host's input wake-up, not a cross-window layout batch. Send
            // runs after the current routed event and before Render; do not demote this wake-up
            // behind the render-priority sibling batch.
            return;
        }

        // Passive reconciles share Render priority with CompositionTarget.Rendering. The pending
        // count still keeps one shared frame behind every sibling already queued in this batch,
        // but the callbacks can no longer be starved by a higher-priority Rendering callback.
        // If input promotes one callback to Send, the same registration drains there.
    }

    private void RunReconcile(long? nowTimestamp = null, bool synchronousFlush = false)
    {
        if (_reconcile == null)
        {
            return;
        }
        if (_visualTransactionDeferrals > 0 && !synchronousFlush)
        {
            // Only the owner's explicit Flush may consume a staged queue generation. This also
            // covers shared frame callbacks, whose native batch alone is not that owner.
            return;
        }
        if (_nativeBatchRetryPending && !_nativeBatchApplyActive)
        {
            // A failed queue batch owns the pending logical generation. Host input and ordinary
            // Loaded work may add dirty flags, but only another coordinated native batch may
            // consume them or publish the retained queue-wide notification.
            StartFrameScheduler();
            return;
        }
        var reconcileTimestamp = nowTimestamp ?? Stopwatch.GetTimestamp();
        var dirty = _dirty;
        _dirty = EdgeCapsuleDirty.None;
        EdgeCapsuleDirty remaining;
        var previousTimestampOverride = _reconcileTimestampOverride;
        _reconcileTimestampOverride = reconcileTimestamp;
        try
        {
            remaining = _reconcile(dirty);
        }
        finally
        {
            _reconcileTimestampOverride = previousTimestampOverride;
        }
        var needsFrame = (remaining & EdgeCapsuleDirty.Frame) != 0;
        var needsApplyRetry = (remaining & EdgeCapsuleDirty.ApplyRetry) != 0;
        _dirty |= remaining & ~EdgeCapsuleDirty.Frame;
        var applyRetryExpired = needsApplyRetry &&
            ApplyRetryExpired(reconcileTimestamp);
        if (applyRetryExpired)
        {
            // Keep Presentation dirty for a later explicit/display invalidation, but stop the
            // bounded attempt/time retry so a permanently invalid HWND cannot spin the UI thread.
            _dirty &= ~EdgeCapsuleDirty.ApplyRetry;
            Transition = null;
            needsFrame = false;
            ExhaustApplyRetryWindow();
        }

        if (needsFrame ||
            (needsApplyRetry && !applyRetryExpired) ||
            _nativeBatchTransactionGroupId > 0)
        {
            StartFrameScheduler();
        }
        else if (!Transition.HasValue)
        {
            StopFrameScheduler();
        }

        if (!Transition.HasValue)
        {
            if (applyRetryExpired)
            {
                if (!_nativeBatchApplyActive)
                {
                    CompletePresentationSettle(success: false);
                }
            }
            else if (_presentationSettleCallback != null &&
                !needsApplyRetry &&
                (_dirty & PresentationWorkMask) == EdgeCapsuleDirty.None)
            {
                SchedulePresentationSettleCompletion();
            }
        }
    }

    private void CompletePresentationSettle(bool success)
    {
        var callback = _presentationSettleCallback;
        _presentationSettleCallback = null;
        callback?.Invoke(success);
    }

    private void SchedulePresentationSettleCompletion()
    {
        var callback = _presentationSettleCallback;
        var dispatcher = _dispatcher;
        if (callback == null || dispatcher == null)
        {
            return;
        }

        // Let WPF finish the destination monitor's Render/Loaded work before the floating cover is
        // released. Intervening dirty work blocks this candidate and the next pass reschedules it.
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(callback, _presentationSettleCallback) ||
                    Transition.HasValue ||
                    (_dirty & PresentationWorkMask) != EdgeCapsuleDirty.None)
                {
                    return;
                }
                // This only reports that Presenter work drained. Cross-HWND callers still verify
                // the native host with ConfirmPresentationSettled before releasing their cover.
                CompletePresentationSettle(success: true);
            }),
            DispatcherPriority.ContextIdle);
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

    internal bool TryDeferSharedFramePostCommit(Action callback) =>
        _advancingSharedFrameScheduler?.TryEnqueuePostCommit(callback) == true;

    internal bool NativeBatchApplyActive => _nativeBatchApplyActive;

    internal int NativeBatchCommitVersion => _nativeBatchCommitVersion;

    internal bool NativeBatchRetryPending => _nativeBatchRetryPending;

    internal long NativeBatchTransactionGroupId =>
        _nativeBatchTransactionGroupId;

    internal bool NativeBatchTransactionRetryExhausted =>
        _nativeBatchTransactionGroupId > 0 && _applyRetryExhausted;

    internal EdgeCapsuleNativeBatchGroup NativeBatchGroup =>
        _nativeBatchTransactionGroupId > 0
            ? new EdgeCapsuleNativeBatchGroup(
                "",
                default,
                0,
                TransactionGroupId: _nativeBatchTransactionGroupId)
            : !string.IsNullOrEmpty(_nativeBatchGroup.MonitorDeviceName)
                ? _nativeBatchGroup
                : new EdgeCapsuleNativeBatchGroup(
                    "",
                    AppliedPresentation.Edge,
                    AppliedPresentation.WallDeviceX,
                    RuntimeHelpers.GetHashCode(this));

    internal bool CanReleaseNativeBatchTransactionGroup(long groupId) =>
        groupId == _nativeBatchTransactionGroupId &&
        EdgeCapsuleNativeTransactionPolicy.CanRelease(
            groupId,
            Transition.HasValue,
            _nativeBatchRetryPending,
            _nativeBatchApplyActive,
            (_dirty & PresentationWorkMask) != EdgeCapsuleDirty.None);

    internal void ReleaseNativeBatchTransactionGroup(long groupId)
    {
        if (groupId != _nativeBatchTransactionGroupId)
        {
            return;
        }

        _nativeBatchTransactionGroupId = 0;
        if (!Transition.HasValue &&
            !_nativeBatchRetryPending &&
            (_dirty & PresentationWorkMask) == EdgeCapsuleDirty.None)
        {
            StopFrameScheduler();
        }
    }

    internal void AbortNativeBatchTransactionGroup(long groupId)
    {
        if (groupId != _nativeBatchTransactionGroupId)
        {
            return;
        }

        _nativeBatchTransactionGroupId = 0;
        _nativeBatchRetryPending = false;
        _dirty &= ~EdgeCapsuleDirty.ApplyRetry;
        Transition = null;
        ExhaustApplyRetryWindow();
        StopFrameScheduler();
        ScheduleNativeBatchPresentationSettleFailure();
    }

    internal EdgeCapsuleNativeBatchApplyStatus NativeBatchApplyStatus
    {
        get
        {
            if (!_nativeBatchApplyActive || !_nativeBatchApplySucceeded)
            {
                return EdgeCapsuleNativeBatchApplyStatus.Failed;
            }
            if (_nativeBatchApplyDeferred)
            {
                return EdgeCapsuleNativeBatchApplyStatus.Deferred;
            }
            return !_nativeBatchRetryPending ||
                _appliedForceApplyVersion == _forceApplyVersion
                    ? EdgeCapsuleNativeBatchApplyStatus.Ready
                    : EdgeCapsuleNativeBatchApplyStatus.Failed;
        }
    }

    internal void BeginNativeBatchApply()
    {
        Debug.Assert(!_nativeBatchApplyActive);
        _nativeBatchDeferredRecoveryGeneration++;
        _nativeBatchDeferredCallbackScheduled = false;
        _nativeBatchApplyActive = true;
        _nativeBatchApplySucceeded = true;
        _nativeBatchApplyAttempted = false;
        _nativeBatchApplyDeferred = false;
    }

    internal void CompleteNativeBatchApplySuccess()
    {
        if (!_nativeBatchApplyActive)
        {
            return;
        }

        var applyAttempted = _nativeBatchApplyAttempted;
        _nativeBatchApplyActive = false;
        _nativeBatchApplySucceeded = false;
        _nativeBatchApplyAttempted = false;
        _nativeBatchApplyDeferred = false;
        _nativeBatchRetryPending = false;
        if (applyAttempted)
        {
            unchecked
            {
                _nativeBatchCommitVersion++;
            }
        }
        ResetApplyRetryWindow();
    }

    internal void CompleteNativeBatchApplyFailure(long nowTimestamp)
    {
        if (!_nativeBatchApplyActive)
        {
            return;
        }

        var logicalApplySucceeded = _nativeBatchApplySucceeded;
        var applyAttempted = _nativeBatchApplyAttempted;
        var retryWasPending = _nativeBatchRetryPending;
        var deferred = _nativeBatchApplyDeferred;
        _nativeBatchApplyActive = false;
        _nativeBatchApplySucceeded = false;
        _nativeBatchApplyAttempted = false;
        _nativeBatchApplyDeferred = false;
        if (!EdgeCapsuleNativeTransactionPolicy.ParticipatesInBatchOutcome(
                _nativeBatchTransactionGroupId,
                applyAttempted,
                retryWasPending,
                deferred))
        {
            // This presenter shared a render tick but did not participate in the failed queue batch.
            // Do not hide it, charge its retry budget or create work it never requested.
            _nativeBatchRetryPending = false;
            return;
        }

        _nativeBatchRetryPending = true;
        _nativeBatchApplyRejectedCallback?.Invoke();
        // A presenter-level apply failure has already registered itself. If this presenter queued a
        // healthy HWND operation but the queue's EndDeferWindowPos failed, account for it here.
        if (logicalApplySucceeded &&
            (applyAttempted || _nativeBatchTransactionGroupId > 0))
        {
            RegisterApplyFailure(nowTimestamp);
        }
        _dirty |= EdgeCapsuleDirty.Presentation;
        unchecked
        {
            _forceApplyVersion++;
        }
        if (ApplyRetryExpired(nowTimestamp))
        {
            _dirty &= ~EdgeCapsuleDirty.ApplyRetry;
            _nativeBatchRetryPending = false;
            Transition = null;
            ExhaustApplyRetryWindow();
            if (_nativeBatchTransactionGroupId == 0)
            {
                StopFrameScheduler();
                ScheduleNativeBatchPresentationSettleFailure();
            }
            return;
        }
        _dirty |= EdgeCapsuleDirty.ApplyRetry;
        StartFrameScheduler();
    }

    internal void CompleteNativeBatchApplyDeferred()
    {
        if (!_nativeBatchApplyActive)
        {
            return;
        }

        var applyAttempted = _nativeBatchApplyAttempted;
        var retryWasPending = _nativeBatchRetryPending;
        var requestedRecovery = _nativeBatchApplyDeferred;
        _nativeBatchApplyActive = false;
        _nativeBatchApplySucceeded = false;
        _nativeBatchApplyAttempted = false;
        _nativeBatchApplyDeferred = false;
        if (!EdgeCapsuleNativeTransactionPolicy.ParticipatesInBatchOutcome(
                _nativeBatchTransactionGroupId,
                applyAttempted,
                retryWasPending,
                requestedRecovery))
        {
            _nativeBatchRetryPending = false;
            return;
        }

        _nativeBatchRetryPending = true;
        _nativeBatchApplyRejectedCallback?.Invoke();
        unchecked
        {
            _forceApplyVersion++;
        }
        _dirty |= EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.ApplyRetry;
        StartFrameScheduler();
        if (requestedRecovery)
        {
            ScheduleNativeBatchDeferredRecovery();
        }
    }

    private void ScheduleNativeBatchDeferredRecovery()
    {
        var callback = _nativeBatchApplyDeferredCallback;
        var dispatcher = _dispatcher;
        if (callback == null ||
            dispatcher == null ||
            dispatcher.HasShutdownStarted ||
            _nativeBatchDeferredCallbackScheduled)
        {
            return;
        }

        var recoveryGeneration = ++_nativeBatchDeferredRecoveryGeneration;
        _nativeBatchDeferredCallbackScheduled = true;
        dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (recoveryGeneration != _nativeBatchDeferredRecoveryGeneration)
                {
                    return;
                }
                _nativeBatchDeferredCallbackScheduled = false;
                if (_nativeBatchRetryPending)
                {
                    callback();
                }
            }),
            DispatcherPriority.Send);
    }

    private void ScheduleNativeBatchPresentationSettleFailure()
    {
        var callback = _presentationSettleCallback;
        _presentationSettleCallback = null;
        if (callback == null)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            callback(false);
            return;
        }

        // Do not invoke user/controller work while the scheduler is still completing sibling
        // presenters' batch handshakes. BeginInvoke runs only after the current atomic loop exits.
        dispatcher.BeginInvoke(
            (Action)(() => callback(false)),
            DispatcherPriority.Send);
    }

    internal bool AdvanceSharedFrame(
        EdgeCapsuleFrameScheduler scheduler,
        DeviceScreenPoint? pointer,
        long frameTimestamp)
    {
        var applyRetryPending = (_dirty & EdgeCapsuleDirty.ApplyRetry) != 0;
        var transactionGroupActive = _nativeBatchTransactionGroupId > 0;
        if (!UsesSharedFrameScheduler(scheduler) ||
            _dispatcher == null ||
            _reconcile == null ||
            (!Transition.HasValue &&
             !applyRetryPending &&
             !transactionGroupActive))
        {
            StopFrameScheduler();
            return false;
        }

        // Native/WPF pointer events and the controller's bounded intent timers wake pointer work.
        // The shared composition scheduler now runs only animation or coordinated apply retries,
        // rather than polling a stationary preview at the monitor refresh rate.
        if (Transition.HasValue)
        {
            _dirty |= EdgeCapsuleDirty.Frame;
        }
        _reconcileGeneration++;
        _reconcileScheduled = false;
        _hasFramePointerOverride = true;
        _framePointerOverride = pointer;
        _advancingSharedFrameScheduler = scheduler;
        BeginNativeBatchApply();
        try
        {
            RunReconcile(frameTimestamp);
        }
        finally
        {
            _advancingSharedFrameScheduler = null;
            _framePointerOverride = null;
            _hasFramePointerOverride = false;
        }
        return UsesSharedFrameScheduler(scheduler) &&
            (Transition.HasValue ||
                (_dirty & EdgeCapsuleDirty.ApplyRetry) != 0 ||
                _nativeBatchTransactionGroupId > 0);
    }
}
