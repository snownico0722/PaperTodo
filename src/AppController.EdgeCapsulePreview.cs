using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    // Two 60-Hz frames reject incidental pass-through without adding the visibly sluggish 50-ms
    // floor to every A-to-B switch. The negative-only predictor can still extend or veto motion.
    private const double EdgeCapsulePreviewTransferResidenceMilliseconds = 32;
    private const double EdgeCapsulePreviewPointerToleranceDip = 2;
    private const double EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds = 24;
    private const double EdgeCapsulePreviewActivationIntentTrackingMilliseconds = 16;
    private const double EdgeCapsulePreviewLayoutSuppressionMilliseconds =
        EdgeCapsuleLayout.SlotMoveMilliseconds + 50;
    private const int EdgeCapsulePreviewCursorReadRetryCount = 1;

    private readonly EdgeCapsuleHoverIntentPredictor
        _edgeCapsulePreviewIntentPredictor = new();
    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;
    private string? _edgeCapsulePreviewOutgoingPaperId;
    private string? _edgeCapsulePreviewQueuedTransferPaperId;
    private string? _edgeCapsulePreviewQueuedCloseOwnerPaperId;
    private EdgeCapsulePreviewCloseReason _edgeCapsulePreviewQueuedCloseReason;
    private EdgeCapsulePreviewActivationIntent?
        _edgeCapsulePreviewActivationIntent;
    private EdgeCapsulePreviewCorridorExitIntent?
        _edgeCapsulePreviewCorridorExitIntent;
    private DispatcherTimer? _edgeCapsulePreviewCorridorIntentTimer;
    private DispatcherTimer? _edgeCapsulePreviewActivationIntentTimer;
    private EdgeCapsulePreviewPointerAnchor?
        _edgeCapsulePreviewLayoutSuppressionAnchor;
    private int _edgeCapsulePreviewTransferGeneration;
    private int _edgeCapsulePreviewCloseGeneration;
    private int _edgeCapsulePreviewPointerResolutionVersion;
    private EdgeCapsulePreviewLayoutSession?
        _edgeCapsulePreviewLastResolvedSession;
    private DeviceScreenPoint? _edgeCapsulePreviewLastResolvedPointer;
    private int _edgeCapsulePreviewLastResolvedVersion = -1;

    private readonly record struct EdgeCapsulePreviewActivationIntent(
        string TargetPaperId,
        string? ExpectedOwnerPaperId,
        DeviceScreenPoint StableAnchor,
        long CandidateSinceTimestamp,
        long StableSinceTimestamp);

    private readonly record struct EdgeCapsulePreviewPointerAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY,
        string QueueKey,
        long CreatedAtTimestamp);

    private readonly record struct EdgeCapsulePreviewStableAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY);

    private readonly record struct EdgeCapsulePreviewCorridorExitIntent(
        string OwnerPaperId,
        long CorridorSinceTimestamp,
        long? NoTargetIntentSinceTimestamp,
        bool PausedForPointerCapture);

    private readonly record struct EdgeCapsulePreviewPointerResolution(
        PaperWindow? Target,
        bool OwnerContains,
        bool TransferRectangleContains);

    private EdgeCapsuleQueuePlan BuildCurrentEdgeCapsuleQueuePlan()
    {
        var papers = DeepCapsulePapersInOrder();
        return EdgeCapsuleQueueCoordinator.Build(
            papers.Select(paper =>
                new EdgeCapsuleQueueMember(paper, QueueKey(paper))),
            State.UseCapsuleCollapseAll);
    }

    private EdgeCapsuleQueuePlan ApplyEdgeCapsulePreviewLayout(
        EdgeCapsuleQueuePlan basePlan)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return basePlan;
        }

        if (!basePlan.Placements.ContainsKey(session.OwnerPaperId) ||
            IsCapsuleCollapseAllActiveForQueue(session.QueueKey) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=owner-missing-or-queue-unavailable queue={session.QueueKey}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var ownerSize = owner.CurrentEdgeCapsulePreviewSize;
        if (!owner.IsEdgeCapsulePreviewOpen ||
            !ownerSize.HasValue ||
            ownerSize.Value != session.Size ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=owner-state open={owner.IsEdgeCapsulePreviewOpen} " +
                $"size={(ownerSize.HasValue ? ownerSize.Value.ToString() : "<null>")} " +
                $"expectedSize={session.Size} eligibility={owner.EdgeCapsulePreviewEligibilityTrace()}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentQueueKey = QueueKey(owner.EdgeCapsulePreviewPaper);
        var currentQueue = basePlan.Queues.FirstOrDefault(queue =>
            string.Equals(
                queue.Key,
                currentQueueKey,
                StringComparison.Ordinal));
        if (currentQueue == null)
        {
            TraceEdgeCapsulePreview(
                $"layout reset owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"reason=current-queue-missing queue={currentQueueKey}");
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentIds = currentQueue.Papers
            .Select(paper => paper.Id)
            .ToArray();
        if (!string.Equals(
                session.QueueKey,
                currentQueueKey,
                StringComparison.Ordinal) ||
            !session.QueuePaperIds.SequenceEqual(
                currentIds,
                StringComparer.Ordinal))
        {
            TraceEdgeCapsulePreview(
                $"layout refresh owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"queue={session.QueueKey}->{currentQueueKey}");
            session = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                basePlan,
                null,
                currentQueueKey,
                session.OwnerPaperId,
                session.Size,
                PaperLayoutDefaults.CapsuleHeight,
                DeepCapsuleGap);
            if (session == null)
            {
                TraceEdgeCapsulePreview(
                    $"layout reset owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                    "reason=layout-refresh-failed");
                ResetEdgeCapsulePreviewWithoutArrange();
                return basePlan;
            }
            _edgeCapsulePreviewSession = session;
        }

        return EdgeCapsulePreviewLayoutCoordinator.Apply(basePlan, session);
    }

    private void ResetEdgeCapsulePreviewWithoutArrange()
    {
        var session = _edgeCapsulePreviewSession;
        TraceEdgeCapsulePreview(
            $"reset without arrange owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)}");
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        var closeGeneration = ++_edgeCapsulePreviewCloseGeneration;
        ReleaseTrackedOutgoingEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            closeGeneration != _edgeCapsulePreviewCloseGeneration ||
            _edgeCapsulePreviewSession != null)
        {
            return;
        }
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            owner.SetEdgeCapsulePreviewClosed(animate: false);
        }
    }

    internal void NotifyEdgeCapsulePointerOverChanged(
        PaperWindow window,
        bool pointerOver)
    {
        if (IsExiting)
        {
            return;
        }

        if (!pointerOver)
        {
            CancelEdgeCapsulePreviewActivationIntent(
                window.EdgeCapsulePreviewPaperId);
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"enter blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()} " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null &&
            string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer) ||
            !window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer);
        if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
        {
            // A WPF enter caused only by the moving queue has no activation authority. Real
            // screen-space pointer motion clears this suppression before the intent gate starts.
            TraceEdgeCapsulePreview(
                $"enter suppressed target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)} pointer={pointer.X},{pointer.Y}");
            return;
        }

        AdvanceEdgeCapsulePreviewActivationIntent(
            session,
            window,
            pointer);
    }

    internal void NotifyEdgeCapsulePreviewPointerSample(
        PaperWindow window,
        DeviceScreenPoint? pointer)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (!pointer.HasValue)
            {
                return;
            }

            ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
                pointer.Value);
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
                return;
            }

            // This also recovers an initial enter whose first dispatcher turn became stale. A real
            // first hit has no dwell; the queued callback still revalidates the physical target.
            if (window.IsEdgeCapsulePointerOver &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer.Value))
            {
                AdvanceEdgeCapsulePreviewActivationIntent(
                    null,
                    window,
                    pointer.Value);
            }
            else
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
            }
            return;
        }

        if (!string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"owner sample blocked owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()}");
            CancelEdgeCapsulePreviewActivationIntent();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
            return;
        }
        if (!pointer.HasValue)
        {
            if (window.EdgeCapsulePreviewPointerCaptureActive)
            {
                HoldEdgeCapsulePreviewForPointerCapture(window, session);
            }
            else
            {
                TrackEdgeCapsulePreviewUnavailablePointer(window, session);
            }
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (window.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(window, session);
            return;
        }
        ResumeEdgeCapsulePreviewAfterPointerCapture();

        if (CanReuseEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value))
        {
            return;
        }

        ObserveEdgeCapsulePreviewPointer(window, pointer.Value);

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.OwnerContains)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(
                    resolution.Target))
            {
                CancelEdgeCapsulePreviewActivationIntent();
                RememberEdgeCapsulePreviewPointerResolution(
                    session,
                    pointer.Value);
                return;
            }

            AdvanceEdgeCapsulePreviewActivationIntent(
                session,
                resolution.Target,
                pointer.Value);
            return;
        }

        CancelEdgeCapsulePreviewActivationIntent();
        var exitPolicy = EdgeCapsulePreviewExitPolicy.Resolve(
            resolution.TransferRectangleContains,
            State.ExperimentalEdgeCapsuleHoverIntent);
        if (exitPolicy != EdgeCapsulePreviewExitPolicyDecision.ImmediateClose)
        {
            var queuedCloseForOwner = string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal);
            if (queuedCloseForOwner &&
                _edgeCapsulePreviewQueuedCloseReason ==
                    EdgeCapsulePreviewCloseReason.OutsideTransferRectangle)
            {
                // Once the physical pointer has crossed the hard edge, returning only to empty
                // space cannot revoke that event. A real owner/target (handled above) still can.
                RememberEdgeCapsulePreviewPointerResolution(
                    session,
                    pointer.Value);
                return;
            }

            var decision = AdvanceEdgeCapsulePreviewCorridorExitIntent(
                window,
                session,
                pointer.Value);
            if (queuedCloseForOwner)
            {
                // A completed fixed wait is final. In predictive mode, only newly confirmed motion
                // toward a real capsule may revoke a queued no-target close; another blank/no-intent
                // sample must not restart the sensitivity clock.
                if (EdgeCapsulePreviewExitPolicy.EmptyRegionCanCancelQueuedClose(
                        _edgeCapsulePreviewQueuedCloseReason,
                        State.ExperimentalEdgeCapsuleHoverIntent,
                        decision == EdgeCapsuleCorridorExitDecision.KeepAlive))
                {
                    CancelQueuedEdgeCapsulePreviewClose();
                }
                else
                {
                    ResetEdgeCapsulePreviewCorridorExitIntent();
                }
            }
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        // The transfer rectangle's outer edge is absolute. Prediction only owns its interior.
        ResetEdgeCapsulePreviewCorridorExitIntent();
        RememberEdgeCapsulePreviewPointerResolution(
            session,
            pointer.Value);
        QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
    }

    internal bool CloseEdgeCapsulePreviewForDrag(PaperWindow draggedWindow)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return false;
        }

        var draggedWindowWasOwner = string.Equals(
            session.OwnerPaperId,
            draggedWindow.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);
        CloseEdgeCapsulePreview(animate: true, arrange: true);
        return draggedWindowWasOwner;
    }

    internal void CloseEdgeCapsulePreviewForClose(PaperWindow window)
    {
        if (IsEdgeCapsulePreviewOwner(window))
        {
            CloseEdgeCapsulePreview(animate: false, arrange: false);
        }
    }

    internal void CloseEdgeCapsulePreviewForBodySessionReset(PaperWindow window)
    {
        if (IsEdgeCapsulePreviewOwner(window))
        {
            // A protocol 1.8 preview can own controls from the body session that is about to be
            // replaced. End the controller session and restore queue placement before that tree is
            // disposed, otherwise the edge host can retain a dead native/Web mini view.
            CloseEdgeCapsulePreview(animate: false, arrange: true);
        }

        // Also repair a locally staged request if the controller session was already removed (for
        // example during shutdown). Apply the compact state synchronously enough to precede the
        // next render, then sever the final visual-tree reference while the session is still alive.
        window.ResetEdgeCapsulePreviewForBodySessionChange();
    }

    internal bool IsEdgeCapsulePreviewOwner(PaperWindow window) =>
        _edgeCapsulePreviewSession is { } session &&
        string.Equals(
            session.OwnerPaperId,
            window.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);

    private void QueueEdgeCapsulePreviewClose(
        PaperWindow window,
        string ownerPaperId,
        EdgeCapsulePreviewCloseReason reason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle)
    {
        if (string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewQueuedCloseReason =
                EdgeCapsulePreviewExitPolicy.StrongerCloseReason(
                    _edgeCapsulePreviewQueuedCloseReason,
                    reason);
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = ownerPaperId;
        _edgeCapsulePreviewQueuedCloseReason = reason;
        var generation = ++_edgeCapsulePreviewCloseGeneration;
        ProcessQueuedEdgeCapsulePreviewClose(
            window,
            ownerPaperId,
            generation,
            EdgeCapsulePreviewCursorReadRetryCount);
    }

    private void ProcessQueuedEdgeCapsulePreviewClose(
        PaperWindow window,
        string ownerPaperId,
        int generation,
        int cursorReadRetriesRemaining)
    {
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _edgeCapsulePreviewCloseGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (IsExiting ||
                    _edgeCapsulePreviewSession is not { } session ||
                    !string.Equals(
                        session.OwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(ownerPaperId, out var owner) ||
                    !ReferenceEquals(owner, window))
                {
                    ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                    return;
                }
                if (owner.CanEnterEdgeCapsulePreview &&
                    owner.EdgeCapsulePreviewPointerCaptureActive)
                {
                    ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                    HoldEdgeCapsulePreviewForPointerCapture(owner, session);
                    return;
                }
                if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    // A transient cursor-read failure must not silently consume the only close
                    // requested by this input turn. Retry once; a confirmed hard boundary or
                    // elapsed no-target deadline remains close authority if reading still fails.
                    ForgetEdgeCapsulePreviewPointerResolution();
                    if (cursorReadRetriesRemaining > 0)
                    {
                        ProcessQueuedEdgeCapsulePreviewClose(
                            window,
                            ownerPaperId,
                            generation,
                            cursorReadRetriesRemaining - 1);
                    }
                    else
                    {
                        var fallbackReason =
                            _edgeCapsulePreviewQueuedCloseReason;
                        ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                        TraceEdgeCapsulePreview(
                            $"close queued owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
                            $"reason={fallbackReason} pointer=<unavailable>");
                        CloseEdgeCapsulePreview(animate: true, arrange: true);
                    }
                    return;
                }

                var queuedReason = _edgeCapsulePreviewQueuedCloseReason;
                ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);

                var resolution = ResolveEdgeCapsulePreviewPointer(
                    session,
                    pointer);
                if (owner.CanEnterEdgeCapsulePreview &&
                    resolution.OwnerContains)
                {
                    return;
                }
                if (owner.CanEnterEdgeCapsulePreview &&
                    resolution.Target != null)
                {
                    // A real target discovered during close revalidation is transfer authority,
                    // not merely a reason to swallow the close. Route it through the same owner
                    // arbiter so the normal 32 ms target-residence rule still applies.
                    ForgetEdgeCapsulePreviewPointerResolution();
                    NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
                    return;
                }
                TraceEdgeCapsulePreview(
                    $"close queued owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
                    $"reason={queuedReason} pointer={pointer.X},{pointer.Y}");
                CloseEdgeCapsulePreview(animate: true, arrange: true);
            }),
            DispatcherPriority.Input);
    }

    private void ConsumeQueuedEdgeCapsulePreviewClose(string ownerPaperId)
    {
        if (!string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
    }

    private void CancelQueuedEdgeCapsulePreviewClose()
    {
        if (_edgeCapsulePreviewQueuedCloseOwnerPaperId == null)
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        _edgeCapsulePreviewCloseGeneration++;
    }

    private void QueueEdgeCapsulePreviewTransfer(
        PaperWindow window,
        string? expectedOwnerPaperId = null,
        EdgeCapsulePreviewStableAnchor? stableAnchor = null)
    {
        var paperId = window.EdgeCapsulePreviewPaperId;
        if (!AllowsEdgeCapsuleQueueProxyOwnership(
                QueueKey(window.EdgeCapsulePreviewPaper)))
        {
            CancelEdgeCapsulePreviewActivationIntent();
            TraceEdgeCapsulePreview(
                $"transfer blocked target={EdgeCapsulePreviewTraceId(paperId)} " +
                "reason=drag-authority");
            return;
        }
        if (string.Equals(
                _edgeCapsulePreviewQueuedTransferPaperId,
                paperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedTransferPaperId = paperId;
        var generation = ++_edgeCapsulePreviewTransferGeneration;
        TraceEdgeCapsulePreview(
            $"transfer queued target={EdgeCapsulePreviewTraceId(paperId)} " +
            $"expectedOwner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} generation={generation}");
        ProcessQueuedEdgeCapsulePreviewTransfer(
            window,
            paperId,
            expectedOwnerPaperId,
            stableAnchor,
            generation,
            EdgeCapsulePreviewCursorReadRetryCount);
    }

    private void ProcessQueuedEdgeCapsulePreviewTransfer(
        PaperWindow window,
        string paperId,
        string? expectedOwnerPaperId,
        EdgeCapsulePreviewStableAnchor? stableAnchor,
        int generation,
        int cursorReadRetriesRemaining)
    {
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsExiting ||
                    generation != _edgeCapsulePreviewTransferGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedTransferPaperId,
                        paperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (!_windows.TryGetValue(paperId, out var current) ||
                    !ReferenceEquals(current, window))
                {
                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} reason=window-changed");
                    return;
                }
                if (!AllowsEdgeCapsuleQueueProxyOwnership(
                        QueueKey(current.EdgeCapsulePreviewPaper)))
                {
                    CancelEdgeCapsulePreviewActivationIntent();
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        "reason=drag-authority");
                    return;
                }
                if (!current.CanEnterEdgeCapsulePreview)
                {
                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=blocked eligibility={current.EdgeCapsulePreviewEligibilityTrace()}");
                    return;
                }
                if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    if (cursorReadRetriesRemaining > 0)
                    {
                        ProcessQueuedEdgeCapsulePreviewTransfer(
                            window,
                            paperId,
                            expectedOwnerPaperId,
                            stableAnchor,
                            generation,
                            cursorReadRetriesRemaining - 1);
                        return;
                    }

                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} reason=no-pointer");
                    return;
                }
                _edgeCapsulePreviewQueuedTransferPaperId = null;
                if (!current.IsEdgeCapsuleInteractiveAt(pointer))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=pointer-outside pointer={pointer.X},{pointer.Y}");
                    return;
                }
                if (stableAnchor.HasValue &&
                    EdgeCapsulePreviewPointerMovedBeyondTolerance(
                        stableAnchor.Value.Point,
                        pointer,
                        stableAnchor.Value.DpiScaleX,
                        stableAnchor.Value.DpiScaleY))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=anchor-moved anchor={stableAnchor.Value.Point.X},{stableAnchor.Value.Point.Y} " +
                        $"pointer={pointer.X},{pointer.Y}");
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (expectedOwnerPaperId == null)
                {
                    // An initial-profile request is valid only for the first preview in a session.
                    // If another request already opened one, its owner sampler starts a transfer.
                    if (session != null)
                    {
                        TraceEdgeCapsulePreview(
                            $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                            $"reason=session-already-open owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)}");
                        return;
                    }
                }
                else if (session == null ||
                    !string.Equals(
                        session.OwnerPaperId,
                        expectedOwnerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(
                        expectedOwnerPaperId,
                        out var owner))
                {
                    TraceEdgeCapsulePreview(
                        $"transfer dropped target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"reason=owner-changed expected={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} " +
                        $"actual={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)}");
                    return;
                }
                else if (owner.EdgeCapsulePreviewPointerCaptureActive)
                {
                    TraceEdgeCapsulePreview(
                        $"transfer paused target={EdgeCapsulePreviewTraceId(paperId)} " +
                        $"owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} reason=pointer-capture");
                    HoldEdgeCapsulePreviewForPointerCapture(owner, session);
                    return;
                }

                OpenOrTransferEdgeCapsulePreview(current, pointer);
            }),
            // Opening after a real hit must be committed before the next composition pass. The
            // callback still runs outside the current routed event and keeps every stale-work guard.
            DispatcherPriority.Send);
    }

    private void OpenOrTransferEdgeCapsulePreview(
        PaperWindow window,
        DeviceScreenPoint pointer)
    {
        if (IsExiting)
        {
            return;
        }
        if (!window.CanEnterEdgeCapsulePreview)
        {
            TraceEdgeCapsulePreview(
                $"open blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"eligibility={window.EdgeCapsulePreviewEligibilityTrace()}");
            return;
        }
        if (!window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            TraceEdgeCapsulePreview(
                $"open blocked target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"reason=pointer-outside pointer={pointer.X},{pointer.Y}");
            return;
        }

        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        _edgeCapsulePreviewCloseGeneration++;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        var previous = _edgeCapsulePreviewSession;
        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            // A transfer may begin before the owner from the transfer before it has completed its
            // 200 ms retraction. Keep at most the current owner plus one outgoing preview tree.
            ReleaseTrackedOutgoingEdgeCapsulePreview();
        }
        var request = window.PrepareEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            !EdgeCapsulePreviewSessionsHaveSameOwner(
                previous,
                _edgeCapsulePreviewSession))
        {
            TraceEdgeCapsulePreview(
                $"open superseded during prepare target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"current={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }
        // Same-generation layout refreshes may replace the immutable record while preserving the
        // owner. Treat that refreshed record as the source session for the rest of this CAS.
        previous = _edgeCapsulePreviewSession;
        if (request == null)
        {
            TraceEdgeCapsulePreview(
                $"open aborted target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                "reason=prepare-null");
            return;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            previous,
            queueKey,
            window.EdgeCapsulePreviewPaperId,
            request.Size,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (next == null)
        {
            TraceEdgeCapsulePreview(
                $"open aborted target={EdgeCapsulePreviewTraceId(window.EdgeCapsulePreviewPaperId)} " +
                $"reason=layout-null queue={queueKey}");
            return;
        }
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            !EdgeCapsulePreviewSessionsHaveSameOwner(
                previous,
                _edgeCapsulePreviewSession))
        {
            TraceEdgeCapsulePreview(
                $"open superseded before mount target={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
                $"current={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }
        previous = _edgeCapsulePreviewSession;

        // Commit the controller owner before the target view is mounted. StagePreviewContent and
        // WPF layout are allowed to re-enter input/layout code; every re-entrant observer must see
        // the same owner that the target model is about to expose. If opening is rejected, roll
        // the controller session back before returning.
        _edgeCapsulePreviewSession = next;
        TraceEdgeCapsulePreview(
            $"session switch prepare from={EdgeCapsulePreviewTraceId(previous?.OwnerPaperId)} " +
            $"to={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} queue={next.QueueKey}");
        if (!window.SetEdgeCapsulePreviewOpen(
                request,
                animate: true))
        {
            if (transferGeneration == _edgeCapsulePreviewTransferGeneration &&
                string.Equals(
                    _edgeCapsulePreviewSession?.OwnerPaperId,
                    next.OwnerPaperId,
                    StringComparison.Ordinal))
            {
                _edgeCapsulePreviewSession = previous;
                // Stage/Prepare may have re-entered layout while the temporary owner was visible.
                // Restore the source plan immediately so queued placement work cannot paint the
                // rejected target generation.
                ArrangeDeepCapsules(animate: false);
            }
            TraceEdgeCapsulePreview(
                $"session switch rollback target={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
                $"restore={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                "reason=model-rejected-or-reentered");
            return;
        }
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } currentSession ||
            !string.Equals(
                currentSession.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            TraceEdgeCapsulePreview(
                $"session switch superseded target={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
                $"current={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }
        // A re-entrant layout refresh may rebuild the immutable session record without superseding
        // this transfer. Continue with that same-generation, same-owner plan.
        next = currentSession;

        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            if (_windows.TryGetValue(previous.OwnerPaperId, out var oldOwner))
            {
                oldOwner.SetEdgeCapsulePreviewClosed(animate: true);
                if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                    _edgeCapsulePreviewSession is not { } sessionAfterClose ||
                    !string.Equals(
                        sessionAfterClose.OwnerPaperId,
                        next.OwnerPaperId,
                        StringComparison.Ordinal))
                {
                    return;
                }
                if (!oldOwner.IsEdgeCapsulePreviewOpen)
                {
                    _edgeCapsulePreviewOutgoingPaperId = previous.OwnerPaperId;
                }
            }
        }

        EnforceEdgeCapsulePreviewContentLimit(transferGeneration);
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } committedSession ||
            !string.Equals(
                committedSession.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }
        next = committedSession;

        ResetEdgeCapsulePreviewCorridorExitIntent();
        RecordEdgeCapsulePreviewTransferPointer(window, next.QueueKey);
        ArrangeDeepCapsules(animate: true);
        var displaced = string.Join(
            ",",
            next.TopOffsetsDip
                .Where(pair => Math.Abs(pair.Value) > 0.01)
                .Select(pair => $"{EdgeCapsulePreviewTraceId(pair.Key)}:{pair.Value:F1}"));
        TraceEdgeCapsulePreview(
            $"session switch committed owner={EdgeCapsulePreviewTraceId(next.OwnerPaperId)} " +
            $"displaced={(string.IsNullOrEmpty(displaced) ? "<none>" : displaced)}");
    }

    private static bool EdgeCapsulePreviewSessionsHaveSameOwner(
        EdgeCapsulePreviewLayoutSession? first,
        EdgeCapsulePreviewLayoutSession? second) =>
        first == null
            ? second == null
            : second != null && string.Equals(
                first.OwnerPaperId,
                second.OwnerPaperId,
                StringComparison.Ordinal);

    private void EnforceEdgeCapsulePreviewContentLimit(int transferGeneration)
    {
        foreach (var window in _windows.Values.ToArray())
        {
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration)
            {
                return;
            }

            var currentPaperId = _edgeCapsulePreviewSession?.OwnerPaperId;
            var outgoingPaperId = _edgeCapsulePreviewOutgoingPaperId;
            var paperId = window.EdgeCapsulePreviewPaperId;
            if (string.Equals(paperId, currentPaperId, StringComparison.Ordinal) ||
                string.Equals(paperId, outgoingPaperId, StringComparison.Ordinal) ||
                (!window.IsEdgeCapsulePreviewOpen &&
                 !window.HasEdgeCapsulePreviewContent))
            {
                continue;
            }

            // Re-entrant provider/layout work can supersede an outer A→B transfer with C before A
            // is recorded as outgoing. Repair that stale third owner immediately; the invariant is
            // current plus one outgoing mounted tree, regardless of callback nesting.
            if (window.IsEdgeCapsulePreviewOpen)
            {
                window.SetEdgeCapsulePreviewClosed(animate: false);
            }
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                string.Equals(
                    paperId,
                    _edgeCapsulePreviewSession?.OwnerPaperId,
                    StringComparison.Ordinal) ||
                string.Equals(
                    paperId,
                    _edgeCapsulePreviewOutgoingPaperId,
                    StringComparison.Ordinal))
            {
                return;
            }
            window.ClearEdgeCapsulePreviewContent();
        }
    }

    private void CloseEdgeCapsulePreview(bool animate, bool arrange)
    {
        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        var closeGeneration = ++_edgeCapsulePreviewCloseGeneration;
        var session = _edgeCapsulePreviewSession;
        TraceEdgeCapsulePreview(
            $"close owner={EdgeCapsulePreviewTraceId(session?.OwnerPaperId)} " +
            $"animate={animate} arrange={arrange}");
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        ReleaseTrackedOutgoingEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            closeGeneration != _edgeCapsulePreviewCloseGeneration ||
            _edgeCapsulePreviewSession != null)
        {
            return;
        }
        PaperWindow? owner = null;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var currentOwner))
        {
            owner = currentOwner;
            currentOwner.SetEdgeCapsulePreviewClosed(animate);
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                closeGeneration != _edgeCapsulePreviewCloseGeneration ||
                _edgeCapsulePreviewSession != null)
            {
                return;
            }
            if (!currentOwner.IsEdgeCapsulePreviewOpen)
            {
                _edgeCapsulePreviewOutgoingPaperId = session.OwnerPaperId;
            }
        }

        if (arrange && session != null && owner != null)
        {
            // Compacting the source queue must not manufacture a new hover under a stationary
            // pointer. The queue key scopes this suppression so a capsule already reached in a
            // different queue can still become an initial candidate immediately.
            RecordEdgeCapsulePreviewTransferPointer(owner, session.QueueKey);
        }
        else
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
        }

        if (arrange)
        {
            ArrangeDeepCapsules(animate);
        }
    }

    private void ReleaseTrackedOutgoingEdgeCapsulePreview()
    {
        var outgoingPaperId = _edgeCapsulePreviewOutgoingPaperId;
        _edgeCapsulePreviewOutgoingPaperId = null;
        if (outgoingPaperId == null ||
            string.Equals(
                outgoingPaperId,
                _edgeCapsulePreviewSession?.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(outgoingPaperId, out var outgoing))
        {
            return;
        }

        // The outgoing model was closed when it stopped owning the session. Releasing its old
        // preview tree must not snap that window ahead of the still-animated queue generation;
        // the compact shell can finish the existing transition without retaining heavy content.
        outgoing.ClearEdgeCapsulePreviewContent();
    }

    private void RecordEdgeCapsulePreviewTransferPointer(
        PaperWindow target,
        string queueKey)
    {
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (target.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
        }
        else if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                pointer,
                out var monitor))
        {
            dpiScaleX = monitor.DpiScaleX;
            dpiScaleY = monitor.DpiScaleY;
        }
        else
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _edgeCapsulePreviewLayoutSuppressionAnchor =
            new EdgeCapsulePreviewPointerAnchor(
                pointer,
                dpiScaleX,
                dpiScaleY,
                queueKey,
                now);
        _edgeCapsulePreviewIntentPredictor.Reset(
            pointer,
            now,
            dpiScaleX,
            dpiScaleY);
    }

    private void ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
        DeviceScreenPoint pointer)
    {
        if (!_edgeCapsulePreviewLayoutSuppressionAnchor.HasValue)
        {
            return;
        }

        var anchor = _edgeCapsulePreviewLayoutSuppressionAnchor.Value;
        if (IsEdgeCapsulePreviewLayoutSuppressionExpired(anchor) ||
            EdgeCapsulePreviewPointerMovedBeyondTolerance(
                anchor.Point,
                pointer,
                anchor.DpiScaleX,
                anchor.DpiScaleY))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            ForgetEdgeCapsulePreviewPointerResolution();
        }
    }

    private bool IsEdgeCapsulePreviewLayoutSuppressedFor(
        PaperWindow target)
    {
        if (_edgeCapsulePreviewLayoutSuppressionAnchor is not { } anchor)
        {
            return false;
        }
        if (IsEdgeCapsulePreviewLayoutSuppressionExpired(anchor))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            ForgetEdgeCapsulePreviewPointerResolution();
            return false;
        }

        return
            string.Equals(
                anchor.QueueKey,
                QueueKey(target.EdgeCapsulePreviewPaper),
                StringComparison.Ordinal);
    }

    private static bool IsEdgeCapsulePreviewLayoutSuppressedFor(
        EdgeCapsulePreviewPointerAnchor anchor,
        string queueKey) =>
        string.Equals(anchor.QueueKey, queueKey, StringComparison.Ordinal);

    private static bool IsEdgeCapsulePreviewLayoutSuppressionExpired(
        EdgeCapsulePreviewPointerAnchor anchor) =>
        Stopwatch.GetElapsedTime(
            anchor.CreatedAtTimestamp,
            Stopwatch.GetTimestamp()).TotalMilliseconds >=
        EdgeCapsulePreviewLayoutSuppressionMilliseconds;

    private void AdvanceEdgeCapsulePreviewActivationIntent(
        EdgeCapsulePreviewLayoutSession? session,
        PaperWindow target,
        DeviceScreenPoint pointer)
    {
        var targetPaperId = target.EdgeCapsulePreviewPaperId;
        if (!target.TryGetEdgeCapsuleInteractiveGeometry(
                out var targetGeometry))
        {
            CancelEdgeCapsulePreviewActivationIntent(targetPaperId);
            return;
        }

        var expectedOwnerPaperId = session?.OwnerPaperId;
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        if (session == null)
        {
            if (predictiveIntentEnabled)
            {
                _edgeCapsulePreviewIntentPredictor.Observe(
                    pointer,
                    now,
                    targetGeometry.DpiScaleX,
                    targetGeometry.DpiScaleY);
            }
            ResetEdgeCapsulePreviewActivationIntent();
            QueueEdgeCapsulePreviewTransfer(target);
            return;
        }

        var intent = _edgeCapsulePreviewActivationIntent;
        if (!intent.HasValue ||
            !string.Equals(
                intent.Value.TargetPaperId,
                targetPaperId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.Value.ExpectedOwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewActivationIntent =
                new EdgeCapsulePreviewActivationIntent(
                    targetPaperId,
                    expectedOwnerPaperId,
                    pointer,
                    now,
                    now);
            TraceEdgeCapsulePreview(
                $"intent candidate target=" +
                $"{EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"owner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} " +
                $"pointer={pointer.X},{pointer.Y}");
            ScheduleEdgeCapsulePreviewActivationIntentCheck(
                target,
                EdgeCapsulePreviewTransferResidenceMilliseconds);
            return;
        }

        var currentIntent = intent.Value;
        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
                currentIntent.StableAnchor,
                pointer,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY))
        {
            currentIntent = currentIntent with
            {
                StableAnchor = pointer,
                StableSinceTimestamp = now
            };
        }

        _edgeCapsulePreviewActivationIntent = currentIntent;
        var candidateElapsed = Stopwatch.GetElapsedTime(
            currentIntent.CandidateSinceTimestamp,
            now).TotalMilliseconds;
        var stableElapsed = Stopwatch.GetElapsedTime(
            currentIntent.StableSinceTimestamp,
            now).TotalMilliseconds;
        if (candidateElapsed <
            EdgeCapsulePreviewTransferResidenceMilliseconds)
        {
            ScheduleEdgeCapsulePreviewActivationIntentCheck(
                target,
                EdgeCapsulePreviewTransferResidenceMilliseconds -
                candidateElapsed);
            return;
        }

        if (predictiveIntentEnabled)
        {
            var decision =
                _edgeCapsulePreviewIntentPredictor.Evaluate(
                    EdgeCapsuleHoverIntentMode.Transfer,
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                    targetGeometry.Bounds,
                    pointer,
                    candidateElapsed,
                    stableElapsed);
            if (decision !=
                EdgeCapsuleHoverIntentDecision.NoExtraDelay)
            {
                ScheduleEdgeCapsulePreviewActivationIntentCheck(
                    target,
                    EdgeCapsulePreviewActivationIntentTrackingMilliseconds);
                return;
            }
        }

        ResetEdgeCapsulePreviewActivationIntent();
        TraceEdgeCapsulePreview(
            $"intent accepted target=" +
            $"{EdgeCapsulePreviewTraceId(targetPaperId)} " +
            $"owner={EdgeCapsulePreviewTraceId(expectedOwnerPaperId)} " +
            $"candidateMs={candidateElapsed:F1} " +
            $"stableMs={stableElapsed:F1}");
        QueueEdgeCapsulePreviewTransfer(
            target,
            expectedOwnerPaperId,
            new EdgeCapsulePreviewStableAnchor(
                currentIntent.StableAnchor,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY));
    }

    internal void InvalidateEdgeCapsulePreviewPointerResolution()
    {
        unchecked
        {
            _edgeCapsulePreviewPointerResolutionVersion++;
        }
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    private bool CanReuseEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer) =>
        // Reuse only an unchanged, settled result between real input, intent deadlines and
        // presentation invalidations. Pending intent always runs the full physical queue resolver.
        _edgeCapsulePreviewActivationIntent == null &&
        _edgeCapsulePreviewQueuedTransferPaperId == null &&
        ReferenceEquals(
            _edgeCapsulePreviewLastResolvedSession,
            session) &&
        _edgeCapsulePreviewLastResolvedPointer == pointer &&
        _edgeCapsulePreviewLastResolvedVersion ==
            _edgeCapsulePreviewPointerResolutionVersion;

    private void RememberEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        _edgeCapsulePreviewLastResolvedSession = session;
        _edgeCapsulePreviewLastResolvedPointer = pointer;
        _edgeCapsulePreviewLastResolvedVersion =
            _edgeCapsulePreviewPointerResolutionVersion;
    }

    private void ForgetEdgeCapsulePreviewPointerResolution()
    {
        _edgeCapsulePreviewLastResolvedSession = null;
        _edgeCapsulePreviewLastResolvedPointer = null;
        _edgeCapsulePreviewLastResolvedVersion = -1;
    }

    private void ObserveEdgeCapsulePreviewPointer(
        PaperWindow owner,
        DeviceScreenPoint pointer)
    {
        if (!State.ExperimentalEdgeCapsuleHoverIntent)
        {
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                pointer,
                out var monitor))
        {
            dpiScaleX = monitor.DpiScaleX;
            dpiScaleY = monitor.DpiScaleY;
        }
        else if (owner.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
        }
        else
        {
            return;
        }

        _edgeCapsulePreviewIntentPredictor.Observe(
            pointer,
            Stopwatch.GetTimestamp(),
            dpiScaleX,
            dpiScaleY);
    }

    private EdgeCapsuleCorridorExitDecision AdvanceEdgeCapsulePreviewCorridorExitIntent(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var intent = _edgeCapsulePreviewCorridorExitIntent;
        var current = !intent.HasValue ||
            !string.Equals(
                intent.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal)
            ? new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false)
            : intent.Value;

        var corridorElapsed = Stopwatch.GetElapsedTime(
            current.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var noTargetIntentElapsed = current.NoTargetIntentSinceTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(
                current.NoTargetIntentSinceTimestamp.Value,
                now).TotalMilliseconds
            : 0;

        EdgeCapsuleCorridorExitDecision decision;
        if (!predictiveIntentEnabled)
        {
            decision = corridorElapsed >=
                EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds
                ? EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent
                : EdgeCapsuleCorridorExitDecision.KeepAlive;
        }
        else
        {
            Span<DeviceScreenRect> keepAliveBounds =
                session.QueuePaperIds.Count <= 32
                    ? stackalloc DeviceScreenRect[session.QueuePaperIds.Count]
                    : new DeviceScreenRect[session.QueuePaperIds.Count];
            var keepAliveCount = 0;
            foreach (var paperId in session.QueuePaperIds)
            {
                if (!_windows.TryGetValue(paperId, out var candidate) ||
                    !candidate.CanEnterEdgeCapsulePreview ||
                    !candidate.TryGetEdgeCapsuleInteractiveGeometry(
                        out var geometry))
                {
                    continue;
                }

                keepAliveBounds[keepAliveCount++] = geometry.Bounds;
            }

            decision = _edgeCapsulePreviewIntentPredictor
                .EvaluateCorridorExit(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                    keepAliveBounds.Slice(0, keepAliveCount),
                    pointer,
                    noTargetIntentElapsed);
        }
        switch (decision)
        {
            case EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent:
                current = current with
                {
                    NoTargetIntentSinceTimestamp =
                        current.NoTargetIntentSinceTimestamp ?? now
                };
                break;
            case EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent:
                TraceEdgeCapsulePreview(
                    $"corridor close owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
                    $"reason={decision} pointer={pointer.X},{pointer.Y} " +
                    $"corridorMs={corridorElapsed:F1} " +
                    $"noTargetMs={noTargetIntentElapsed:F1}");
                QueueEdgeCapsulePreviewClose(
                    owner,
                    session.OwnerPaperId,
                    EdgeCapsulePreviewCloseReason.NoTargetIntent);
                ResetEdgeCapsulePreviewCorridorExitIntent();
                return decision;
            default:
                current = current with
                {
                    NoTargetIntentSinceTimestamp = null
                };
                break;
        }

        _edgeCapsulePreviewCorridorExitIntent = current;
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(owner, now);
        return decision;
    }

    private void ScheduleEdgeCapsulePreviewCorridorIntentCheck(
        PaperWindow owner,
        long now,
        bool retryAfterCursorReadFailure = false)
    {
        if (_edgeCapsulePreviewCorridorExitIntent is not { } intent)
        {
            return;
        }

        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var closeMilliseconds = predictiveIntentEnabled
            ? _edgeCapsulePreviewIntentPredictor
                .CorridorNoTargetIntentCloseMilliseconds(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
            : EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds;
        var corridorElapsed = Stopwatch.GetElapsedTime(
            intent.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var closeElapsed = predictiveIntentEnabled
            ? intent.NoTargetIntentSinceTimestamp.HasValue
                ? Stopwatch.GetElapsedTime(
                    intent.NoTargetIntentSinceTimestamp.Value,
                    now).TotalMilliseconds
                : 0
            : corridorElapsed;
        var remaining = Math.Max(1, closeMilliseconds - closeElapsed);
        // Empty corridor pixels are intentionally HTTRANSPARENT, so they do not keep producing WPF
        // mouse moves. Keep one owner-local sampler active only while the pointer is in that empty
        // rectangle, so crossing its hard boundary is observed promptly in either settings mode.
        var nextCheck = retryAfterCursorReadFailure
            ? EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds
            : Math.Min(
                EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds,
                remaining);
        if (_edgeCapsulePreviewCorridorIntentTimer == null)
        {
            _edgeCapsulePreviewCorridorIntentTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                owner.Dispatcher);
            _edgeCapsulePreviewCorridorIntentTimer.Tick +=
                OnEdgeCapsulePreviewCorridorIntentTimerTick;
        }

        _edgeCapsulePreviewCorridorIntentTimer.Stop();
        _edgeCapsulePreviewCorridorIntentTimer.Interval =
            TimeSpan.FromMilliseconds(nextCheck);
        _edgeCapsulePreviewCorridorIntentTimer.Start();
    }

    private void OnEdgeCapsulePreviewCorridorIntentTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewCorridorExitIntent is not { } intent ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                intent.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            return;
        }

        if (!owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(owner, session.OwnerPaperId);
            return;
        }
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(owner, session);
            return;
        }
        if (intent.PausedForPointerCapture)
        {
            intent = new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                Stopwatch.GetTimestamp(),
                null,
                false);
            _edgeCapsulePreviewCorridorExitIntent = intent;
            _edgeCapsulePreviewIntentPredictor.Reset();
        }
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // Empty pixels are HTTRANSPARENT, so losing this one sample must not silently discard
            // the only active boundary/deadline watcher. Without a fresh trajectory, prediction no
            // longer has positive keep-alive evidence; start/continue the normal no-target clock.
            var now = Stopwatch.GetTimestamp();
            if (!intent.PausedForPointerCapture)
            {
                var predictiveIntentEnabled =
                    State.ExperimentalEdgeCapsuleHoverIntent;
                var current = intent;
                if (predictiveIntentEnabled &&
                    !current.NoTargetIntentSinceTimestamp.HasValue)
                {
                    current = current with
                    {
                        NoTargetIntentSinceTimestamp = now
                    };
                    _edgeCapsulePreviewCorridorExitIntent = current;
                }

                var closeMilliseconds = predictiveIntentEnabled
                    ? _edgeCapsulePreviewIntentPredictor
                        .CorridorNoTargetIntentCloseMilliseconds(
                            State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
                    : EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds;
                var closeSince = predictiveIntentEnabled
                    ? current.NoTargetIntentSinceTimestamp ?? now
                    : current.CorridorSinceTimestamp;
                if (Stopwatch.GetElapsedTime(
                        closeSince,
                        now).TotalMilliseconds >= closeMilliseconds)
                {
                    ResetEdgeCapsulePreviewCorridorExitIntent();
                    QueueEdgeCapsulePreviewClose(
                        owner,
                        session.OwnerPaperId,
                        EdgeCapsulePreviewCloseReason.NoTargetIntent);
                    return;
                }
            }

            // Keep probing so a recovered cursor read can still recognize a real owner/target or a
            // hard-boundary crossing before the current deadline expires.
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            return;
        }

        // Re-enter the one owner arbiter. This is important when the timer itself discovers a real
        // target: that hit must start the normal 32 ms transfer rather than merely cancel closing.
        ForgetEdgeCapsulePreviewPointerResolution();
        NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
    }

    private void HoldEdgeCapsulePreviewForPointerCapture(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session)
    {
        var now = Stopwatch.GetTimestamp();
        ForgetEdgeCapsulePreviewPointerResolution();
        CancelEdgeCapsulePreviewActivationIntent();
        CancelQueuedEdgeCapsulePreviewClose();
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                true);
        // Capture pauses both close clocks, but the existing empty-region watcher keeps polling.
        // Once capture ends it re-enters the owner arbiter and starts a fresh blank/outside decision.
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
    }

    private void TrackEdgeCapsulePreviewUnavailablePointer(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session)
    {
        var now = Stopwatch.GetTimestamp();
        ForgetEdgeCapsulePreviewPointerResolution();
        CancelEdgeCapsulePreviewActivationIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
        var intent = _edgeCapsulePreviewCorridorExitIntent;
        var current = !intent.HasValue ||
            intent.Value.PausedForPointerCapture ||
            !string.Equals(
                intent.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal)
            ? new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false)
            : intent.Value;
        if (State.ExperimentalEdgeCapsuleHoverIntent &&
            !current.NoTargetIntentSinceTimestamp.HasValue)
        {
            current = current with
            {
                NoTargetIntentSinceTimestamp = now
            };
        }
        _edgeCapsulePreviewCorridorExitIntent = current;
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
    }

    private void ResumeEdgeCapsulePreviewAfterPointerCapture()
    {
        if (_edgeCapsulePreviewCorridorExitIntent is not
            { PausedForPointerCapture: true })
        {
            return;
        }

        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
    }

    private void ResetEdgeCapsulePreviewCorridorExitIntent()
    {
        _edgeCapsulePreviewCorridorExitIntent = null;
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
    }

    private void CancelEdgeCapsulePreviewActivationIntent(
        string? targetPaperId = null)
    {
        var intent = _edgeCapsulePreviewActivationIntent;
        if (intent.HasValue &&
            (targetPaperId == null ||
             string.Equals(
                 intent.Value.TargetPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            ResetEdgeCapsulePreviewActivationIntent();
        }

        if (_edgeCapsulePreviewQueuedTransferPaperId != null &&
            (targetPaperId == null ||
             string.Equals(
                 _edgeCapsulePreviewQueuedTransferPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            _edgeCapsulePreviewQueuedTransferPaperId = null;
            _edgeCapsulePreviewTransferGeneration++;
        }
    }

    private void ResetEdgeCapsulePreviewActivationIntent()
    {
        _edgeCapsulePreviewActivationIntent = null;
        _edgeCapsulePreviewActivationIntentTimer?.Stop();
    }

    private void ScheduleEdgeCapsulePreviewActivationIntentCheck(
        PaperWindow target,
        double delayMilliseconds)
    {
        if (_edgeCapsulePreviewActivationIntentTimer == null)
        {
            _edgeCapsulePreviewActivationIntentTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                target.Dispatcher);
            _edgeCapsulePreviewActivationIntentTimer.Tick +=
                OnEdgeCapsulePreviewActivationIntentTimerTick;
        }

        _edgeCapsulePreviewActivationIntentTimer.Stop();
        _edgeCapsulePreviewActivationIntentTimer.Interval =
            TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds));
        _edgeCapsulePreviewActivationIntentTimer.Start();
    }

    private void OnEdgeCapsulePreviewActivationIntentTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewActivationIntentTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewActivationIntent is not { } intent ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                intent.ExpectedOwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewActivationIntent();
            return;
        }
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(owner, session);
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // Do not leave a second retry loop alive after the target sample disappears. The shared
            // pointer-recovery watcher will retry, recognize a real target again with a fresh 32 ms
            // residence period, or close through the normal blank-region deadline.
            TrackEdgeCapsulePreviewUnavailablePointer(owner, session);
            return;
        }

        // Reuse the owner path so the deadline still performs the complete physical queue hit test,
        // stable-anchor validation and predictor veto before it can enqueue a transfer.
        ForgetEdgeCapsulePreviewPointerResolution();
        NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
    }

    private EdgeCapsulePreviewPointerResolution ResolveEdgeCapsulePreviewPointer(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        PaperWindow? target = null;
        var ownerContains = false;
        Span<EdgeCapsulePreviewCorridorNode> corridorNodes =
            session.QueuePaperIds.Count <= 32
                ? stackalloc EdgeCapsulePreviewCorridorNode[
                    session.QueuePaperIds.Count]
                : new EdgeCapsulePreviewCorridorNode[
                    session.QueuePaperIds.Count];
        var corridorCount = 0;
        var previousCorridorNodeValid = false;

        foreach (var paperId in session.QueuePaperIds)
        {
            if (!_windows.TryGetValue(paperId, out var window))
            {
                previousCorridorNodeValid = false;
                continue;
            }

            if (window.CanEnterEdgeCapsulePreview &&
                window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry))
            {
                corridorNodes[corridorCount++] =
                    new EdgeCapsulePreviewCorridorNode(
                        geometry.Bounds,
                        previousCorridorNodeValid);
                previousCorridorNodeValid = true;
            }
            else
            {
                previousCorridorNodeValid = false;
            }

            var isOwner = string.Equals(
                paperId,
                session.OwnerPaperId,
                StringComparison.Ordinal);
            if (isOwner)
            {
                ownerContains = window.IsEdgeCapsuleInteractiveAt(pointer);
            }
            else if (target == null &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer))
            {
                target = window;
            }
        }

        if (target == null)
        {
            // A physical hit in another monitor/edge queue is still a transfer from the current
            // session. Let the old owner arbitrate it so the target keeps the same 32 ms residence
            // contract instead of closing and reopening as a new first card.
            foreach (var paper in State.Papers)
            {
                if (string.Equals(
                        QueueKey(paper),
                        session.QueueKey,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(paper.Id, out var candidate) ||
                    !candidate.CanEnterEdgeCapsulePreview ||
                    !candidate.IsEdgeCapsuleInteractiveAt(pointer))
                {
                    continue;
                }

                target = candidate;
                break;
            }
        }

        if (corridorCount == 0)
        {
            return new EdgeCapsulePreviewPointerResolution(
                target,
                ownerContains,
                false);
        }

        var corridorContains = EdgeCapsulePreviewCorridor.Contains(
            corridorNodes[..corridorCount],
            pointer);
        return new EdgeCapsulePreviewPointerResolution(
            target,
            ownerContains,
            corridorContains);
    }

    private static bool EdgeCapsulePreviewPointerMovedBeyondTolerance(
        DeviceScreenPoint anchor,
        DeviceScreenPoint pointer,
        double dpiScaleX,
        double dpiScaleY)
    {
        var deltaX = (pointer.X - anchor.X) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleX);
        var deltaY = (pointer.Y - anchor.Y) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleY);
        var tolerance = EdgeCapsulePreviewPointerToleranceDip;
        return deltaX * deltaX + deltaY * deltaY >
            tolerance * tolerance;
    }

    private static double NormalizeEdgeCapsulePreviewDpiScale(double scale) =>
        double.IsFinite(scale) ? Math.Max(1, scale) : 1;

    private void RefreshEdgeCapsuleHoverIntentRuntime()
    {
        CancelEdgeCapsulePreviewActivationIntent();
        CancelQueuedEdgeCapsulePreviewClose();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewIntentPredictor.Reset();
        foreach (var window in _windows.Values)
        {
            window.RefreshEdgeCapsuleHoverIntentSettings();
        }
    }
}
