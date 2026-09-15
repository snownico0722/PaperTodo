using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private static WindowNative.WindowCloakBatchResult PublishSettledInputCover(
        IReadOnlyCollection<WindowNative.WindowCloakChange> revealChanges,
        Func<bool> publish,
        Action rollback)
    {
        var revealed = WindowNative.TrySetWindowCloakedBatchDetailed(revealChanges);
        if (revealed != WindowNative.WindowCloakBatchResult.Success) return revealed;
#if DEBUG
        EdgeCapsuleColdStartDiagnostics.Boundary("settled-input-source-visible");
#endif
        try
        {
            if (publish()) return WindowNative.WindowCloakBatchResult.Success;
        }
        catch
        {
            // The outgoing real source remains visible while its previous cover is restored.
        }
        try
        {
            rollback();
            // Only recovery needs another fence. Never cloak the outgoing source again until
            // its previous root is confirmed visible, even when the failed publish committed.
            if (!WindowNative.TryFlushDesktopComposition())
                return WindowNative.WindowCloakBatchResult.RollbackFailed;
            var restore = revealChanges.Select(change => new WindowNative.WindowCloakChange(
                change.Handle, change.RollbackCloaked, change.Cloaked)).ToArray();
            return WindowNative.TrySetWindowCloakedBatchDetailed(restore) ==
                WindowNative.WindowCloakBatchResult.Success
                ? WindowNative.WindowCloakBatchResult.RolledBack
                : WindowNative.WindowCloakBatchResult.RollbackFailed;
        }
        catch
        {
            // The caller's existing cover-loss path restores every real source. Do not return
            // to a predecessor whose visual rollback or re-cloak could not be verified.
            return WindowNative.WindowCloakBatchResult.RollbackFailed;
        }
    }

    private bool CanCreateSettledInputSuccessor(EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members) =>
        IsRetainedForQueueBrowsing && _successorHeld && !_completionPendingDuringSuccessorHold &&
        !plan.IsStaticPreacquisition && plan.DurationMilliseconds == 0 &&
        members.Count == _members.Count - 1 &&
        members.Select(member => member.SourceHandle).Distinct().Count() == members.Count &&
        members.All(member => member.Plan.Start == member.Plan.Source && member.Plan.Source == member.Plan.Target &&
            !EdgeCapsuleQueueProxyPolicy.RequiresTranslation(member.Plan.Start, member.Plan.Target) &&
            _members.Any(previous => ReferenceEquals(previous.Window, member.Window) &&
                previous.SourceHandle == member.SourceHandle &&
                _cloakedRealSourceHandles.Contains(member.SourceHandle) &&
                HasCompatibleEndpoint(member.Window, member.Plan.Target)));

    internal bool TryReleaseSettledInput(PaperWindow inputWindow,
        out EdgeCapsuleQueueCompositionProxy? successor,
        Func<EdgeCapsuleQueueCompositionProxy, EdgeCapsuleQueueCompositionProxy?, bool> coverReady,
        Action<EdgeCapsuleQueueCompositionProxy, EdgeCapsuleQueueCompositionProxy?> coverRollback,
        Action<EdgeCapsuleQueueCompositionProxy, bool, bool> completed)
    {
        successor = null;
        if (_members.Count < 2 || !CanRoutePointerInput || !IsRetainedForQueueBrowsing ||
            !_members.Any(member => ReferenceEquals(member.Window, inputWindow)) ||
            !_members.All(member => member.Window.IsEdgeCapsuleQueueProxyInputSettled) ||
            !TryReserveForSuccessor()) return false;

        var started = Stopwatch.GetTimestamp();
        EdgeCapsuleQueueCompositionProxy? staged = null;
        var transferred = false;
        try
        {
            // No model change, native move, animation cancellation, or alternative shape state.
            // Prepare the already-settled WPF endpoints together, then use the existing outgoing
            // source transaction to reveal only the input window while peers keep their cloak.
            // Startup validation must not become a long-lived strong reference chain after the
            // successor publishes. The predecessor and endpoint windows are guaranteed alive by
            // the active source set during startup, so weak guards preserve the same validation
            // while allowing retired generations and released windows to be collected afterwards.
            var predecessorReference = new WeakReference<EdgeCapsuleQueueCompositionProxy>(this);
            var endpoints = new List<(WeakReference<PaperWindow> Window, EdgeCapsulePresentationFrame Frame)>();
            var peers = new List<EdgeCapsuleQueueCompositionProxyMember>();
            foreach (var member in _members)
            {
                if (!member.Window.TryPrepareSettledQueueProxyInput(out var frame) ||
                    !HasCompatibleEndpoint(member.Window, frame)) return false;
                endpoints.Add((new WeakReference<PaperWindow>(member.Window), frame));
                if (!ReferenceEquals(member.Window, inputWindow))
                    peers.Add(new(member.Window, new(member.Plan.PaperId, frame, frame, frame), member.SourceHandle));
            }
            _members[0].Window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
            bool StillValid()
            {
                if (!predecessorReference.TryGetTarget(out var predecessor) ||
                    predecessor._disposed || predecessor._coverLost ||
                    predecessor._completionPendingDuringSuccessorHold || !predecessor._successorHeld)
                {
                    return false;
                }
                return endpoints.All(item =>
                    item.Window.TryGetTarget(out var window) &&
                    window.VerifySettledQueueProxyInput(item.Frame));
            }
            if (!StillValid()) return false;
            var plan = _plan with { Members = peers.Select(member => member.Plan).ToArray(),
                DurationMilliseconds = 0,
                IsStaticPreacquisition = false, IsSettledInputHandoff = true };
            staged = TryCreate(ReserveSessionOrdinal(), plan, peers, this,
                _ => StillValid(), _ => true, _interactionRequested, _environmentChanged,
                coverReady, coverRollback, completed);
            if (staged == null) return false;
            staged.SettledInputRequested = SettledInputRequested;
            if (!staged.TryStart(out _)) return false;
            transferred = staged.CoverPublished;
            if (!transferred) return false;
            successor = staged;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace($"proxy.input phase=member-released session={_sessionOrdinal} successor={staged.SessionOrdinal} queue={QueueKey} remaining={peers.Count} totalMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
#endif
            return true;
        }
        catch (Exception error)
        {
            Trace.TraceError("Settled input handoff failed: {0}", error);
            return false;
        }
        finally
        {
            if (!transferred)
            {
                // A published generation cannot roll back to a retired predecessor. Both failure
                // cases use the same safe recovery as an ordinary successor startup.
                if (staged?.CoverPublished == true) staged.CompleteNow(success: false);
                else staged?.AbortStaged();
                CompleteAfterFailedSuccessor(success: false);
            }
        }
    }
}
