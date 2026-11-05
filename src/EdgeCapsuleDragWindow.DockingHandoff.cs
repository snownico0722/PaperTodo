using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleDragWindow
{
    private enum DockingHandoffAnimationPhase
    {
        Flight,
        Reveal
    }

    private sealed record DockingHandoffAnimation(
        DockingHandoffAnimationPhase Phase,
        EdgeCapsuleFloatingHandoffGeometry Geometry,
        EdgeCapsuleEdge Edge,
        double SurfaceStartWidthDip,
        double SurfaceTargetWidthDip,
        double StartOpacity,
        long StartedAtTimestamp,
        long DurationTimestampTicks,
        Action<bool> Completed);

#if DEBUG
    private bool _diagnosticHandoffTracking;
    private string _diagnosticHandoffPhase = "<none>";
    private long _diagnosticHandoffStartedAt;
    private long _diagnosticLastHandoffFrameAt;
    private int _diagnosticHandoffFrames;
    private int _diagnosticHandoffNativeMoves;
#endif
    private DockingHandoffAnimation? _dockingHandoffAnimation;
    private double _currentSurfaceWidthDip;
    private EdgeCapsuleEdge _currentDockingEdge;
    private bool _dockingPresentationActive;
    private int _lastDockingDeviceLeft = int.MinValue;
    private int _lastDockingDeviceTop = int.MinValue;
    private double _lastDockingSurfaceWidthDip = double.NaN;
    private EdgeCapsuleEdge? _lastDockingEdge;

    public void AnimateDockingHandoff(
        DeviceScreenRect dockingAnchorBounds,
        EdgeCapsuleEdge targetEdge,
        int durationMilliseconds,
        Action<bool> completed)
    {
        if (_dockingPresentationActive && targetEdge != _currentDockingEdge)
        {
            CancelDockingHandoffAnimation();
            completed(false);
            return;
        }
        CancelDockingHandoffAnimation();
        if (_isClosed || dockingAnchorBounds.IsEmpty)
        {
            completed(false);
            return;
        }

        // A very quick release can overlap the entrance scale. The docking surface starts from the
        // real full-size pill, so remove that transform before sampling its physical rectangle.
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _entranceScale.ScaleX = 1;
        _entranceScale.ScaleY = 1;
        _surface.Opacity = 1;
        _outline.Opacity = 1;
        if (!WindowNative.TryGetWindowDeviceBounds(this, out var startHostBounds) ||
            startHostBounds.IsEmpty)
        {
            completed(false);
            return;
        }

        var targetCenter = new DeviceScreenPoint(
            dockingAnchorBounds.Left + dockingAnchorBounds.Width / 2.0,
            dockingAnchorBounds.Top + dockingAnchorBounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                targetCenter,
                out var targetMonitor))
        {
            completed(false);
            return;
        }

        var geometry = EdgeCapsuleGeometry.FloatingHandoffGeometry(
            startHostBounds,
            dockingAnchorBounds,
            targetEdge,
            _widthDip,
            _heightDip,
            targetMonitor.DpiScaleX,
            targetMonitor.DpiScaleY);
        if (!geometry.IsUsable)
        {
            completed(false);
            return;
        }

        var startSurfaceWidthDip = Math.Clamp(_currentSurfaceWidthDip, 1, _widthDip);
        var targetSurfaceWidthDip = geometry.SurfaceTargetWidthDip;
        _dockingPresentationActive = true;
        _currentDockingEdge = targetEdge;
#if DEBUG
        BeginHandoffDiagnostics("flight");
#endif

        // The WPF Window keeps its fixed logical Width/Height. Each frame moves only the HWND and
        // changes only the child width, so there is no second native size owner to fight WPF.
        if (!ApplyDockingFrame(
                new DeviceScreenPoint(startHostBounds.Left, startHostBounds.Top),
                startSurfaceWidthDip,
                targetEdge))
        {
#if DEBUG
            CompleteHandoffDiagnostics("initial-apply-failed");
#endif
            completed(false);
            return;
        }

        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Flight,
            geometry,
            targetEdge,
            startSurfaceWidthDip,
            targetSurfaceWidthDip,
            1,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        if (durationMilliseconds <= 0)
        {
            // A changed destination can be aligned once without starting another flight.
            // Keep the same endpoint apply, layout and physical-bounds verification.
            CompleteDockingHandoffAnimation(reachedTarget: true);
            return;
        }
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void AnimateDockingReveal(int durationMilliseconds, Action<bool> completed)
    {
        CancelDockingHandoffAnimation();
        if (_isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            completed(false);
            return;
        }

        // The confirmed docked host owns the final outline. Keep the floating body as the
        // anti-flash cover, but do not cross-fade two outlines with different edge geometry.
        _outline.Opacity = 0;
        var startOpacity = Math.Clamp(_surface.Opacity, 0, 1);
        if (startOpacity <= 0.001)
        {
            _surface.Opacity = 0;
            completed(true);
            return;
        }
#if DEBUG
        BeginHandoffDiagnostics("reveal");
#endif

        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Reveal,
            default,
            default,
            0,
            0,
            startOpacity,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void RestoreDockingCover()
    {
        if (_isClosed)
        {
            return;
        }

        _outline.Opacity = 1;
        _surface.Opacity = 1;
        WindowNative.BringToFrontNoActivate(this);
    }

    private void OnDockingHandoffFrame(object? sender, EventArgs e) =>
        AdvanceDockingHandoffFrame();

    private void AdvanceDockingHandoffFrame()
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null || _isClosed)
        {
            return;
        }
#if DEBUG
        RecordHandoffFrameDiagnostics();
#endif

        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - animation.StartedAtTimestamp);
        var rawProgress = Math.Clamp(
            elapsed / (double)animation.DurationTimestampTicks,
            0,
            1);
        var progress = 1.0 - Math.Pow(1.0 - rawProgress, 3.0);
        if (animation.Phase == DockingHandoffAnimationPhase.Flight)
        {
            var hostPosition = EdgeCapsuleGeometry.InterpolateDevicePosition(
                animation.Geometry.HostStartBounds,
                animation.Geometry.HostTargetBounds,
                progress);
            var surfaceWidthDip = animation.SurfaceStartWidthDip +
                (animation.SurfaceTargetWidthDip - animation.SurfaceStartWidthDip) * progress;
            if (!ApplyDockingFrame(
                    hostPosition,
                    surfaceWidthDip,
                    animation.Edge))
            {
                CompleteDockingHandoffAnimation(reachedTarget: false);
                return;
            }
        }
        else
        {
            _surface.Opacity = Math.Clamp(
                animation.StartOpacity * (1.0 - progress),
                0,
                1);
        }

        if (rawProgress >= 1)
        {
            CompleteDockingHandoffAnimation(reachedTarget: true);
        }
    }

    private void CompleteDockingHandoffAnimation(bool reachedTarget)
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null)
        {
            return;
        }

        CompositionTarget.Rendering -= OnDockingHandoffFrame;
        if (!reachedTarget ||
            _isClosed ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("interrupted");
#endif
            animation.Completed(false);
            return;
        }

        if (animation.Phase == DockingHandoffAnimationPhase.Reveal)
        {
            _surface.Opacity = 0;
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("completed");
#endif
            animation.Completed(true);
            return;
        }

        if (!ApplyDockingFrame(
                new DeviceScreenPoint(
                    animation.Geometry.HostTargetBounds.Left,
                    animation.Geometry.HostTargetBounds.Top),
                animation.SurfaceTargetWidthDip,
                animation.Edge))
        {
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("endpoint-apply-failed");
#endif
            animation.Completed(false);
            return;
        }

        // The floating HWND still owns cover authority. Drain this tiny persistent visual tree
        // synchronously at the endpoint, then verify and complete in the same call stack. A
        // ContextIdle continuation can be starved for hundreds of milliseconds by high-refresh
        // Render/Input traffic even though the flight already reached its target.
        RefreshNativeMetricsLayout();
        CompleteDockingHandoffEndpointSettle(animation);
    }

    private void CompleteDockingHandoffEndpointSettle(DockingHandoffAnimation animation)
    {
        if (!ReferenceEquals(animation, _dockingHandoffAnimation) || _isClosed)
        {
            return;
        }

        var settled = ApplyDockingFrame(
                new DeviceScreenPoint(
                    animation.Geometry.HostTargetBounds.Left,
                    animation.Geometry.HostTargetBounds.Top),
                animation.SurfaceTargetWidthDip,
                animation.Edge,
                forceNativeMove: true) &&
            WindowNative.TryGetWindowDeviceBounds(this, out var actualHostBounds) &&
            EdgeCapsuleGeometry.DeviceBoundsMatch(
                actualHostBounds,
                animation.Geometry.HostTargetBounds,
                tolerance: 2) &&
            MatchesDockingSurfaceLayout(animation.SurfaceTargetWidthDip);
        _dockingHandoffAnimation = null;
#if DEBUG
        CompleteHandoffDiagnostics(settled ? "completed" : "endpoint-verify-failed");
#endif
        animation.Completed(settled);
    }

    private bool MatchesDockingSurfaceLayout(double targetWidthDip)
    {
        if (!double.IsFinite(targetWidthDip) ||
            targetWidthDip <= 0 ||
            !double.IsFinite(_surface.ActualWidth) ||
            !double.IsFinite(_surface.ActualHeight) ||
            _surface.ActualWidth <= 0 ||
            _surface.ActualHeight <= 0)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(_surface);
        var actualWidth = (int)Math.Round(
            _surface.ActualWidth * dpi.DpiScaleX,
            MidpointRounding.AwayFromZero);
        var actualHeight = (int)Math.Round(
            _surface.ActualHeight * dpi.DpiScaleY,
            MidpointRounding.AwayFromZero);
        var targetWidth = (int)Math.Round(
            targetWidthDip * dpi.DpiScaleX,
            MidpointRounding.AwayFromZero);
        var targetHeight = (int)Math.Round(
            _heightDip * dpi.DpiScaleY,
            MidpointRounding.AwayFromZero);
        return Math.Abs(actualWidth - targetWidth) <= 1 &&
            Math.Abs(actualHeight - targetHeight) <= 1;
    }

    private void CancelDockingHandoffAnimation()
    {
        if (_dockingHandoffAnimation == null)
        {
            return;
        }

        var animation = _dockingHandoffAnimation;
        _dockingHandoffAnimation = null;
        CompositionTarget.Rendering -= OnDockingHandoffFrame;
#if DEBUG
        CompleteHandoffDiagnostics("cancelled");
#endif
        animation.Completed(false);
    }

    private bool ApplyDockingFrame(
        DeviceScreenPoint hostPosition,
        double surfaceWidthDip,
        EdgeCapsuleEdge edge,
        bool forceNativeMove = false)
    {
        if (_isClosed ||
            !double.IsFinite(hostPosition.X) ||
            !double.IsFinite(hostPosition.Y) ||
            !double.IsFinite(surfaceWidthDip) ||
            surfaceWidthDip <= 0)
        {
            return false;
        }

        var deviceLeft = (int)Math.Round(
            hostPosition.X,
            MidpointRounding.AwayFromZero);
        var deviceTop = (int)Math.Round(
            hostPosition.Y,
            MidpointRounding.AwayFromZero);
        var clampedSurfaceWidth = Math.Clamp(
            surfaceWidthDip,
            1,
            _widthDip);
        if (_lastDockingEdge != edge ||
            !double.IsFinite(_lastDockingSurfaceWidthDip) ||
            Math.Abs(_lastDockingSurfaceWidthDip - clampedSurfaceWidth) > 0.01)
        {
            _surface.HorizontalAlignment = edge == EdgeCapsuleEdge.Left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            _surface.VerticalAlignment = VerticalAlignment.Center;
            _surface.Width = clampedSurfaceWidth;
            _surface.Height = _heightDip;
            _lastDockingSurfaceWidthDip = clampedSurfaceWidth;
            _lastDockingEdge = edge;
        }
        _currentSurfaceWidthDip = clampedSurfaceWidth;
        _currentDockingEdge = edge;

        // Rendering can repeat at the same display timestamp, and eased samples often round to
        // the same physical pixel. Each redundant SetWindowPos serializes with DWM for up to 10ms
        // in the captured traces, so collapse identical native positions before that boundary.
        if (!forceNativeMove &&
            _lastDockingDeviceLeft == deviceLeft &&
            _lastDockingDeviceTop == deviceTop)
        {
            return true;
        }

        // Position is committed last. Width/Height remain owned by the fixed WPF Window and are
        // never included in this native operation.
#if DEBUG
        var nativeStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var appliedPosition = new DeviceScreenPoint(
            deviceLeft,
            deviceTop);
        var moved = WindowNative.TryMoveWindowDevicePosition(
            this,
            appliedPosition);
        if (moved)
        {
            _lastDockingDeviceLeft = deviceLeft;
            _lastDockingDeviceTop = deviceTop;
        }
#if DEBUG
        RecordHandoffNativeMoveDiagnostics(
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(nativeStartedAt),
            appliedPosition,
            moved);
#endif
        return moved;
    }

#if DEBUG
    private void BeginHandoffDiagnostics(string phase)
    {
        if (_diagnosticHandoffTracking)
        {
            CompleteHandoffDiagnostics("restarted");
        }
        _diagnosticHandoffTracking = true;
        _diagnosticHandoffPhase = phase;
        _diagnosticHandoffStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        _diagnosticLastHandoffFrameAt = 0;
        _diagnosticHandoffFrames = 0;
        _diagnosticHandoffNativeMoves = 0;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={phase} event=begin paper={_diagnosticId} " +
            $"dragHost={_diagnosticHostId}");
    }

    private void RecordHandoffFrameDiagnostics()
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        var frameAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var totalMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            _diagnosticHandoffStartedAt,
            frameAt);
        var gapMs = _diagnosticLastHandoffFrameAt == 0
            ? totalMs
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _diagnosticLastHandoffFrameAt,
                frameAt);
        _diagnosticLastHandoffFrameAt = frameAt;
        _diagnosticHandoffFrames++;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=frame " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} " +
            $"sequence={_diagnosticHandoffFrames} totalMs={totalMs:F3} " +
            $"gapMs={gapMs:F3}");
    }

    private void RecordHandoffNativeMoveDiagnostics(
        double nativeMs,
        DeviceScreenPoint hostPosition,
        bool moved)
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        _diagnosticHandoffNativeMoves++;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=native-move " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} " +
            $"sequence={_diagnosticHandoffNativeMoves} outcome={(moved ? "success" : "failed")} " +
            $"nativeMs={nativeMs:F3} target={hostPosition.X:F0},{hostPosition.Y:F0}");
    }

    private void CompleteHandoffDiagnostics(string outcome)
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        var durationMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            _diagnosticHandoffStartedAt);
        _diagnosticHandoffTracking = false;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=summary " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} outcome={outcome} " +
            $"totalMs={durationMs:F3} frames={_diagnosticHandoffFrames} " +
            $"nativeMoves={_diagnosticHandoffNativeMoves}");
    }
#endif

    private static long AnimationDurationTicks(int durationMilliseconds) =>
        Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency * Math.Max(1, durationMilliseconds) / 1000.0));
}
