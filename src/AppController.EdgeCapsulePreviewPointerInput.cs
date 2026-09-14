namespace PaperTodo;

public sealed partial class AppController
{
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
    /// stale. The first card may therefore open from a verified physical hit; an existing session
    /// still uses the normal 32 ms target-residence / 2-DIP stability contract.
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

        // The first eligible card opens immediately from this verified physical hit. Do not insert
        // a redundant Resting→Hovered proxy immediately before the larger preview proxy; that would
        // add startup work and a visual phase the historical first-hit contract never had.
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
}
