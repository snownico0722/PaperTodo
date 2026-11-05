using System.Windows.Interop;

namespace PaperTodo;

public sealed partial class MasterCapsuleWindow
{
    internal string QueueProxyKey => _queueKey;
    internal string QueueProxySourceId => "master:" + _queueKey;
    internal bool IsQueueProxyClosed => _isClosingForReal;
    internal bool CanRouteQueueProxyInput => !_isClosingForReal && IsVisible &&
        !_experimentalPassive && _gestureState == MasterGestureState.Idle;
    internal IntPtr QueueProxySourceHandle => new WindowInteropHelper(this).Handle;

    internal EdgeCapsulePresentationFrame QueueProxyTarget
    {
        get
        {
            if (_isClosingForReal || !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    _queueMonitorDeviceName, this, out var monitor))
                return EdgeCapsulePresentationFrame.Hidden;
            var geometry = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
                monitor, _queueEdge, QueueTopForArea(monitor.LocalWorkAreaDip),
                MasterDockedWidth(monitor.DpiScaleY), 0, PaperLayoutDefaults.CapsuleHeight));
            return new EdgeCapsulePresentationFrame(true, EdgeCapsuleSurfaceKind.DockedResting,
                geometry.Bounds, geometry.Bounds, geometry.InteractiveBounds, _queueEdge,
                geometry.RestingWidthDevice, geometry.WallDeviceX,
                geometry.DpiScaleX, geometry.DpiScaleY, 0, 1, 1, true, true, false);
        }
    }

    internal EdgeCapsuleQueueProxyCandidate? CaptureQueueProxyCandidate(
        EdgeCapsuleQueueCompositionProxy? predecessor)
    {
        if (!CanRouteQueueProxyInput || !Topmost ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var bounds)) return null;
        var target = QueueProxyTarget;
        var source = target with { Bounds = bounds, HostBounds = bounds,
            InteractiveBounds = EdgeCapsuleGeometry.InteractiveBoundsForAppliedBounds(
                bounds, _queueEdge, target.DpiScaleX, target.DpiScaleY, WindowChromeMargin) };
        var start = source;
        var retained = predecessor?.RetainsSource(this) == true;
        if (retained)
        {
            predecessor!.TryGetPresentation(this, out start);
            predecessor.TryGetSourcePresentation(this, out source);
        }
        return new EdgeCapsuleQueueProxyCandidate(QueueProxySourceId, _queueKey,
            start, source, target,
            EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Placement,
                AnimationTiming.ScaleMilliseconds(EdgeCapsuleLayout.SlotMoveMilliseconds)),
            QueueProxySourceHandle != IntPtr.Zero, Topmost, retained);
    }

    internal bool ApplyQueueProxyEndpoint()
    {
        if (_isClosingForReal) return !WindowNative.IsWindowHandleAlive(QueueProxySourceHandle);
        var target = QueueProxyTarget;
        if (!target.Visible || !target.IsUsable) return false;
        ApplyMasterEdgeLayout();
        ++_moveGeneration; // Retire idle settles belonging to the preceding direct move.
        var applied = WindowNative.TrySetWindowDeviceBounds(this, target.Bounds);
        if (applied) _currentTopDip = AnchorTop;
        return applied;
    }

    internal bool VerifyQueueProxyEndpoint(EdgeCapsulePresentationFrame target) =>
        _isClosingForReal
            ? !WindowNative.IsWindowHandleAlive(QueueProxySourceHandle)
            : target == QueueProxyTarget &&
              WindowNative.TryGetWindowDeviceBounds(this, out var bounds) && bounds == target.Bounds;
}
