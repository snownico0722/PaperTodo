namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal bool CanPreacquireEdgeCapsuleSource => CanEnterEdgeCapsulePreview &&
        !IsEdgeCapsulePreviewOpen && !EdgeCapsulePreviewPointerCaptureActive &&
        !_edgeCapsule.ContextMenuOpen && EdgeCapsuleGesture == EdgeCapsuleGestureState.Idle &&
        CurrentEdgeCapsuleVisualAuthority == EdgeCapsuleVisualAuthority.RealDocked &&
        _edgeCapsule.IsSettledForPreacquisition;

    [System.Diagnostics.Conditional("DEBUG")]
    internal void TraceEdgeCapsuleCompositionVisibility(string context)
    {
#if DEBUG
        _edgeCapsuleHost?.TraceCompositionVisibility(
            $"{context} paper={_paper.Id} " +
            $"authority={CurrentEdgeCapsuleVisualAuthority}");
#endif
    }

    internal EdgeCapsuleVisualAuthority CurrentEdgeCapsuleVisualAuthority
    {
        get
        {
            var floatingCoverActive =
                _deepCapsuleFloatingDragHost is { IsVisible: true };
            return EdgeCapsuleQueueProxyPolicy.ResolveVisualAuthority(
                _edgeCapsule.State.Gesture,
                floatingCoverActive,
                _controller.IsEdgeCapsuleQueueProxyRetainingSource(
                    this));
        }
    }

    internal EdgeCapsuleQueueProxyCandidate?
        CaptureEdgeCapsuleQueueProxyCandidate(
            string queueKey,
            EdgeCapsuleMotion motion,
            EdgeCapsulePresentationFrame? startOverride = null,
            EdgeCapsulePresentationFrame? sourceOverride = null,
            bool retainedByCurrentProxy = false)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            _edgeCapsuleHost is not { IsVisible: true } host)
        {
            return null;
        }

        var applied = _edgeCapsule.AppliedPresentation;
        var start = startOverride ?? applied;
        var source = sourceOverride ?? applied;
        var target = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        var sourceReady = retainedByCurrentProxy
            ? host.MatchesQueueTranslationSurface(source)
            : host.MatchesPresentation(source);
        return new EdgeCapsuleQueueProxyCandidate(
            _paper.Id,
            queueKey,
            start,
            source,
            target,
            motion,
            host.Handle != IntPtr.Zero &&
                sourceReady &&
                !IsExperimentalPassive &&
                !_advancedInteractionLocked &&
                !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
                _controller.FullscreenAvoidanceWindowForQueue(
                    _paper.CapsuleMonitorDeviceName) == IntPtr.Zero,
            host.IsTopmost,
            retainedByCurrentProxy,
            CurrentEdgeCapsuleVisualAuthority);
    }

    internal (
        DeviceScreenRect PreviewBounds,
        int MaximumDownwardShiftDevice)
        CaptureEdgeCapsuleQueueProxyCapacity()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return default;
        }

        // Collapse-all/release is a queue-wide visual-only translation. During collapse the model
        // has already entered RetractedCollapsed; during release the real applied frame is still
        // DockedRetracted until the shared transaction commits. Neither path can open Preview while
        // it is moving, so reserving future Preview capacity here has no visual purpose.
        if (IsDeepCapsuleRetractedIntoMaster ||
            _edgeCapsule.AppliedPresentation.Surface ==
                EdgeCapsuleSurfaceKind.DockedRetracted)
        {
            return default;
        }

        var layout = CaptureEdgeCapsuleLayoutSnapshot();
        if (!layout.IsUsable)
        {
            return default;
        }

        // This is output-HWND capacity, not a WPF backing surface. A plugin may declare any finite
        // miniMaxSize it needs; an omitted declaration gets the finite 800x600/initial-size fallback.
        // The monitor work area remains the only host-imposed physical clamp, so the proxy no longer
        // reserves a full-work-area envelope merely because the protocol had no finite maximum.
        var workAreaDip = layout.Monitor.LocalWorkAreaDip;
        var maximumPreview = ResolveEdgeCapsulePreviewMaximumCapacity()
            .Normalize(
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumWidthDip,
                    workAreaDip.Width - 16),
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumHeightDip,
                    workAreaDip.Height - 16));
        var previewBodyWidth = Math.Max(
            1,
            maximumPreview.WidthDip -
            layout.MaximumCloseWidthDip);
        var preview = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                layout.NormalTopDip,
                previewBodyWidth,
                layout.MaximumCloseWidthDip,
                maximumPreview.HeightDip));
        var compact = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                layout.NormalTopDip,
                layout.RestingWidthDip,
                0,
                layout.HeightDip));
        return (
            preview.Bounds,
            EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(
                compact.Bounds,
                preview.Bounds,
                layout.Monitor.WorkArea.Bottom));
    }

    internal Func<bool> CaptureEdgeCapsulePointerInputValidity()
    {
        var source = EdgeCapsuleQueueProxySourceHandle;
        var body = _bodySessionGeneration;
        var preview = _edgeCapsulePreviewRequest;
        return () => CanRouteEdgeCapsuleQueueProxyInput && !IsClosed &&
            source != IntPtr.Zero && EdgeCapsuleQueueProxySourceHandle == source &&
            _bodySessionGeneration == body && ReferenceEquals(_edgeCapsulePreviewRequest, preview);
    }

    internal IntPtr EdgeCapsuleQueueProxySourceHandle =>
        _edgeCapsuleHost?.Handle ?? IntPtr.Zero;

    internal bool CanRouteEdgeCapsuleQueueProxyInput =>
        CanEnterEdgeCapsulePreview;

    internal bool IsEdgeCapsuleQueueProxyInputSettled =>
        _windowLifecycle == PaperWindowLifecycleState.Alive && !IsClosed &&
        _edgeCapsule.IsSettledForPreacquisition;

    internal bool TryGetEdgeCapsuleQueueProxyAppliedPresentation(
        out EdgeCapsulePresentationFrame frame)
    {
        frame = EdgeCapsulePresentationFrame.Hidden;
        return _windowLifecycle == PaperWindowLifecycleState.Alive &&
            !IsClosed && _edgeCapsuleHost != null &&
            _edgeCapsuleHost.TryGetAppliedPresentation(out frame);
    }

    internal bool TryPrepareSettledQueueProxyInput(out EdgeCapsulePresentationFrame frame)
    {
        frame = _edgeCapsule.AppliedPresentation;
        return IsEdgeCapsuleQueueProxyInputSettled && _edgeCapsuleHost != null &&
            frame.Visible && _edgeCapsuleHost.PrepareCompositionSourceLayoutForBatchHandoff();
    }

    internal bool VerifySettledQueueProxyInput(EdgeCapsulePresentationFrame frame) =>
        IsEdgeCapsuleQueueProxyInputSettled && _edgeCapsule.AppliedPresentation == frame &&
        _edgeCapsuleHost?.MatchesPresentation(frame) == true;

    internal bool ApplyEdgeCapsuleQueueProxyEndpoint(
        EdgeCapsulePresentationFrame endpoint)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return false;
        }
        if (!endpoint.Visible)
        {
            return _edgeCapsuleHost?.Apply(endpoint) ?? true;
        }
        return EnsureDeepCapsuleSlotHost().Apply(endpoint);
    }

    internal bool
        PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff() =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        !IsClosed &&
        (_edgeCapsuleHost?
            .PrepareCompositionSourceLayoutForBatchHandoff() ??
         false);

    internal bool TryApplyLatestEdgeCapsuleQueueProxyEndpoint(
        out EdgeCapsulePresentationFrame endpoint)
    {
        endpoint = EdgeCapsulePresentationFrame.Hidden;
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
        }

        endpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        return ApplyEdgeCapsuleQueueProxyEndpoint(endpoint);
    }

    internal bool VerifyEdgeCapsuleQueueProxyEndpoint(
        EdgeCapsulePresentationFrame endpoint)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return _edgeCapsuleHost?.Handle is not { } handle ||
                !WindowNative.IsWindowHandleAlive(handle);
        }

        var latestEndpoint = _edgeCapsule
            .PlanTargetPresentation(
                CaptureEdgeCapsuleLayoutSnapshot())
            .ToFrame();
        var presenterSettled =
            !_edgeCapsule.HasActiveTransition &&
            _edgeCapsule.AppliedPresentation == endpoint &&
            latestEndpoint == endpoint;
#if DEBUG
        if (!presenterSettled)
        {
            var applied = _edgeCapsule.AppliedPresentation;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=presenter-mismatch " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"transition={_edgeCapsule.HasActiveTransition} " +
                $"applied={applied.Surface}:{applied.Bounds.Width}x{applied.Bounds.Height} " +
                $"endpoint={endpoint.Surface}:{endpoint.Bounds.Width}x{endpoint.Bounds.Height} " +
                $"latest={latestEndpoint.Surface}:{latestEndpoint.Bounds.Width}x{latestEndpoint.Bounds.Height}");
        }
#endif
        if (!presenterSettled)
        {
            return false;
        }

        return endpoint.Visible
            ? _edgeCapsuleHost?.MatchesPresentation(endpoint) == true
            : _edgeCapsuleHost == null ||
              _edgeCapsuleHost.MatchesPresentation(endpoint);
    }

    // Queue-proxy pointer sampling is richer than the Presenter's state: the controller still needs
    // every physical point for target/corridor arbitration, while the Presenter only stores whether
    // the point is over its current interactive surface. Do not manufacture Pointer dirty work when
    // the same reducer intent would be a no-op; doing so makes the same tick fail the settled-input
    // handoff test that follows it and can starve selective handoff during continuous in-card motion.
    internal bool ShouldInvalidateEdgeCapsuleQueueProxyPointer(
        DeviceScreenPoint? pointer,
        EdgeCapsulePresentationFrame presentedFrame)
    {
        var over = pointer.HasValue &&
            presentedFrame.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(
                presentedFrame.InteractiveBounds,
                pointer.Value);
        return EdgeCapsuleReducer.Reduce(
            _edgeCapsule.Model,
            EdgeCapsuleIntent.PointerSampled(over)).Changed;
    }

    internal void InvalidateEdgeCapsuleQueueProxyPointer(DeviceScreenPoint? pointer)
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        var presentedFrame = ResolveEdgeCapsulePresentedFrame(
            _edgeCapsule.AppliedPresentation);
        var needsPresenterReconcile =
            ShouldInvalidateEdgeCapsuleQueueProxyPointer(
                pointer,
                presentedFrame);
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(
            this,
            pointer);
        if (needsPresenterReconcile)
        {
            InvalidateEdgeCapsulePointer();
        }
    }

    internal void FlushEdgeCapsuleQueueProxyEndpoint()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        // This is a real synchronous settlement barrier, not a best-effort equality shortcut.
        // Pending Measure/Pointer/Presentation work can exist even when the currently applied
        // frame and host already equal the old target; Flush cancels that queued reconcile and
        // consumes the latest model/layout before the DComp cover is allowed to disappear.
        _edgeCapsule.CancelTransition();
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure |
            EdgeCapsuleDirty.Pointer);
    }
}