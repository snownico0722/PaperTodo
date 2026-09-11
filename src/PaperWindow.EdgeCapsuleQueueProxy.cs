namespace PaperTodo;

public sealed partial class PaperWindow
{
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
        int MaximumDownwardShiftDevice,
        int WorkAreaBottomDevice)
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
            Math.Max(
                0,
                preview.Bounds.Height -
                compact.Bounds.Height),
            layout.Monitor.WorkArea.Bottom);
    }

    internal IntPtr EdgeCapsuleQueueProxySourceHandle =>
        _edgeCapsuleHost?.Handle ?? IntPtr.Zero;

    internal bool CanRouteEdgeCapsuleQueueProxyInput =>
        CanEnterEdgeCapsulePreview;

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

    internal void InvalidateEdgeCapsuleQueueProxyPointer()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed)
        {
            return;
        }

        var pointer = CaptureEdgeCapsulePointerPosition();
        _controller.NotifyEdgeCapsulePreviewPhysicalPointer(
            this,
            pointer);
        InvalidateEdgeCapsulePointer();
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
