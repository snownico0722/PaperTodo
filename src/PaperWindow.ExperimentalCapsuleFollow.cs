using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static double ExperimentalCapsuleFollowSlideMilliseconds => InjectionParameters.ExperimentalFollowSlideMs.Value;
    private static int ExperimentalCapsuleFollowRetractDelayMilliseconds => InjectionParameters.ExperimentalFollowRetractMs.Value;

    private readonly record struct ExperimentalCapsuleFollowTransition(
        DeviceScreenRect StartBounds,
        long StartedAt,
        bool KeepBehindTargetUntilComplete,
        bool KeepCloseAsActivatorUntilComplete);

    private ExperimentalCapsuleFollowPlan?
        _experimentalCapsuleFollowPresentation;
    private ExperimentalCapsuleFollowTransition?
        _experimentalCapsuleFollowTransition;
    private DispatcherTimer? _experimentalCapsuleFollowRetractTimer;

    private bool ExperimentalCapsuleFollowCloseActivates =>
        _experimentalCapsuleFollowPresentation is
        {
            IsRetracted: true,
            HeadUsesCloseSegment: true
        } ||
        _experimentalCapsuleFollowTransition?
            .KeepCloseAsActivatorUntilComplete == true;

    private bool IsExperimentalCapsuleFollowBehindTarget =>
        _experimentalCapsuleFollowPresentation?.IsBehindTarget == true ||
        _experimentalCapsuleFollowTransition?
            .KeepBehindTargetUntilComplete == true;

    private IntPtr ExperimentalCapsuleFollowTargetHandle =>
        _paper.IsCollapsed &&
        _experimentalWindowAttachment is
        {
            TargetKind:
                ExperimentalAttachmentTargetKind.ExternalWindow
        } session
            ? session.ExternalWindow.Handle
            : IntPtr.Zero;

    private bool ShouldKeepExperimentalCapsuleFollowAboveTarget
    {
        get
        {
            var target = ExperimentalCapsuleFollowTargetHandle;
            if (target == IntPtr.Zero ||
                _experimentalCapsuleFollowPresentation == null ||
                IsExperimentalCapsuleFollowBehindTarget ||
                !_paper.IsCollapsed)
            {
                return false;
            }

            var foreground = ExternalWindowNative.ForegroundWindow;
            return foreground == target ||
                ExternalWindowNative.IsSameProcess(
                    _experimentalWindowAttachment!.ExternalWindow,
                    foreground);
        }
    }

    private IntPtr ResolveExperimentalCapsuleFollowZOrderTarget(
        IntPtr fullscreenAvoidanceWindow) =>
        IsExperimentalCapsuleFollowBehindTarget
            ? ExperimentalCapsuleFollowTargetHandle
            : fullscreenAvoidanceWindow;

    private void OnExperimentalCapsuleFollowHoverChanged(bool reveal)
    {
        if (reveal)
        {
            StopExperimentalCapsuleFollowRetractTimer();
        }

        var session = _experimentalWindowAttachment;
        if (!_paper.IsCollapsed ||
            session?.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow ||
            !ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot) ||
            !snapshot.IsUsableTarget ||
            !WindowNative.TryGetWindowDeviceBounds(
                this,
                out var currentBounds) ||
            !TryGetTargetMonitor(snapshot, out var monitor))
        {
            return;
        }

        var plan =
            ExperimentalWindowAttachmentGeometry
                .ResolveCapsuleFollow(
                    session,
                    snapshot.Bounds,
                    currentBounds,
                    monitor,
                    WindowWorkAreaHelper.ConnectedMonitorGeometries(),
                    reveal);
        SetExperimentalCapsuleFollowPresentation(
            plan,
            animate: true);
    }

    private void ScheduleExperimentalCapsuleFollowRetract()
    {
        var timer = _experimentalCapsuleFollowRetractTimer;
        if (timer == null)
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(
                    ExperimentalCapsuleFollowRetractDelayMilliseconds)
            };
            timer.Tick += OnExperimentalCapsuleFollowRetractTimerTick;
            _experimentalCapsuleFollowRetractTimer = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void OnExperimentalCapsuleFollowRetractTimerTick(
        object? sender,
        EventArgs e)
    {
        StopExperimentalCapsuleFollowRetractTimer();
        if (_capsuleShell?.IsMouseOver == true)
        {
            return;
        }

        OnExperimentalCapsuleFollowHoverChanged(reveal: false);
    }

    private void StopExperimentalCapsuleFollowRetractTimer()
    {
        _experimentalCapsuleFollowRetractTimer?.Stop();
    }

    private void SetExperimentalCapsuleFollowPresentation(
        ExperimentalCapsuleFollowPlan plan,
        bool animate)
    {
        var previous = _experimentalCapsuleFollowPresentation;
        var modeChanged =
            !previous.HasValue ||
            previous.Value.Mode != plan.Mode;
        _experimentalCapsuleFollowPresentation = plan;

        DeviceScreenRect currentBounds = default;
        var shouldAnimate =
            animate &&
            modeChanged &&
            _controller.State.EnableAnimations &&
            WindowNative.TryGetWindowDeviceBounds(
                this,
                out currentBounds) &&
            currentBounds != plan.Bounds;
        if (!shouldAnimate)
        {
            _experimentalCapsuleFollowTransition = null;
            ApplyExperimentalAttachmentBounds(plan.Bounds);
            if (modeChanged)
            {
                RefreshExperimentalCapsuleFollowVisual();
                RefreshEffectiveTopmost();
            }
            return;
        }

        _experimentalCapsuleFollowTransition =
            new ExperimentalCapsuleFollowTransition(
                currentBounds,
                Stopwatch.GetTimestamp(),
                KeepBehindTargetUntilComplete:
                    previous?.Mode ==
                        ExperimentalCapsuleFollowMode
                            .RetractedBehindTarget &&
                    plan.Mode ==
                        ExperimentalCapsuleFollowMode.Revealed,
                KeepCloseAsActivatorUntilComplete:
                    previous?.HeadUsesCloseSegment == true &&
                    plan.Mode ==
                        ExperimentalCapsuleFollowMode.Revealed);
        RefreshExperimentalCapsuleFollowVisual();
        RefreshEffectiveTopmost();
        _controller.RequestExperimentalWindowFrames();
    }

    private bool AdvanceExperimentalCapsuleFollowTransition()
    {
        if (_experimentalCapsuleFollowTransition is not { } transition ||
            _experimentalCapsuleFollowPresentation is not { } presentation)
        {
            return false;
        }

        var elapsedMilliseconds =
            (Stopwatch.GetTimestamp() - transition.StartedAt) *
            1000.0 /
            Stopwatch.Frequency;
        var progress = Math.Clamp(
            elapsedMilliseconds /
                ExperimentalCapsuleFollowSlideMilliseconds,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        ApplyExperimentalAttachmentBounds(
            InterpolateExperimentalCapsuleFollowBounds(
                transition.StartBounds,
                presentation.Bounds,
                eased));
        if (progress < 1)
        {
            return true;
        }

        _experimentalCapsuleFollowTransition = null;
        if (transition.KeepCloseAsActivatorUntilComplete)
        {
            RefreshExperimentalCapsuleFollowVisual();
        }
        if (transition.KeepBehindTargetUntilComplete)
        {
            RefreshEffectiveTopmost();
        }
        return true;
    }

    private void PrepareExperimentalCapsuleFollowForDetach()
    {
        var session = _experimentalWindowAttachment;
        if (!_paper.IsCollapsed ||
            session?.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow ||
            !WindowNative.TryGetWindowDeviceBounds(
                this,
                out var currentBounds))
        {
            return;
        }

        DeviceScreenRect visibleBounds;
        if (ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot) &&
            snapshot.IsUsableTarget &&
            TryGetTargetMonitor(snapshot, out var monitor))
        {
            visibleBounds =
                ExperimentalWindowAttachmentGeometry
                    .ResolveCapsuleFollow(
                        session,
                        snapshot.Bounds,
                        currentBounds,
                        monitor,
                        WindowWorkAreaHelper
                            .ConnectedMonitorGeometries(),
                        reveal: true)
                    .Bounds;
        }
        else
        {
            var center = new DeviceScreenPoint(
                currentBounds.Left + currentBounds.Width / 2.0,
                currentBounds.Top + currentBounds.Height / 2.0);
            visibleBounds =
                WindowWorkAreaHelper
                    .TryGetMonitorGeometryAtDeviceScreenPoint(
                        center,
                        this,
                        out var currentMonitor)
                    ? ExperimentalWindowAttachmentGeometry
                        .KeepContained(
                            currentBounds,
                            currentMonitor.WorkArea)
                    : currentBounds;
        }

        _experimentalCapsuleFollowTransition = null;
        ApplyExperimentalAttachmentBounds(visibleBounds);
    }

    private void ClearExperimentalCapsuleFollowPresentation()
    {
        StopExperimentalCapsuleFollowRetractTimer();
        var changed =
            _experimentalCapsuleFollowPresentation.HasValue ||
            _experimentalCapsuleFollowTransition.HasValue;
        _experimentalCapsuleFollowPresentation = null;
        _experimentalCapsuleFollowTransition = null;
        if (!changed)
        {
            return;
        }

        RefreshExperimentalCapsuleFollowVisual();
        RefreshEffectiveTopmost();
    }

    private static DeviceScreenRect
        InterpolateExperimentalCapsuleFollowBounds(
            DeviceScreenRect from,
            DeviceScreenRect to,
            double progress)
    {
        var left = Interpolate(from.Left, to.Left, progress);
        var top = Interpolate(from.Top, to.Top, progress);
        return new DeviceScreenRect(
            left,
            top,
            left + to.Width,
            top + to.Height);

        static int Interpolate(int start, int end, double amount) =>
            (int)Math.Round(
                start + (end - start) * amount,
                MidpointRounding.AwayFromZero);
    }
}
