using System.Diagnostics;
using Vortice.DirectComposition;

namespace PaperTodo;

// The position and modifier state belong to the original native message, not to the later
// handoff callback. ScreenPoint is converted once, while the output HWND is still unchanged.
internal readonly record struct EdgeCapsulePointerDown(
    DeviceScreenPoint ScreenPoint, int Message, IntPtr KeyState);

// One native press may wait only for the synchronous authority-release attempt that it triggered.
// If that attempt schedules any retry, the press is dropped; old input is never replayed later.
internal sealed class EdgeCapsuleInputHandoff
{
    private sealed record Pending(Func<bool> IsCurrent, Action Deliver);
    private Pending? _pending;
    internal int Count => _pending == null ? 0 : 1;

    internal void Enqueue(Func<bool> isCurrent, Action deliver) =>
        _pending = new Pending(isCurrent, deliver);

    internal void Complete()
    {
        var pending = _pending;
        _pending = null; // Remove ownership BEFORE callbacks can re-enter completion.
        if (pending == null)
        {
            return;
        }

        try
        {
            if (pending.IsCurrent()) pending.Deliver();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Edge input transfer failed: {0}", error);
        }
    }

    // ScheduleCompletionRetry calls this before arming the retry. A press belongs only to the
    // original synchronous handoff attempt; any retry deliberately drops it instead of replaying it.
    internal void Prune() => Cancel();
    internal void Cancel() => _pending = null;
}

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private EdgeCapsuleInputHandoff? _inputHandoff;

    private const int MaximumCompletionRetryCount = 2;

    // A queue generation that enters or leaves DockedRetracted is the master collapse-all/release
    // animation. It is purely visual: the master owns the gesture, and moving proxy pixels must
    // never become a temporary mouse owner for the desktop or another application.
    private bool RoutesPointerInput => EdgeCapsuleQueueProxyPolicy.RoutesPointerInput(_plan);

    // The plan's pointer role is stable, but native messages can re-enter while a cover is
    // being published/replaced or released. All input entry points share this readiness boundary.
    private bool CanRoutePointerInput =>
        !_disposed && !_starting && _coverPublished && !_coverLost &&
        !_sourcesReleased && !_finishing && !_successorHeld && RoutesPointerInput;

    private bool ContainsVisual(DeviceScreenPoint point)
    {
        if (!CanRoutePointerInput)
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        return _visuals.Any(state =>
        {
            var member = state.Member;
            if (!member.CanRouteInput)
            {
                return false;
            }
            var frame = SamplePresentation(state, now);
            return frame.Visible &&
                frame.IsHitTestVisible &&
                !frame.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(
                    frame.InteractiveBounds,
                    point);
        });
    }

    private long AnimationStartedAtTimestamp =>
        Volatile.Read(ref _animationStartedAtTimestamp)
            is var started && started > 0
                ? started
                : Stopwatch.GetTimestamp();

    private void OnSampleTimerTick(object? sender, EventArgs e)
    {
        if (!CanRoutePointerInput)
        {
            return;
        }
        DeviceScreenPoint? pointer = WindowNative.TryGetCursorScreenPosition(out var current) ? current : null;
        foreach (var member in _members)
        {
            if (member.Window is PaperWindow paper)
                paper.InvalidateEdgeCapsuleQueueProxyPointer(pointer);
        }
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        CompleteNow(_completionRetrySuccess);
    }

    internal bool TryGetPresentationAt(
        System.Windows.Window window,
        long timestamp,
        out EdgeCapsulePresentationFrame frame)
    {
        var state = _visuals.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Member.Window, window));
        if (_disposed || _coverLost || state == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        frame = SamplePresentation(state, timestamp);
        return true;
    }

    public bool TryGetPresentation(
        System.Windows.Window window,
        out EdgeCapsulePresentationFrame frame) =>
        TryGetPresentationAt(
            window,
            Stopwatch.GetTimestamp(),
            out frame);

    public bool TryGetSourcePresentation(
        System.Windows.Window window,
        out EdgeCapsulePresentationFrame frame)
    {
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (_disposed || member == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        // Real HWNDs settle to Target at startup. Their live WPF surface may
        // continue morphing, but native capacity and identity are stable.
        frame = member.Plan.Target;
        return frame.IsUsable;
    }

    public bool RetainsSource(System.Windows.Window window) =>
        !_disposed &&
        !_sourcesReleased &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            member.SourceHandle != IntPtr.Zero);

    public bool Routes(System.Windows.Window window) =>
        !_disposed &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window));

    public IntPtr SourceHandleFor(System.Windows.Window window) =>
        _members.FirstOrDefault(member =>
            ReferenceEquals(member.Window, window))
            ?.SourceHandle ?? IntPtr.Zero;

    public bool TryReserveForSuccessor()
    {
        if (_disposed ||
            _inputHandoff is { Count: > 0 } ||
            _starting ||
            _finishing ||
            _coverLost ||
            _sourcesReleased ||
            !_coverPublished ||
            _successorHeld)
        {
            return false;
        }

        _successorHeld = true;
        _completionPendingDuringSuccessorHold = false;
        _pendingSuccessorCompletionSuccess = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=reserve session={_sessionOrdinal} " +
            $"queue={_plan.QueueKey} progress=" +
            $"{EdgeCapsuleQueueProxyPolicy.SampleProgress(AnimationStartedAtTimestamp, _plan.DurationMilliseconds, Stopwatch.GetTimestamp()):F4}");
#endif
        return true;
    }

    public void CompleteAfterFailedSuccessor(bool success)
    {
        if (_disposed || !_successorHeld)
        {
            return;
        }

        var pendingCompletion =
            _completionPendingDuringSuccessorHold;
        var pendingSuccess =
            _pendingSuccessorCompletionSuccess && success;
        _successorHeld = false;
        _completionPendingDuringSuccessorHold = false;
        _pendingSuccessorCompletionSuccess = true;

        if (pendingCompletion)
        {
            CompleteNow(pendingSuccess);
            return;
        }

        var durationTicks = Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency *
                Math.Max(1, _plan.DurationMilliseconds) /
                1000.0));
        var elapsedTicks = Math.Max(
            0,
            Stopwatch.GetTimestamp() -
            AnimationStartedAtTimestamp);
        if (elapsedTicks >= durationTicks)
        {
            CompleteNow(success);
            return;
        }

        var remainingMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(
                (durationTicks - elapsedTicks) *
                1000.0 /
                Stopwatch.Frequency));
        if (RoutesPointerInput) _sampleTimer.Start();
        _completionTimer.Interval =
            TimeSpan.FromMilliseconds(
                remainingMilliseconds +
                CompletionGuardMilliseconds);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=resume session={_sessionOrdinal} " +
            $"queue={_plan.QueueKey} remainingMs={remainingMilliseconds}");
#endif
    }

    public bool TryResolveInputTarget(
        DeviceScreenPoint point,
        out IntPtr targetHandle,
        out DeviceScreenPoint endpointPoint)
    {
        if (!CanRoutePointerInput)
        {
            targetHandle = IntPtr.Zero;
            endpointPoint = point;
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var state in _visuals)
        {
            var member = state.Member;
            if (!member.CanRouteInput)
            {
                continue;
            }

            var current = SamplePresentation(state, now);
            if (!current.IsHitTestVisible ||
                current.InteractiveBounds.IsEmpty ||
                !EdgeCapsuleGeometry.Contains(
                    current.InteractiveBounds,
                    point))
            {
                continue;
            }

            var offset =
                EdgeCapsuleQueueProxyPolicy.TranslationOffset(
                    current,
                    member.Plan.Target.HostBounds);
            targetHandle = member.SourceHandle;
            endpointPoint = new DeviceScreenPoint(
                point.X - offset.X,
                point.Y - offset.Y);
            return targetHandle != IntPtr.Zero;
        }

        targetHandle = IntPtr.Zero;
        endpointPoint = point;
        return false;
    }

    private void HandleInteractionRequested(EdgeCapsulePointerDown input)
    {
        if (CanRoutePointerInput)
        {
            _interactionRequested(input);
        }
    }

    private void HandleEnvironmentChanged()
    {
        if (!_disposed && !_starting)
        {
            _environmentChanged();
        }
    }

    private void HandleCompositionPaint()
    {
        if (_disposed || _sourcesReleased)
        {
            return;
        }
        try
        {
            using var baseDevice =
                _device.QueryInterface<IDCompositionDevice>();
            baseDevice.CheckDeviceState(out var valid).CheckError();
            if (valid)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue composition device check failed. " +
                "Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        HandleOutputLost();
    }

    private void HandleOutputLost()
    {
        _coverLost = true;
        CompleteNow(success: false);
    }

    private void HandleSharedRuntimeLost()
    {
        if (_disposed || _sourcesReleased || _coverLost)
        {
            return;
        }

        _coverLost = true;
        var dispatcher = _members[0].Window.Dispatcher;
        if (dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            CompleteNow(success: false);
            return;
        }

        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Send,
            (Action)(() => CompleteNow(success: false)));
    }

    public void CompleteNow(bool success)
    {
        if (_starting)
        {
            _completionPendingDuringStart = true;
            _pendingStartCompletionSuccess &= success;
            return;
        }
        if (_successorHeld)
        {
            _completionPendingDuringSuccessorHold = true;
            _pendingSuccessorCompletionSuccess &= success;
            return;
        }
        if (_disposed || _finishing)
        {
            return;
        }

        _finishing = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            _completed(this, success);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule queue proxy completion failed. " +
                "Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            ScheduleCompletionRetry(success: false);
        }
    }

    internal void DeferPointerDown(Func<bool> isCurrent, Action deliver) =>
        (_inputHandoff ??= new()).Enqueue(isCurrent, deliver);

    internal void CompleteDeferredPointerInput()
    {
        if (_coverLost)
        {
            _inputHandoff?.Cancel();
            return;
        }
        _inputHandoff?.Complete();
    }

    public void ScheduleCompletionRetry(bool success)
    {
        _inputHandoff?.Prune();
        if (_disposed)
        {
            return;
        }
        if (_successorHeld)
        {
            _completionPendingDuringSuccessorHold = true;
            _pendingSuccessorCompletionSuccess &= success;
            return;
        }
        if (_sourcesReleased)
        {
            DisposeCore(clearTargetRoot: true);
            return;
        }

        _finishing = false;
        _completionTimer.Stop();
        if (_completionRetryCount >= MaximumCompletionRetryCount)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.handoff phase=retry-exhausted session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"attempts={_completionRetryCount} successTarget={success} scheduled=false");
#endif
            return;
        }

        _completionRetrySuccess = success;
        _completionRetryCount++;
        _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _completionTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retry session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"attempt={_completionRetryCount} successTarget={success}");
#endif
    }
}
