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

    // A visible cover must still shield its actual card area while handoff/hold/retry temporarily
    // rejects business input. Letting those presses through could activate another application.
    // This is derived ownership, not another per-paper hit model.
    private bool CanMaintainNativeInputRegion =>
        !_disposed && _coverPublished && !_coverLost && !_sourcesReleased &&
        ReferenceEquals(_host.Current, this) && RoutesPointerInput;

    private bool CanRoutePointerInput =>
        CanMaintainNativeInputRegion && !_starting && !_finishing && !_successorHeld &&
        !(_completionRetryCount > 0 && _completionTimer.IsEnabled);

    private bool ContainsVisual(DeviceScreenPoint point) =>
        CanMaintainNativeInputRegion && ContainsPresentedInput(point);

    // Completion temporarily closes native routing, but still needs the actual pointer/shape to
    // decide whether retaining the cover would deny the settled WPF controls their normal input.
    internal bool HasInteractivePointer() =>
        WindowNative.TryGetCursorScreenPosition(out var point) && ContainsPresentedInput(point);

    private bool ContainsPresentedInput(DeviceScreenPoint point)
    {
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

        if (!CanMaintainNativeInputRegion)
        {
            return;
        }
        if (!RefreshNativeInputRegion())
        {
            CompleteNow(success: false);
            return;
        }
        // Continue the existing geometry sampling while business input is suspended. Native
        // messages within the live cover are swallowed; transparent holes remain OS passthrough.
        if (!CanRoutePointerInput) return;
        DeviceScreenPoint? pointer = WindowNative.TryGetCursorScreenPosition(out var position)
            ? position : null;
        if (ShouldDispatchPointerSample(pointer))
        {
            foreach (var member in _members)
            {
                if (!CanRoutePointerInput) break;
                member.Window.InvalidateEdgeCapsuleQueueProxyPointer(pointer);
            }
        }

        // Input invalidation above may start a local WPF shape change without a new translation
        // generation. Wait for that work to settle; do not snap an opening preview just to deliver
        // hover. Check even after an unchanged sample so its final reconcile can release the cover.
        if (ShouldReleaseForPointerInput(pointer))
        {
            var now = Stopwatch.GetTimestamp();
            var member = _members.FirstOrDefault(item =>
                TrySamplePresentation(item, now, out var frame) && frame.Visible && frame.IsHitTestVisible &&
                EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, pointer!.Value));
            if (member != null && SettledInputRequested is { } request)
                request(this, member.Window);
            else
                CompleteNow(success: true);
        }
    }

    internal bool ShouldReleaseForPointerInput(DeviceScreenPoint? pointer) =>
        CanRoutePointerInput && IsRetainedForQueueBrowsing && pointer.HasValue && ContainsVisual(pointer.Value) &&
        _members.All(member => member.Window.IsEdgeCapsuleQueueProxyInputSettled);

    private bool RefreshNativeInputRegion()
    {
        // A predecessor and its staged successor share this HWND pair. Only the published owner
        // may mutate its native hit region, including when an older callback resumes after reentry.
        if (!ReferenceEquals(_host.Current, this)) return false;
        // The output is permanently click-through. Only this finite native input region owns
        // mouse messages, and its geometry comes from the same applied/translation frame as routing.
        // Changing the input region never clips the compositor output or resizes a WPF source.
        var regions = _nativeInputRegions;
        regions.Clear();
        if (CanMaintainNativeInputRegion)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var member in _members)
            {
                if (member.Window.CanRouteEdgeCapsuleQueueProxyInput &&
                    TrySamplePresentation(member, now, out var frame) &&
                    frame.Visible && frame.IsHitTestVisible && !frame.InteractiveBounds.IsEmpty)
                {
                    regions.Add(frame.InteractiveBounds);
                }
            }
        }
        return _window.TrySetInputRegions(regions);
    }

    private bool ClearNativeInputRegionIfOwned()
    {
        if (!ReferenceEquals(_host.Current, this)) return true;
        if (_window.Handle == IntPtr.Zero)
        {
            // TrySetInputRegions requires a complete pair. If output destruction left only the
            // input HWND alive, there is no visual cover to preserve; hide its surviving partner.
            _window.Hide();
            return true;
        }
        return _window.TrySetInputRegions(Array.Empty<DeviceScreenRect>());
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
            _inputHandoff is { Count: > 0 } ||
            _starting ||
            _finishing ||
            _coverLost ||
            _sourcesReleased ||
            !_coverPublished ||
            _successorHeld ||
            (_completionRetryCount > 0 && _completionTimer.IsEnabled) ||
            !ReferenceEquals(_host.Current, this))
        {
            return false;
        }

        _successorHeld = true;
        _completionPendingDuringSuccessorHold = false;
        _pendingSuccessorCompletionSuccess = true;
        _completionTimer.Stop();
        var inputReady = RefreshNativeInputRegion();
        if (_disposed || !_successorHeld || !ReferenceEquals(_host.Current, this)) return false;
        if (!inputReady)
        {
            _successorHeld = false;
            CompleteNow(success: false);
            return false;
        }
        if (CanMaintainNativeInputRegion) _sampleTimer.Start();
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
        if (!RefreshNativeInputRegion())
        {
            CompleteNow(success: false);
            return;
        }
        if (!CanRoutePointerInput && RoutesPointerInput) return;
        if (_disposed || _finishing || _successorHeld || !ReferenceEquals(_host.Current, this)) return;
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
        _sampleTimer.Stop();
        _ = ClearNativeInputRegionIfOwned();
        CompleteNow(success: false);
    }

    private void HandleSharedRuntimeLost()
    {
        if (_disposed || _sourcesReleased || _coverLost)
        {
            return;
        }

        _coverLost = true;
        _sampleTimer.Stop();
        _ = ClearNativeInputRegionIfOwned();
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
        _completionTimer.Stop();
        try
        {
            var ownedInput = ReferenceEquals(_host.Current, this);
            if (ownedInput) _ = RefreshNativeInputRegion();
            if (_disposed || !_finishing || _successorHeld ||
                (ownedInput && !ReferenceEquals(_host.Current, this))) return;
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
        if (!RefreshNativeInputRegion())
        {
            CompleteNow(success: false);
            return;
        }
        if (!CanRoutePointerInput && RoutesPointerInput) return;
        if (_disposed || _finishing || _successorHeld || !ReferenceEquals(_host.Current, this)) return;
        if (RoutesPointerInput) _sampleTimer.Start();
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

        // Preserve the visible cover's shield, but reject business input until handoff succeeds.
        // The existing sampler only refreshes current geometry during this retry window.
        _finishing = true;
        _completionRetrySuccess = success;
        _completionTimer.Stop();
        var ownedInput = ReferenceEquals(_host.Current, this);
        if (ownedInput) _ = RefreshNativeInputRegion();
        if (_disposed || !_finishing || _successorHeld ||
            (ownedInput && !ReferenceEquals(_host.Current, this))) return;
        _finishing = false;

        if (_coverLost)
        {
            _sampleTimer.Stop();
            _ = ClearNativeInputRegionIfOwned();
            if (_disposed || (ownedInput && !ReferenceEquals(_host.Current, this))) return;
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
            _sampleTimer.Stop();
            _ = ClearNativeInputRegionIfOwned();
            if (_disposed || (ownedInput && !ReferenceEquals(_host.Current, this))) return;
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
        // Establish retry admission before Start can synchronously notify Dispatcher hooks.
        if (CanMaintainNativeInputRegion && _completionTimer.IsEnabled) _sampleTimer.Start();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retry session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"attempt={_completionRetryCount} successTarget={success}");
#endif
    }
}
