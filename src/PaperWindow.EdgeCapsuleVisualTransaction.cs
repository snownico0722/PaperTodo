namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal IDisposable DeferEdgeCapsuleVisualTransactionReconcile() =>
        _edgeCapsule.DeferReconcileToVisualTransaction();

    private bool TryStageEdgeCapsuleVisualTransaction(
        bool animate,
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds,
        bool refreshLayout = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        var motion = animate
            ? EdgeCapsuleMotion.Animate(reason, durationMilliseconds)
            : EdgeCapsuleMotion.Snap(reason);
        return _controller.TryStageEdgeCapsuleVisualTransaction(
            this,
            motion,
            refreshLayout);
    }

    internal void JoinEdgeCapsuleNativeTransactionGroup(long groupId) =>
        _edgeCapsule.JoinNativeBatchTransactionGroup(groupId);

    internal EdgeCapsuleNativeBatchApplyStatus CommitEdgeCapsuleVisualTransaction(
        EdgeCapsuleMotion motion,
        bool refreshLayout,
        long transactionTimestamp,
        bool rebaseActiveTransition)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement))
        {
            return EdgeCapsuleNativeBatchApplyStatus.Ready;
        }

        _edgeCapsule.BeginNativeBatchApply();
        _edgeCapsule.RequestPresentation(
            motion,
            rebaseActiveTransition);
        var dirty = EdgeCapsuleDirty.Presentation;
        if (refreshLayout)
        {
            dirty |= EdgeCapsuleDirty.Measure;
        }

        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsuleVisualTransactionNotificationDeferred = true;
        try
        {
            _edgeCapsule.Flush(
                dirty,
                dispatcher,
                ReconcileEdgeCapsule,
                transactionTimestamp);
        }
        finally
        {
            _edgeCapsuleVisualTransactionNotificationDeferred = false;
        }
        return _edgeCapsule.NativeBatchApplyStatus;
    }

    internal void RebaseEdgeCapsuleQueueProxyAnimationClock(
        long transactionTimestamp)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        _edgeCapsule.RebaseActiveTransitionStart(
            transactionTimestamp);
    }

    internal void CompleteEdgeCapsuleVisualTransactionApply(
        bool success,
        bool deferred,
        long transactionTimestamp)
    {
        if (success)
        {
            _edgeCapsule.CompleteNativeBatchApplySuccess();
            return;
        }
        if (deferred)
        {
            _edgeCapsule.CompleteNativeBatchApplyDeferred();
            return;
        }

        // Re-enter through the shared frame scheduler. A temporary cross-queue transaction group
        // keeps its related queues in one native batch; ordinary queues still retry independently.
        _edgeCapsule.CompleteNativeBatchApplyFailure(transactionTimestamp);
    }
}
