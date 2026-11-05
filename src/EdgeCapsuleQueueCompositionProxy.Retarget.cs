using System.Diagnostics;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    // true means this generation consumed the update, including a covered failure.
    // A different source/shape still uses the established successor handoff.
    internal bool TryRetarget(EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members,
        Func<long, bool> commitEndpoints, Func<long, bool> rebaseClock,
        IntPtr insertAfter = default)
    {
        if (!_successorHeld || _disposed || _coverLost || _sourcesReleased ||
            members.Count != _members.Count ||
            !EdgeCapsuleQueueProxyPolicy.CanRetarget(_plan, plan, _outputBounds)) return false;
        var bySource = members.ToDictionary(member => member.SourceHandle);
        if (_visuals.Any(state => !bySource.TryGetValue(state.Member.SourceHandle, out var next) ||
                !ReferenceEquals(next.Window, state.Member.Window))) return false;

        try
        {
            if (insertAfter != IntPtr.Zero && !_window.PlaceBehind(insertAfter))
                throw new InvalidOperationException("Queue translation could not stay behind its interaction surface.");
            // Keep the old clock running while logical/native endpoints settle beneath
            // the same live cover. Sample after that work, just before the new commit.
            if (!commitEndpoints(Stopwatch.GetTimestamp()))
                throw new InvalidOperationException("Queue retarget endpoints did not settle.");
            var timestamp = Stopwatch.GetTimestamp();
            var starts = _visuals.Select(state =>
                EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(
                    SamplePresentation(state, timestamp))).ToArray();
            for (var index = 0; index < _visuals.Count; index++)
            {
                var state = _visuals[index];
                var next = bySource[state.Member.SourceHandle];
                state.Member = next;
                state.StartOffsetX = starts[index].Left - _outputBounds.Left;
                state.StartOffsetY = starts[index].Top - _outputBounds.Top;
                state.TargetOffsetX = next.Plan.Target.HostBounds.Left - _outputBounds.Left;
                state.TargetOffsetY = next.Plan.Target.HostBounds.Top - _outputBounds.Top;
            }
            _plan = plan;
            _members = members;
            if (!rebaseClock(timestamp))
                throw new InvalidOperationException("Queue retarget clock could not be rebased.");
            ConfigureAnimations(timestamp);
            _device.Commit().CheckError();
            Volatile.Write(ref _animationStartedAtTimestamp, timestamp);
            _successorHeld = false;
            _completionRetryCount = 0;
            _completionRetrySuccess = true;
            if (_completionPendingDuringSuccessorHold)
            {
                _completionPendingDuringSuccessorHold = false;
                CompleteNow(_pendingSuccessorCompletionSuccess);
                return true;
            }
            if (RoutesPointerInput) _sampleTimer.Start();
            _completionTimer.Interval = TimeSpan.FromMilliseconds(
                plan.DurationMilliseconds + CompletionGuardMilliseconds);
            _completionTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Queue translation retarget failed: {0}", ex);
            _successorHeld = false;
            CompleteNow(success: false);
            return true;
        }
    }
}
