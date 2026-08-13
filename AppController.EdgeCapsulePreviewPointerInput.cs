namespace PaperTodo;

public sealed partial class AppController
{
    internal bool HasDeepCapsuleFloatingDragCoverForQueue(
        PaperWindow inputWindow)
    {
        var queueKey = QueueKey(inputWindow.EdgeCapsulePreviewPaper);
        return _windows.Values.Any(candidate =>
            !candidate.IsClosed &&
            candidate.HasDeepCapsuleFloatingDragCover &&
            string.Equals(
                QueueKey(candidate.EdgeCapsulePreviewPaper),
                queueKey,
                StringComparison.Ordinal));
    }

    private void CancelPreviewActivationBehindFloatingDrag(
        PaperWindow inputWindow)
    {
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        unchecked
        {
            _edgeCapsulePreviewTransferGeneration++;
        }
        CancelEdgeCapsulePreviewActivationIntent(
            inputWindow.EdgeCapsulePreviewPaperId);
    }

    /// <summary>
    /// Physical pointer authority for edge-preview input. Host/native input may prove that the
    /// pointer is inside a real applied rectangle even while the Presenter's cosmetic hover bit is
    /// stale. The first card may therefore open from a verified physical hit; an existing session
    /// still uses the normal 50 ms / 2-DIP transfer contract.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewPhysicalPointer(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        if (IsExiting)
        {
            return;
        }

        // During floating/docking handoff the floating HWND is already the queue's visual cover.
        // A physical hit on a peer must not start a second preview proxy underneath that cover.
        // Invalidate a transfer already queued earlier in the same reconcile notification turn.
        if (HasDeepCapsuleFloatingDragCoverForQueue(inputWindow))
        {
            CancelPreviewActivationBehindFloatingDrag(inputWindow);
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null)
        {
            // During continuous browsing, the candidate spends the transfer-intent interval in its
            // compact hover shape. Prime that resize before the caller's Send reconcile so the real
            // HWND never exposes intermediate width frames.
            inputWindow.PrimeEdgeCapsulePointerComposition(pointer);

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

        ResetEdgeCapsulePreviewCorridorExitIntent();
        if (!pointer.HasValue)
        {
            inputWindow.PrimeEdgeCapsulePointerComposition(pointer);
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
            inputWindow.PrimeEdgeCapsulePointerComposition(pointer);
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
