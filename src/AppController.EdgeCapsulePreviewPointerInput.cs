using System.Diagnostics;

namespace PaperTodo;

public sealed partial class AppController
{
    // Boundary confirmation is transport-level wake state only. The existing corridor intent and
    // its single owner-local timer remain the only preview-exit timing authority.
    private string? _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
    private int _edgeCapsulePreviewBoundaryLeaveTransferGeneration;

    private bool AllowsEdgeCapsuleQueueProxyOwnership(string queueKey) =>
        !_windows.Values.Any(candidate =>
            !candidate.IsClosed &&
            !candidate.AllowsDeepCapsuleQueueProxyOwnership &&
            string.Equals(
                QueueKey(candidate.EdgeCapsulePreviewPaper),
                queueKey,
                StringComparison.Ordinal));

    /// <summary>
    /// Physical pointer authority for edge-preview input. Host/native input may prove that the
    /// pointer is inside a real applied rectangle even while the Presenter's cosmetic hover bit is
    /// stale. A normal first card may open immediately from that hit. A just-finished drag and an
    /// existing session use the shared target-residence / stability gate.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewPhysicalPointer(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        if (IsExiting)
        {
            return;
        }

        // Once reorder starts, the drag gesture owns this queue's visible transition. Invalidate
        // work queued before that gesture change and never start a preview underneath it.
        if (!AllowsEdgeCapsuleQueueProxyOwnership(
                QueueKey(inputWindow.EdgeCapsulePreviewPaper)))
        {
            CancelEdgeCapsulePreviewActivationIntent();
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null)
        {
            // WM_MOUSELEAVE confirmation reuses the existing corridor watcher. Passive WPF and
            // reconcile samples that still resolve to the owner are wake noise, not fresh native
            // re-entry, so they must not cancel that watcher. An outside-owner sample retires the
            // confirmation marker here and then continues through the canonical owner arbiter.
            if (PreserveEdgeCapsulePreviewHostBoundaryLeave(
                    session,
                    pointer))
            {
                return;
            }

            // Physical host input is only the wake-up authority. Once a preview session exists,
            // the owner remains the single queue-wide arbiter for owner/target/corridor/outside
            // resolution, transfer timing and close timing. Do not recreate that state machine in
            // this input adapter.
            if (_windows.TryGetValue(session.OwnerPaperId, out var owner))
            {
                NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
            }
            return;
        }

        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        if (!pointer.HasValue)
        {
            return;
        }

        var point = pointer.Value;
        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(point);
        if (!inputWindow.CanEnterEdgeCapsulePreview ||
            !inputWindow.IsEdgeCapsuleInteractiveAt(point) ||
            IsEdgeCapsulePreviewLayoutSuppressedFor(inputWindow))
        {
            // With no preview transaction available, compact hover is the visible interaction and
            // therefore needs compositor ownership itself.
            CancelEdgeCapsulePreviewActivationIntent(
                inputWindow.EdgeCapsulePreviewPaperId);
            return;
        }

        // A normal first hit can open immediately; the activation gate handles the short post-drag
        // exception. Neither path needs a redundant Resting→Hovered proxy before the preview proxy.
        if (!inputWindow.IsEdgeCapsulePointerOver)
        {
            TraceEdgeCapsulePreview(
                $"physical hit recovery target={EdgeCapsulePreviewTraceId(inputWindow.EdgeCapsulePreviewPaperId)} " +
                $"pointer={point.X},{point.Y}");
        }

        AdvanceEdgeCapsulePreviewActivationIntent(
            null,
            inputWindow,
            point);
    }

    /// <summary>
    /// A verified native hit/move inside committed InteractiveBounds is fresh physical activity and
    /// may revoke a pending WM_MOUSELEAVE confirmation. Routed WPF enter/leave and reconcile samples
    /// never call this path.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostPointerActivity()
    {
        var ownerPaperId = _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (ownerPaperId == null)
        {
            return;
        }

        TraceEdgeCapsulePreview(
            $"boundary confirm cancelled owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
            "reason=verified-host-activity");
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    /// <summary>
    /// WM_MOUSELEAVE can be delivered while the cursor still resolves inside the last committed
    /// InteractiveBounds. The following pixels may already be HTTRANSPARENT, so no later HWND move
    /// is guaranteed. Seed the existing corridor watcher only for that ambiguity and hand every
    /// actual owner/target/corridor/outside decision back to the canonical owner arbiter.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostBoundaryLeave(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        NotifyEdgeCapsulePreviewPhysicalPointer(inputWindow, pointer);

        if (IsExiting ||
            !pointer.HasValue ||
            _edgeCapsulePreviewSession is not { } session ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (!resolution.OwnerContains)
        {
            // The canonical arbiter already started transfer/corridor/hard-close work.
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId =
            session.OwnerPaperId;
        _edgeCapsulePreviewBoundaryLeaveTransferGeneration =
            _edgeCapsulePreviewTransferGeneration;
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false);
        ForgetEdgeCapsulePreviewPointerResolution();
        TraceEdgeCapsulePreview(
            $"boundary confirm armed owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.Value.X},{pointer.Value.Y}");
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
        EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
    }

    /// <summary>
    /// Passive WPF/reconcile samples are allowed to resolve a boundary confirmation, but while they
    /// still report the owner they must not cancel the already-armed native leave watcher. Verified
    /// native activity cancels earlier through NotifyEdgeCapsulePreviewHostPointerActivity.
    /// </summary>
    private bool PreserveEdgeCapsulePreviewHostBoundaryLeave(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint? pointer)
    {
        var expectedOwnerPaperId =
            _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (expectedOwnerPaperId == null)
        {
            return false;
        }

        if (_edgeCapsulePreviewBoundaryLeaveTransferGeneration !=
                _edgeCapsulePreviewTransferGeneration ||
            !string.Equals(
                session.OwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal))
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return false;
        }

        if (!pointer.HasValue)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            return true;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.OwnerContains)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false);
        ForgetEdgeCapsulePreviewPointerResolution();
        TraceEdgeCapsulePreview(
            $"boundary confirm resolved owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.Value.X},{pointer.Value.Y} " +
            $"target={EdgeCapsulePreviewTraceId(resolution.Target?.EdgeCapsulePreviewPaperId)} " +
            $"transferContains={resolution.TransferRectangleContains} source=sample");
        return false;
    }

    private void EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation()
    {
        var timer = _edgeCapsulePreviewCorridorIntentTimer;
        if (timer == null)
        {
            return;
        }

        // The normal corridor handler was attached when the timer was created. Re-attach this
        // continuation last so it can restore the same watcher only if the normal owner arbiter saw
        // a still-inside sample or a transient cursor-read failure.
        timer.Tick -= OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick;
        timer.Tick += OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick;
    }

    private void OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick(
        object? sender,
        EventArgs e)
    {
        var expectedOwnerPaperId =
            _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (expectedOwnerPaperId == null)
        {
            return;
        }

        if (IsExiting ||
            _edgeCapsulePreviewBoundaryLeaveTransferGeneration !=
                _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                session.OwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }

        // The normal corridor handler has already converted capture into its established paused
        // state. Boundary confirmation no longer owns anything once capture is active.
        if (!owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // The normal corridor handler may have started its no-target clock from this failed
            // sample. A boundary confirmation has no positive exit evidence yet, so revoke any
            // close it queued, restart the shared watcher, and wait for a readable physical point.
            CancelQueuedEdgeCapsulePreviewClose();
            var now = Stopwatch.GetTimestamp();
            _edgeCapsulePreviewCorridorExitIntent =
                new EdgeCapsulePreviewCorridorExitIntent(
                    session.OwnerPaperId,
                    now,
                    null,
                    false);
            ForgetEdgeCapsulePreviewPointerResolution();
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer);
        if (resolution.OwnerContains)
        {
            // The normal handler just treated this as an ordinary owner sample and therefore reset
            // the corridor intent. Restore the same single watcher without starting a second timer.
            CancelQueuedEdgeCapsulePreviewClose();
            var now = Stopwatch.GetTimestamp();
            _edgeCapsulePreviewCorridorExitIntent =
                new EdgeCapsulePreviewCorridorExitIntent(
                    session.OwnerPaperId,
                    now,
                    null,
                    false);
            ForgetEdgeCapsulePreviewPointerResolution();
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
            return;
        }

        // The normal handler already routed this readable outside-owner sample through the canonical
        // owner arbiter. Only retire the transport-level confirmation marker; preserve any corridor
        // or transfer intent that arbiter just established.
        TraceEdgeCapsulePreview(
            $"boundary confirm resolved owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.X},{pointer.Y} " +
            $"target={EdgeCapsulePreviewTraceId(resolution.Target?.EdgeCapsulePreviewPaperId)} " +
            $"transferContains={resolution.TransferRectangleContains} source=timer");
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
    }

    private void ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation()
    {
        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId = null;
    }

    internal void SuppressEdgeCapsulePreviewForMasterQueueLayout(
        string monitorDeviceName,
        EdgeCapsuleEdge edge) =>
        SuppressEdgeCapsulePreviewForMasterQueueLayout(MasterCapsuleQueueKey(monitorDeviceName, edge));

    internal void SuppressEdgeCapsulePreviewForMasterQueueLayout(string queueKey)
    {
        if (IsExiting)
        {
            return;
        }

        // Master collapse/expand moves real capsule HWNDs under a stationary pointer. Cancel any
        // activation work that was based on the pre-toggle layout before arming the same
        // layout-induced-hover suppression used by preview compaction.
        ResetEdgeCapsulePreviewActivationIntent();
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewTransferGeneration++;
        ForgetEdgeCapsulePreviewPointerResolution();

        var target = _windows.Values.FirstOrDefault(window =>
            string.Equals(
                QueueKey(window.EdgeCapsulePreviewPaper),
                queueKey,
                StringComparison.Ordinal));
        if (target == null)
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        RecordEdgeCapsulePreviewTransferPointer(target, queueKey);

        // Collapse-all is one queue-wide visual operation, just like Preview transfer. Open the
        // controller-owned transaction before ToggleCapsuleCollapseAllActive mutates the queue and
        // synchronously calls ArrangeDeepCapsules. Every RetractIntoMaster/ApplyDeepCapsulePlacement
        // in that arrange can then stage into the same Send-priority commit and start from one QPC
        // timestamp instead of seven independent per-paper clocks.
        foreach (var window in _windows.Values.Where(w =>
                     QueueKey(w.EdgeCapsulePreviewPaper) == queueKey).ToArray())
            BeginEdgeCapsuleVisualTransaction(window);
    }
}
