using System.Diagnostics;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private const int MaximumCompletionRetryCount = 2;

    // A queue generation that enters or leaves DockedRetracted is the master collapse-all/release
    // animation. It is purely visual: the master owns the gesture, and moving proxy pixels must
    // never become a temporary mouse owner for the desktop or another application.
    private bool RoutesPointerInput =>
        !_plan.Members.Any(member =>
            member.Start.Surface == EdgeCapsuleSurfaceKind.DockedRetracted ||
            member.Target.Surface == EdgeCapsuleSurfaceKind.DockedRetracted);

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
        return _members.Any(member =>
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                return false;
            }
            return TrySamplePresentation(member, now, out var frame) &&
                frame.Visible &&
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
#if DEBUG
        using var edgeJournalStage = EdgeDiagnosticObservation.Begin("proxy.pointer", this);
#endif

        if (!CanRoutePointerInput)
        {
            return;
        }
        DeviceScreenPoint? pointer = WindowNative.TryGetCursorScreenPosition(out var position)
            ? position : null;
        if (!ShouldDispatchPointerSample(pointer)) return;
        foreach (var member in _members)
        {
            if (!CanRoutePointerInput) break;
            member.Window.InvalidateEdgeCapsuleQueueProxyPointer(pointer);
        }
    }

    private bool ShouldDispatchPointerSample(DeviceScreenPoint? pointer)
    {
        if (!_retainedAfterAnimation) return true;

        // A settled queue still observes pointer movement without keeping every WPF presenter
        // reconciling at the timer cadence. Applied shape changes also invalidate the sample, so
        // a stationary pointer is reconsidered when live WPF content changes its real hit area.
        var changed = !_hasRetainedPointerSample || _lastRetainedPointer != pointer;
        _lastRetainedPointerFrames ??= new EdgeCapsulePresentationFrame[_members.Count];
        for (var index = 0; index < _members.Count; index++)
        {
            _members[index].Window.TryGetEdgeCapsuleQueueProxyAppliedPresentation(out var frame);
            changed |= _lastRetainedPointerFrames[index] != frame;
            _lastRetainedPointerFrames[index] = frame;
        }
        _hasRetainedPointerSample = true;
        _lastRetainedPointer = pointer;
        return changed;
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
#if DEBUG
        using var edgeJournalStage = EdgeDiagnosticObservation.Begin("proxy.timer", this);
#endif

        _completionTimer.Stop();
        CompleteNow(_completionRetrySuccess, allowBrowseRetention: true);
    }

    internal bool TryGetPresentationAt(
        PaperWindow window,
        long timestamp,
        out EdgeCapsulePresentationFrame frame)
    {
        var member = _members.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Window, window));
        if (_disposed || _coverLost || member == null)
        {
            frame = EdgeCapsulePresentationFrame.Hidden;
            return false;
        }

        return TrySamplePresentation(member, timestamp, out frame);
    }

    private bool TrySamplePresentation(
        EdgeCapsuleQueueCompositionProxyMember member,
        long timestamp,
        out EdgeCapsulePresentationFrame frame)
    {
        if (_retainedAfterAnimation)
        {
            // Translation has settled, while the live WPF source may still change hover/content
            // presentation. Read that applied shape directly instead of replaying an old target.
            // This source accessor must not resolve through the proxy again.
            return member.Window.TryGetEdgeCapsuleQueueProxyAppliedPresentation(out frame) &&
                EdgeCapsuleQueueProxyPolicy.HasStableLiveSurfaceIdentity(frame, member.Plan.Target);
        }

        frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
            member.Plan,
            AnimationStartedAtTimestamp,
            _plan.DurationMilliseconds,
            timestamp);
        return true;
    }

    public bool TryGetPresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame) =>
        TryGetPresentationAt(
            window,
            Stopwatch.GetTimestamp(),
            out frame);

    public bool TryGetSourcePresentation(
        PaperWindow window,
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

    public bool RetainsSource(PaperWindow window) =>
        !_disposed &&
        !_sourcesReleased &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            member.SourceHandle != IntPtr.Zero);

    public bool Routes(PaperWindow window) =>
        !_disposed &&
        _members.Any(member =>
            ReferenceEquals(member.Window, window));

    public IntPtr SourceHandleFor(PaperWindow window) =>
        _members.FirstOrDefault(member =>
            ReferenceEquals(member.Window, window))
            ?.SourceHandle ?? IntPtr.Zero;

    public bool TryReserveForSuccessor()
    {
        if (_disposed ||
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
        foreach (var member in _members)
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                continue;
            }

            if (!TrySamplePresentation(member, now, out var current) ||
                !current.Visible || !current.IsHitTestVisible ||
                current.InteractiveBounds.IsEmpty ||
                !EdgeCapsuleGeometry.Contains(
                    current.InteractiveBounds,
                    point))
            {
                continue;
            }

            var offset =
                EdgeCapsuleQueueProxyPolicy.TranslationOffset(
                    member.Plan,
                    AnimationStartedAtTimestamp,
                    _plan.DurationMilliseconds,
                    now);
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

    private void HandleInteractionRequested(
        DeviceScreenPoint point,
        int message)
    {
        if (CanRoutePointerInput)
        {
            _interactionRequested(point, message);
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

    public void CompleteNow(bool success, bool allowBrowseRetention = false)
    {
#if DEBUG
        using var edgeJournalStage = EdgeDiagnosticObservation.Begin("proxy.complete", this);
#endif

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
            _completed(this, success, allowBrowseRetention);
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

    internal bool HasCompatibleEndpoint(
        PaperWindow window,
        EdgeCapsulePresentationFrame endpoint) =>
        _members.Any(member =>
            ReferenceEquals(member.Window, window) &&
            EdgeCapsuleQueueProxyPolicy.HasStableLiveSurfaceIdentity(endpoint, member.Plan.Target));

    internal void RetainForQueueBrowsing()
    {
        // The controller has verified the real/WPF endpoint and this queue remains browsable,
        // either after real movement or directly after a static idle acquisition.
        // Keep the same live authority for its successor, without scheduling another completion.
        // Explicit input/environment/lifecycle completion still releases it immediately.
        _retainedAfterAnimation = true;
        _finishing = false;
        if (RoutesPointerInput) _sampleTimer.Start();
    }

    public void ScheduleCompletionRetry(bool success)
    {
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
        _completionRetrySuccess = success;
        _completionTimer.Stop();

        if (_coverLost)
        {
            // The normal handoff budget is already exhausted (or the DComp output was lost). Source
            // reveal is now the only safe authority transition. Keep that emergency recovery paced
            // at 50 ms if Windows temporarily refuses the uncloak; never turn it into a Send loop.
            _completionRetrySuccess = false;
            _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
            _completionTimer.Start();
            return;
        }

        if (_completionRetryCount >= MaximumCompletionRetryCount)
        {
            // Two delayed retries are enough for transient WPF/native settlement. After that the
            // last proxy frame must not become a permanent authority: enter the existing cover-loss
            // path, which reveals real sources before this broken generation can retire.
            _coverLost = true;
            _completionRetrySuccess = false;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.handoff phase=retry-exhausted session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"attempts={_completionRetryCount} successTarget={success}");
#endif
            _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
            _completionTimer.Start();
            return;
        }

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
