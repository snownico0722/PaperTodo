using System.Diagnostics;

namespace PaperTodo;

internal enum ExperimentalAttachmentOwner
{
    CapsuleMagnet,
    WindowTether
}

internal enum ExperimentalAttachmentTargetKind
{
    Screen,
    ExternalWindow
}

internal enum ExperimentalAttachmentEdge
{
    Left,
    Right,
    Top,
    Bottom
}

internal sealed record ExperimentalWindowAttachmentSession(
    ExperimentalAttachmentOwner Owner,
    ExperimentalAttachmentTargetKind TargetKind,
    ExperimentalAttachmentEdge Edge,
    bool InsideTarget,
    ExternalWindowIdentity ExternalWindow,
    string MonitorDeviceName,
    string TargetTitle,
    double PerpendicularOffsetDevice,
    double GapDip,
    DeviceScreenRect LastTargetBounds);

internal readonly record struct ExperimentalAttachmentPlan(
    ExperimentalWindowAttachmentSession Session,
    DeviceScreenRect WindowBounds,
    double ScoreDip);

internal static partial class ExperimentalWindowAttachmentGeometry
{
    private const double MinimumTetherHandleWidthDip = 48;
    private const double MinimumTetherHandleHeightDip = 12;

    public static bool TryPlanCapsuleMagnet(
        DeviceScreenRect capsuleBounds,
        MonitorGeometry monitor,
        IReadOnlyList<ExternalWindowSnapshot> externalWindows,
        bool includeScreenEdges,
        bool includeWindowEdges,
        double snapDistanceDip,
        double windowGapDip,
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (capsuleBounds.IsEmpty ||
            !double.IsFinite(snapDistanceDip) ||
            snapDistanceDip <= 0)
        {
            return false;
        }

        var candidates = new List<ExperimentalAttachmentPlan>();
        if (includeScreenEdges)
        {
            AddScreenCandidates(
                candidates,
                capsuleBounds,
                monitor,
                snapDistanceDip);
        }

        if (includeWindowEdges)
        {
            foreach (var external in externalWindows)
            {
                AddExternalCandidates(
                    candidates,
                    capsuleBounds,
                    external,
                    snapDistanceDip,
                    windowGapDip);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        plan = candidates
            .OrderBy(candidate => candidate.ScoreDip)
            .ThenBy(candidate =>
                candidate.Session.TargetKind == ExperimentalAttachmentTargetKind.Screen
                    ? 0
                    : 1)
            .First();
        return true;
    }

    public static bool TryPlanWindowTether(
        DeviceScreenRect paperBounds,
        ExternalWindowSnapshot externalWindow,
        MonitorGeometry monitor,
        string preferredEdge,
        double gapDip,
        double dragRegionTopDip,
        double dragRegionHeightDip,
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (paperBounds.IsEmpty ||
            !externalWindow.IsUsableTarget ||
            monitor.WorkArea.IsEmpty ||
            !double.IsFinite(gapDip) ||
            !double.IsFinite(dragRegionTopDip) ||
            dragRegionTopDip < 0 ||
            !double.IsFinite(dragRegionHeightDip) ||
            dragRegionHeightDip <= 0)
        {
            return false;
        }

        var normalizedEdge =
            ExperimentalWindowTetherOptions.NormalizeEdge(preferredEdge);
        ExperimentalAttachmentEdge[] edges = normalizedEdge switch
        {
            ExperimentalWindowTetherOptions.Left =>
                [ExperimentalAttachmentEdge.Left],
            ExperimentalWindowTetherOptions.Right =>
                [ExperimentalAttachmentEdge.Right],
            ExperimentalWindowTetherOptions.Top =>
                [ExperimentalAttachmentEdge.Top],
            ExperimentalWindowTetherOptions.Bottom =>
                [ExperimentalAttachmentEdge.Bottom],
            _ => Enum.GetValues<ExperimentalAttachmentEdge>()
        };
        var candidates = new List<ExperimentalAttachmentPlan>();
        foreach (var edge in edges)
        {
            AddTetherCandidate(
                candidates,
                paperBounds,
                externalWindow,
                monitor,
                edge,
                insideTarget: false,
                gapDip,
                dragRegionTopDip,
                dragRegionHeightDip);
            AddTetherCandidate(
                candidates,
                paperBounds,
                externalWindow,
                monitor,
                edge,
                insideTarget: true,
                gapDip,
                dragRegionTopDip,
                dragRegionHeightDip);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        plan = candidates
            .OrderBy(candidate => candidate.Session.InsideTarget ? 1 : 0)
            .ThenBy(candidate => candidate.ScoreDip)
            .First();
        return true;
    }

    public static bool TryPlanCapsuleMagnetForExternalTarget(
        DeviceScreenRect capsuleBounds,
        ExternalWindowSnapshot externalWindow,
        ExperimentalAttachmentEdge edge,
        double gapDip,
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (capsuleBounds.IsEmpty ||
            !externalWindow.IsUsableTarget ||
            !double.IsFinite(gapDip))
        {
            return false;
        }

        var perpendicularOffset = edge is
            ExperimentalAttachmentEdge.Left or
            ExperimentalAttachmentEdge.Right
                ? capsuleBounds.Top - externalWindow.Bounds.Top
                : capsuleBounds.Left - externalWindow.Bounds.Left;
        var session = new ExperimentalWindowAttachmentSession(
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.ExternalWindow,
            edge,
            InsideTarget: false,
            externalWindow.Identity,
            "",
            externalWindow.Title,
            perpendicularOffset,
            Math.Max(0, gapDip),
            externalWindow.Bounds);
        var desired = Resolve(
            session,
            externalWindow.Bounds,
            capsuleBounds,
            externalWindow.DpiScale);
        if (desired.IsEmpty)
        {
            return false;
        }

        plan = new ExperimentalAttachmentPlan(
            session,
            desired,
            ScoreDip: 0);
        return true;
    }

    public static DeviceScreenRect Resolve(
        ExperimentalWindowAttachmentSession session,
        DeviceScreenRect targetBounds,
        DeviceScreenRect currentWindowBounds,
        double dpiScale)
    {
        if (targetBounds.IsEmpty || currentWindowBounds.IsEmpty)
        {
            return default;
        }

        var gapDevice = Math.Max(
            0,
            RoundDevice(session.GapDip * Math.Max(1, dpiScale)));
        var width = currentWindowBounds.Width;
        var height = currentWindowBounds.Height;
        var offset = session.Edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? ClampOffset(
                    session.PerpendicularOffsetDevice,
                    targetBounds.Height,
                    height)
                : ClampOffset(
                    session.PerpendicularOffsetDevice,
                    targetBounds.Width,
                    width);

        var left = currentWindowBounds.Left;
        var top = currentWindowBounds.Top;
        switch (session.Edge)
        {
            case ExperimentalAttachmentEdge.Left:
                left = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Left + gapDevice
                        : targetBounds.Left - gapDevice - width;
                top = targetBounds.Top + RoundDevice(offset);
                break;
            case ExperimentalAttachmentEdge.Right:
                left = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Right - gapDevice - width
                        : targetBounds.Right + gapDevice;
                top = targetBounds.Top + RoundDevice(offset);
                break;
            case ExperimentalAttachmentEdge.Top:
                left = targetBounds.Left + RoundDevice(offset);
                top = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Top + gapDevice
                        : targetBounds.Top - gapDevice - height;
                break;
            case ExperimentalAttachmentEdge.Bottom:
                left = targetBounds.Left + RoundDevice(offset);
                top = session.TargetKind == ExperimentalAttachmentTargetKind.Screen ||
                    session.InsideTarget
                        ? targetBounds.Bottom - gapDevice - height
                        : targetBounds.Bottom + gapDevice;
                break;
        }

        return new DeviceScreenRect(left, top, left + width, top + height);
    }

    public static bool KeepsTetherHandleReachable(
        DeviceScreenRect bounds,
        DeviceScreenRect workArea,
        double dpiScale,
        double dragRegionTopDip,
        double dragRegionHeightDip)
    {
        if (bounds.IsEmpty ||
            workArea.IsEmpty ||
            !double.IsFinite(dragRegionTopDip) ||
            dragRegionTopDip < 0 ||
            !double.IsFinite(dragRegionHeightDip) ||
            dragRegionHeightDip <= 0)
        {
            return false;
        }

        dpiScale = Math.Max(1, dpiScale);
        var dragRegionTop = bounds.Top +
            RoundDevice(dragRegionTopDip * dpiScale);
        var dragRegion = new DeviceScreenRect(
            bounds.Left,
            Math.Min(bounds.Bottom, dragRegionTop),
            bounds.Right,
            Math.Min(
                bounds.Bottom,
                dragRegionTop +
                RoundDevice(dragRegionHeightDip * dpiScale)));
        if (dragRegion.IsEmpty)
        {
            return false;
        }

        var visibleWidth = Math.Max(
            0,
            Math.Min(dragRegion.Right, workArea.Right) -
            Math.Max(dragRegion.Left, workArea.Left));
        var visibleHeight = Math.Max(
            0,
            Math.Min(dragRegion.Bottom, workArea.Bottom) -
            Math.Max(dragRegion.Top, workArea.Top));
        return visibleWidth >= Math.Min(
                dragRegion.Width,
                RoundDevice(MinimumTetherHandleWidthDip * dpiScale)) &&
            visibleHeight >= Math.Min(
                dragRegion.Height,
                RoundDevice(MinimumTetherHandleHeightDip * dpiScale));
    }

    public static DeviceScreenRect KeepTetherHandleVisible(
        DeviceScreenRect bounds,
        DeviceScreenRect workArea,
        double dpiScale,
        double dragRegionTopDip,
        double dragRegionHeightDip)
    {
        if (bounds.IsEmpty ||
            workArea.IsEmpty ||
            !double.IsFinite(dragRegionTopDip) ||
            dragRegionTopDip < 0 ||
            !double.IsFinite(dragRegionHeightDip) ||
            dragRegionHeightDip <= 0)
        {
            return bounds;
        }

        dpiScale = Math.Max(1, dpiScale);
        var visibleWidth = Math.Min(
            bounds.Width,
            RoundDevice(MinimumTetherHandleWidthDip * dpiScale));
        var dragRegionTopOffset = Math.Min(
            bounds.Height,
            RoundDevice(dragRegionTopDip * dpiScale));
        var dragRegionHeight = Math.Min(
            Math.Max(0, bounds.Height - dragRegionTopOffset),
            RoundDevice(dragRegionHeightDip * dpiScale));
        if (dragRegionHeight <= 0)
        {
            return bounds;
        }

        var visibleDragHeight = Math.Min(
            dragRegionHeight,
            RoundDevice(MinimumTetherHandleHeightDip * dpiScale));
        var minLeft = workArea.Left - bounds.Width + visibleWidth;
        var maxLeft = workArea.Right - visibleWidth;
        var minTop =
            workArea.Top + visibleDragHeight -
            dragRegionTopOffset - dragRegionHeight;
        var maxTop =
            workArea.Bottom - visibleDragHeight -
            dragRegionTopOffset;
        var left = Math.Clamp(bounds.Left, minLeft, maxLeft);
        var top = Math.Clamp(bounds.Top, minTop, maxTop);
        return new DeviceScreenRect(
            left,
            top,
            left + bounds.Width,
            top + bounds.Height);
    }

    private static void AddScreenCandidates(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect capsuleBounds,
        MonitorGeometry monitor,
        double snapDistanceDip)
    {
        if (monitor.WorkArea.IsEmpty)
        {
            return;
        }

        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Left,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleX,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Right,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleX,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Top,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleY,
            snapDistanceDip,
            gapDip: 0);
        AddCandidate(
            candidates,
            ExperimentalAttachmentOwner.CapsuleMagnet,
            ExperimentalAttachmentTargetKind.Screen,
            ExperimentalAttachmentEdge.Bottom,
            insideTarget: true,
            default,
            monitor.DeviceName,
            "",
            capsuleBounds,
            monitor.WorkArea,
            monitor.DpiScaleY,
            snapDistanceDip,
            gapDip: 0);
    }

    private static void AddExternalCandidates(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect capsuleBounds,
        ExternalWindowSnapshot external,
        double snapDistanceDip,
        double gapDip)
    {
        if (!external.IsUsableTarget)
        {
            return;
        }

        foreach (var edge in Enum.GetValues<ExperimentalAttachmentEdge>())
        {
            AddCandidate(
                candidates,
                ExperimentalAttachmentOwner.CapsuleMagnet,
                ExperimentalAttachmentTargetKind.ExternalWindow,
                edge,
                insideTarget: false,
                external.Identity,
                "",
                external.Title,
                capsuleBounds,
                external.Bounds,
                external.DpiScale,
                snapDistanceDip,
                gapDip);
            AddCandidate(
                candidates,
                ExperimentalAttachmentOwner.CapsuleMagnet,
                ExperimentalAttachmentTargetKind.ExternalWindow,
                edge,
                insideTarget: true,
                external.Identity,
                "",
                external.Title,
                capsuleBounds,
                external.Bounds,
                external.DpiScale,
                snapDistanceDip,
                gapDip);
        }
    }

    private static void AddTetherCandidate(
        ICollection<ExperimentalAttachmentPlan> candidates,
        DeviceScreenRect paperBounds,
        ExternalWindowSnapshot externalWindow,
        MonitorGeometry monitor,
        ExperimentalAttachmentEdge edge,
        bool insideTarget,
        double gapDip,
        double dragRegionTopDip,
        double dragRegionHeightDip)
    {
        var perpendicularOffset = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? paperBounds.Top - externalWindow.Bounds.Top
                : paperBounds.Left - externalWindow.Bounds.Left;
        var session = new ExperimentalWindowAttachmentSession(
            ExperimentalAttachmentOwner.WindowTether,
            ExperimentalAttachmentTargetKind.ExternalWindow,
            edge,
            insideTarget,
            externalWindow.Identity,
            monitor.DeviceName,
            externalWindow.Title,
            perpendicularOffset,
            Math.Max(0, gapDip),
            externalWindow.Bounds);
        var desired = Resolve(
            session,
            externalWindow.Bounds,
            paperBounds,
            externalWindow.DpiScale);
        if (desired.IsEmpty ||
            !KeepsTetherHandleReachable(
                desired,
                monitor.WorkArea,
                Math.Max(monitor.DpiScaleX, monitor.DpiScaleY),
                dragRegionTopDip,
                dragRegionHeightDip))
        {
            return;
        }

        var deltaX = desired.Left - paperBounds.Left;
        var deltaY = desired.Top - paperBounds.Top;
        var scoreDip = Math.Sqrt(
            deltaX * (double)deltaX +
            deltaY * (double)deltaY) /
            Math.Max(1, externalWindow.DpiScale);
        candidates.Add(new ExperimentalAttachmentPlan(
            session,
            desired,
            scoreDip));
    }

    private static void AddCandidate(
        ICollection<ExperimentalAttachmentPlan> candidates,
        ExperimentalAttachmentOwner owner,
        ExperimentalAttachmentTargetKind targetKind,
        ExperimentalAttachmentEdge edge,
        bool insideTarget,
        ExternalWindowIdentity externalWindow,
        string monitorDeviceName,
        string targetTitle,
        DeviceScreenRect currentWindowBounds,
        DeviceScreenRect targetBounds,
        double dpiScale,
        double snapDistanceDip,
        double gapDip)
    {
        dpiScale = Math.Max(1, dpiScale);
        var perpendicularOffset = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? currentWindowBounds.Top - targetBounds.Top
                : currentWindowBounds.Left - targetBounds.Left;
        var session = new ExperimentalWindowAttachmentSession(
            owner,
            targetKind,
            edge,
            insideTarget,
            externalWindow,
            monitorDeviceName,
            targetTitle,
            perpendicularOffset,
            gapDip,
            targetBounds);
        var desired = Resolve(
            session,
            targetBounds,
            currentWindowBounds,
            dpiScale);
        if (desired.IsEmpty)
        {
            return;
        }

        var axisDistanceDevice = edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? Math.Abs(desired.Left - currentWindowBounds.Left)
                : Math.Abs(desired.Top - currentWindowBounds.Top);
        var scoreDip = axisDistanceDevice / dpiScale;
        if (scoreDip > snapDistanceDip)
        {
            return;
        }

        if (!PerpendicularRangesTouch(
                edge,
                currentWindowBounds,
                targetBounds,
                RoundDevice(snapDistanceDip * dpiScale)))
        {
            return;
        }

        candidates.Add(new ExperimentalAttachmentPlan(
            session,
            desired,
            scoreDip));
    }

    private static bool PerpendicularRangesTouch(
        ExperimentalAttachmentEdge edge,
        DeviceScreenRect window,
        DeviceScreenRect target,
        int tolerance)
    {
        return edge is
            ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                ? window.Bottom >= target.Top - tolerance &&
                  window.Top <= target.Bottom + tolerance
                : window.Right >= target.Left - tolerance &&
                  window.Left <= target.Right + tolerance;
    }

    private static double ClampOffset(
        double offset,
        int targetLength,
        int windowLength)
    {
        if (!double.IsFinite(offset))
        {
            return 0;
        }

        return Math.Clamp(
            offset,
            0,
            Math.Max(0, targetLength - windowLength));
    }

    private static int RoundDevice(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

public sealed partial class PaperWindow
{
    private readonly record struct
        ExperimentalAttachmentFormContinuation(
            ExperimentalWindowAttachmentSession Session);

    private ExperimentalWindowAttachmentSession? _experimentalWindowAttachment;
    // One-shot handoff while the existing paper-form transition owns HWND
    // geometry; it never acts as a second tracking or presentation state.
    private ExperimentalAttachmentFormContinuation?
        _experimentalAttachmentFormContinuation;
    private ExperimentalTetherCapsuleWindow? _experimentalTetherCapsule;
    private bool _experimentalTetherPresentationSuppressed;
    private bool _experimentalTetherReplanPending;
    private ExperimentalAttachmentPreviewWindow?
        _experimentalCapsuleMagnetPreview;
    private ExperimentalAttachmentPlan?
        _experimentalCapsuleMagnetPreviewPlan;
    private bool _experimentalCapsuleMagnetDragPreviewActive;
    private long _experimentalCapsuleMagnetPreviewTimestamp;

    private double ExperimentalTetherDragRegionTopDip =>
        (_paperChrome?.Margin.Top ?? WindowChromeMargin) +
        (_paperChrome?.BorderThickness.Top ?? 1) +
        (_topBar?.Margin.Top ?? 3);

    private double ExperimentalTetherDragRegionHeightDip =>
        _topBar != null &&
        double.IsFinite(_topBar.Height) &&
        _topBar.Height > 0
            ? _topBar.Height
            : TitleBarHeight;

    internal bool HasExperimentalWindowAttachment =>
        _experimentalWindowAttachment != null;

    internal bool HasExperimentalExternalWindowAttachment =>
        _experimentalWindowAttachment?.TargetKind ==
        ExperimentalAttachmentTargetKind.ExternalWindow;

    internal bool HasExperimentalTetherCapsuleSurface =>
        _experimentalTetherCapsule?.IsVisible == true;

    internal bool IsExperimentalTetherPresentationSuppressed =>
        _experimentalTetherPresentationSuppressed;

    private bool HasExperimentalCapsuleMagnet =>
        _experimentalWindowAttachment?.Owner ==
        ExperimentalAttachmentOwner.CapsuleMagnet;

    private bool HasExperimentalWindowTether =>
        _experimentalWindowAttachment?.Owner ==
        ExperimentalAttachmentOwner.WindowTether;

    internal bool SuppressesExpandedDeepCapsuleSlot =>
        HasExperimentalWindowTether ||
        _experimentalAttachmentFormContinuation?.Session.Owner ==
            ExperimentalAttachmentOwner.WindowTether;

    internal bool TracksExperimentalExternalWindow(IntPtr handle) =>
        handle != IntPtr.Zero &&
        _experimentalWindowAttachment is
        {
            TargetKind:
                ExperimentalAttachmentTargetKind.ExternalWindow
        } session &&
        session.ExternalWindow.Handle == handle;

    private void SetExperimentalWindowAttachment(
        ExperimentalWindowAttachmentSession? session)
    {
        var trackedExternalWindow =
            HasExperimentalExternalWindowAttachment;
        _experimentalWindowAttachment = session;
        if (trackedExternalWindow !=
            HasExperimentalExternalWindowAttachment)
        {
            _controller.NotifyExperimentalWindowAttachmentChanged();
            if (HasExperimentalExternalWindowAttachment)
            {
                _controller.RequestExperimentalWindowFrames();
            }
        }
    }

    internal bool RefreshExperimentalAttachmentFrame(
        out IntPtr targetHandle,
        out bool changed)
    {
        targetHandle = IntPtr.Zero;
        changed = false;
        var session = _experimentalWindowAttachment;
        if (session == null ||
            session.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow)
        {
            return false;
        }

        targetHandle = session.ExternalWindow.Handle;
        var hasSnapshot =
            ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot);
        var targetAvailable =
            hasSnapshot && snapshot.IsUsableTarget;
        if (!targetAvailable)
        {
            if (session.Owner ==
                ExperimentalAttachmentOwner.CapsuleMagnet)
            {
                DetachExperimentalWindowAttachment(
                    savePosition: true);
                changed = true;
                return false;
            }

            if (hasSnapshot)
            {
                SuppressExperimentalTetherPresentation(snapshot);
            }
            return _experimentalWindowAttachment != null;
        }

        var targetChanged =
            snapshot.Bounds != session.LastTargetBounds ||
            !string.Equals(
                snapshot.Title,
                session.TargetTitle,
                StringComparison.Ordinal);
        if (targetChanged)
        {
            ReconcileExperimentalWindowAttachment(snapshot);
        }

        var presentationChanged = false;
        if (session.Owner == ExperimentalAttachmentOwner.WindowTether &&
            _experimentalTetherPresentationSuppressed)
        {
            RestoreExperimentalTetherPresentation();
            presentationChanged = true;
        }

        changed =
            targetChanged ||
            presentationChanged ||
            AdvanceExperimentalCapsuleFollowTransition();
        return _experimentalWindowAttachment != null;
    }

    private void DetachExperimentalAttachmentBeforeUserDrag()
    {
        if (_experimentalWindowAttachment != null)
        {
            DetachExperimentalWindowAttachment(savePosition: false);
        }
    }

    private bool TryPlanExperimentalCapsuleMagnet(
        out ExperimentalAttachmentPlan plan)
    {
        plan = default;
        if (!_controller.State.ExperimentalCapsuleMagnetism ||
            !_paper.IsCollapsed ||
            HasDeepCapsuleSlotPlacement ||
            IsPaperFormTransitioning ||
            !IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var capsuleBounds))
        {
            return false;
        }

        var center = new DeviceScreenPoint(
            capsuleBounds.Left + capsuleBounds.Width / 2.0,
            capsuleBounds.Top + capsuleBounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                center,
                this,
                out var monitor))
        {
            return false;
        }

        var externalTargets =
            _controller.State.ExperimentalCapsuleMagnetWindowEdges
                ? ExternalWindowNative.EnumerateTargets(maximumCount: 40)
                : Array.Empty<ExternalWindowSnapshot>();
        return ExperimentalWindowAttachmentGeometry.TryPlanCapsuleMagnet(
                capsuleBounds,
                monitor,
                externalTargets,
                _controller.State.ExperimentalCapsuleMagnetScreenEdges,
                _controller.State.ExperimentalCapsuleMagnetWindowEdges,
                _controller.State.ExperimentalCapsuleMagnetDistance,
                ExperimentalWindowAttachmentOptions.DefaultWindowGap,
                out plan);
    }

    private void BeginExperimentalCapsuleMagnetDragPreview()
    {
        EndExperimentalCapsuleMagnetDragPreview();
        if (!_controller.State.ExperimentalCapsuleMagnetism ||
            !_paper.IsCollapsed ||
            HasDeepCapsuleSlotPlacement ||
            IsPaperFormTransitioning ||
            !IsVisible)
        {
            return;
        }

        _experimentalCapsuleMagnetDragPreviewActive = true;
        _experimentalCapsuleMagnetPreviewTimestamp = 0;
        UpdateExperimentalCapsuleMagnetDragPreview(force: true);
    }

    private void UpdateExperimentalCapsuleMagnetDragPreview(
        bool force = false)
    {
        if (!_experimentalCapsuleMagnetDragPreviewActive)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force &&
            _experimentalCapsuleMagnetPreviewTimestamp != 0 &&
            (now - _experimentalCapsuleMagnetPreviewTimestamp) * 1000.0 /
                Stopwatch.Frequency < 32)
        {
            return;
        }
        _experimentalCapsuleMagnetPreviewTimestamp = now;

        if (!TryPlanExperimentalCapsuleMagnet(out var plan))
        {
            CloseExperimentalCapsuleMagnetPreview();
            _experimentalCapsuleMagnetPreviewPlan = null;
            return;
        }

        var emphasize =
            !_experimentalCapsuleMagnetPreviewPlan.HasValue ||
            !IsSameExperimentalMagnetPreviewTarget(
                _experimentalCapsuleMagnetPreviewPlan.Value,
                plan);
        _experimentalCapsuleMagnetPreviewPlan = plan;
        var preview = _experimentalCapsuleMagnetPreview ??=
            new ExperimentalAttachmentPreviewWindow();
        preview.ShowAt(
            plan.WindowBounds,
            _controller.FullscreenAvoidanceWindowFor(this),
            emphasize,
            _controller.State.EnableAnimations);
        if (emphasize &&
            _controller.State.EnableAnimations &&
            _capsuleShell != null)
        {
            AnimationHelper.QuickBounce(
                _capsuleShell,
                scale: 1.025,
                duration: 70);
        }
    }

    private void TryAttachExperimentalCapsuleMagnetAfterDrag()
    {
        var hasPlan = TryPlanExperimentalCapsuleMagnet(out var plan);
        EndExperimentalCapsuleMagnetDragPreview();
        if (!hasPlan)
        {
            return;
        }

        SetExperimentalWindowAttachment(plan.Session);
        if (plan.Session.TargetKind ==
                ExperimentalAttachmentTargetKind.ExternalWindow &&
            ExternalWindowNative.TryGetSnapshot(
                plan.Session.ExternalWindow,
                out var target))
        {
            ReconcileExperimentalWindowAttachment(
                target,
                animateCapsulePresentation: true);
        }
        else
        {
            ClearExperimentalCapsuleFollowPresentation();
            ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        }
        SaveGeometryForCurrentPresentation();
        RefreshExperimentalAttachmentMenus();
        if (_controller.State.EnableAnimations && _capsuleShell != null)
        {
            AnimationHelper.QuickBounce(
                _capsuleShell,
                scale: 1.04,
                duration: 90);
        }
    }

    private void EndExperimentalCapsuleMagnetDragPreview()
    {
        _experimentalCapsuleMagnetDragPreviewActive = false;
        _experimentalCapsuleMagnetPreviewTimestamp = 0;
        _experimentalCapsuleMagnetPreviewPlan = null;
        CloseExperimentalCapsuleMagnetPreview();
    }

    private void CloseExperimentalCapsuleMagnetPreview()
    {
        var preview = _experimentalCapsuleMagnetPreview;
        _experimentalCapsuleMagnetPreview = null;
        preview?.CloseForOwner();
    }

    private static bool IsSameExperimentalMagnetPreviewTarget(
        ExperimentalAttachmentPlan first,
        ExperimentalAttachmentPlan second)
    {
        var left = first.Session;
        var right = second.Session;
        return left.TargetKind == right.TargetKind &&
            left.Edge == right.Edge &&
            left.InsideTarget == right.InsideTarget &&
            left.ExternalWindow == right.ExternalWindow &&
            string.Equals(
                left.MonitorDeviceName,
                right.MonitorDeviceName,
                StringComparison.Ordinal);
    }

    private bool AttachExperimentalWindowTether(
        ExternalWindowIdentity identity) =>
        AttachExperimentalWindowTether(
            identity,
            preferredEdge: null);

    private bool AttachExperimentalWindowTether(
        ExternalWindowIdentity identity,
        ExperimentalAttachmentEdge? preferredEdge)
    {
        if (!_controller.State.ExperimentalWindowTethering ||
            _paper.IsCollapsed ||
            IsPaperFormTransitioning ||
            WindowState != System.Windows.WindowState.Normal ||
            _isSnappedPresentation ||
            !IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var paperBounds) ||
            !ExternalWindowNative.TryGetSnapshot(identity, out var target) ||
            !target.IsUsableTarget)
        {
            return false;
        }

        var targetCenter = new DeviceScreenPoint(
            target.Bounds.Left + target.Bounds.Width / 2.0,
            target.Bounds.Top + target.Bounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                targetCenter,
                this,
                out var monitor) ||
            !ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                paperBounds,
                target,
                monitor,
                preferredEdge.HasValue
                    ? ExperimentalWindowTetherOption(preferredEdge.Value)
                    : _controller.State
                        .ExperimentalWindowTetherPreferredEdge,
                _controller.State.ExperimentalWindowTetherGap,
                ExperimentalTetherDragRegionTopDip,
                ExperimentalTetherDragRegionHeightDip,
                out var plan))
        {
            return false;
        }

        DetachExperimentalWindowAttachment(savePosition: false);
        SetExperimentalWindowAttachment(plan.Session);
        _experimentalTetherReplanPending = false;
        // A tethered expanded paper owns its external-window edge instead of an
        // expanded edge-queue reservation. Reconcile through the existing queue
        // coordinator so the edge capsule keeps a single source of truth.
        _controller.ArrangeDeepCapsules(animate: false);
        ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        SaveGeometryForCurrentPresentation();
        RefreshExperimentalAttachmentMenus();
        return true;
    }

    private static string ExperimentalWindowTetherOption(
        ExperimentalAttachmentEdge edge) =>
        edge switch
        {
            ExperimentalAttachmentEdge.Left =>
                ExperimentalWindowTetherOptions.Left,
            ExperimentalAttachmentEdge.Right =>
                ExperimentalWindowTetherOptions.Right,
            ExperimentalAttachmentEdge.Top =>
                ExperimentalWindowTetherOptions.Top,
            _ => ExperimentalWindowTetherOptions.Bottom
        };

    private void PrepareExperimentalAttachmentForFormTransition(
        bool collapsed)
    {
        if (_experimentalAttachmentFormContinuation.HasValue)
        {
            return;
        }

        var session = _experimentalWindowAttachment;
        var continuesFromMagnet =
            !collapsed &&
            session is
            {
                Owner: ExperimentalAttachmentOwner.CapsuleMagnet,
                TargetKind:
                    ExperimentalAttachmentTargetKind.ExternalWindow
            } &&
            _controller.State.ExperimentalCapsuleMagnetism &&
            _controller.State.ExperimentalCapsuleMagnetWindowEdges &&
            _controller.State.ExperimentalWindowTethering;
        var continuesFromWindowTether =
            session is
            {
                Owner: ExperimentalAttachmentOwner.WindowTether,
                TargetKind:
                    ExperimentalAttachmentTargetKind.ExternalWindow
            } &&
            _controller.State.ExperimentalWindowTethering;
        if (!continuesFromMagnet && !continuesFromWindowTether)
        {
            DetachExperimentalWindowAttachment(savePosition: false);
            return;
        }

        var continuation =
            new ExperimentalAttachmentFormContinuation(session!);
        DetachExperimentalWindowAttachment(
            savePosition: false,
            reconcileDeepCapsules: false);
        _experimentalAttachmentFormContinuation = continuation;
    }

    private void RestoreExperimentalAttachmentAfterFormTransition(
        bool collapsed)
    {
        var continuation =
            _experimentalAttachmentFormContinuation;
        _experimentalAttachmentFormContinuation = null;
        if (!continuation.HasValue)
        {
            return;
        }

        if (!collapsed)
        {
            RestoreExperimentalWindowTetherAfterFormTransition(
                continuation.Value);
            return;
        }

        if (continuation.Value.Session.Owner ==
            ExperimentalAttachmentOwner.WindowTether)
        {
            RestoreExperimentalWindowTetherCapsuleAfterFormTransition(
                continuation.Value);
            return;
        }

        RestoreExperimentalCapsuleMagnetAfterInterruptedTransition(
            continuation.Value);
    }

    private void RestoreExperimentalWindowTetherAfterFormTransition(
        ExperimentalAttachmentFormContinuation continuation)
    {
        var source = continuation.Session;
        if (!_controller.State.ExperimentalWindowTethering ||
            source.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow ||
            !ExternalWindowNative.TryGetSnapshot(
                source.ExternalWindow,
                out var target))
        {
            return;
        }

        if (target.IsUsableTarget)
        {
            _ = AttachExperimentalWindowTether(
                source.ExternalWindow,
                source.Edge);
            return;
        }

        SetExperimentalWindowAttachment(source with
        {
            Owner = ExperimentalAttachmentOwner.WindowTether,
            LastTargetBounds = target.Bounds.IsEmpty
                ? source.LastTargetBounds
                : target.Bounds,
            TargetTitle = target.Title
        });
        _experimentalTetherReplanPending = false;
        SuppressExperimentalTetherPresentation(target);
        RefreshExperimentalAttachmentMenus();
    }

    private void RestoreExperimentalWindowTetherCapsuleAfterFormTransition(
        ExperimentalAttachmentFormContinuation continuation)
    {
        var source = continuation.Session;
        if (!_controller.State.ExperimentalWindowTethering ||
            source.Owner != ExperimentalAttachmentOwner.WindowTether ||
            source.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow ||
            !_paper.IsCollapsed ||
            IsPaperFormTransitioning ||
            !IsVisible ||
            !ExternalWindowNative.TryGetSnapshot(
                source.ExternalWindow,
                out var target))
        {
            return;
        }

        SetExperimentalWindowAttachment(source with
        {
            Owner = ExperimentalAttachmentOwner.WindowTether,
            LastTargetBounds = target.Bounds.IsEmpty
                ? source.LastTargetBounds
                : target.Bounds,
            TargetTitle = target.Title
        });
        _experimentalTetherReplanPending = false;
        if (target.IsUsableTarget)
        {
            ReconcileExperimentalWindowAttachment(
                target,
                animateCapsulePresentation: false);
        }
        else
        {
            SuppressExperimentalTetherPresentation(target);
        }
        RefreshExperimentalAttachmentMenus();
    }

    private void RestoreExperimentalCapsuleMagnetAfterInterruptedTransition(
        ExperimentalAttachmentFormContinuation continuation)
    {
        if (!_controller.State.ExperimentalCapsuleMagnetism ||
            !_controller.State.ExperimentalCapsuleMagnetWindowEdges ||
            !_paper.IsCollapsed ||
            HasDeepCapsuleSlotPlacement ||
            IsPaperFormTransitioning ||
            !IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(
                this,
                out var capsuleBounds) ||
            !ExternalWindowNative.TryGetSnapshot(
                continuation.Session.ExternalWindow,
                out var target) ||
            !ExperimentalWindowAttachmentGeometry
                .TryPlanCapsuleMagnetForExternalTarget(
                    capsuleBounds,
                    target,
                    continuation.Session.Edge,
                    ExperimentalWindowAttachmentOptions
                        .DefaultWindowGap,
                    out var plan))
        {
            return;
        }

        SetExperimentalWindowAttachment(plan.Session);
        ReconcileExperimentalWindowAttachment(
            target,
            animateCapsulePresentation: false);
        RefreshExperimentalAttachmentMenus();
    }

    internal void HandleExternalWindowEvent(ExternalWindowEvent windowEvent)
    {
        var session = _experimentalWindowAttachment;
        if (session == null ||
            session.TargetKind != ExperimentalAttachmentTargetKind.ExternalWindow)
        {
            return;
        }

        var isWindowTether =
            session.Owner == ExperimentalAttachmentOwner.WindowTether;
        if (isWindowTether &&
            (windowEvent.Kind & ExternalWindowEventKind.Foreground) != 0 &&
            ExternalWindowNative.IsSameProcess(
                session.ExternalWindow,
                windowEvent.Handle) &&
            ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var foregroundTarget) &&
            foregroundTarget.IsUsableTarget)
        {
            ReconcileExperimentalWindowAttachment(foregroundTarget);
            if (_experimentalTetherPresentationSuppressed)
            {
                RestoreExperimentalTetherPresentation();
            }
            BringExperimentalTetherAboveTargetNoActivate();
        }

        var isDesktopSwitch =
            (windowEvent.Kind & ExternalWindowEventKind.DesktopSwitched) != 0;
        if (!isDesktopSwitch &&
            session.ExternalWindow.Handle != windowEvent.Handle)
        {
            return;
        }

        if ((windowEvent.Kind & ExternalWindowEventKind.Destroyed) != 0 ||
            !ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        var targetUnavailable =
            snapshot.IsMinimized ||
            snapshot.IsCloaked ||
            !snapshot.IsVisible;
        var targetBecameUnavailable =
            (windowEvent.Kind &
             (ExternalWindowEventKind.MinimizeStarted |
              ExternalWindowEventKind.Cloaked)) != 0;
        if (isWindowTether &&
            (targetUnavailable || targetBecameUnavailable))
        {
            SuppressExperimentalTetherPresentation(snapshot);
            return;
        }

        if (targetUnavailable)
        {
            if (session.Owner == ExperimentalAttachmentOwner.CapsuleMagnet)
            {
                DetachExperimentalWindowAttachment(savePosition: true);
            }
            return;
        }

        ReconcileExperimentalWindowAttachment(snapshot);
        if (isWindowTether)
        {
            RestoreExperimentalTetherPresentation();
        }
    }

    internal void RefreshExperimentalAttachmentForDisplayMetrics()
    {
        var session = _experimentalWindowAttachment;
        if (session == null)
        {
            return;
        }

        if (session.TargetKind == ExperimentalAttachmentTargetKind.Screen)
        {
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    session.MonitorDeviceName,
                    this,
                    out var monitor) ||
                !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds))
            {
                DetachExperimentalWindowAttachment(savePosition: true);
                return;
            }

            var desired = ExperimentalWindowAttachmentGeometry.Resolve(
                session,
                monitor.WorkArea,
                currentBounds,
                session.Edge is
                    ExperimentalAttachmentEdge.Left or ExperimentalAttachmentEdge.Right
                        ? monitor.DpiScaleX
                        : monitor.DpiScaleY);
            SetExperimentalWindowAttachment(session with
            {
                LastTargetBounds = monitor.WorkArea
            });
            ApplyExperimentalAttachmentBounds(desired);
            return;
        }

        HandleExternalWindowEvent(new ExternalWindowEvent(
            session.ExternalWindow.Handle,
            ExternalWindowEventKind.Location));
    }

    internal void DisableExperimentalCapsuleMagnet()
    {
        EndExperimentalCapsuleMagnetDragPreview();
        if (_experimentalAttachmentFormContinuation?.Session.Owner ==
            ExperimentalAttachmentOwner.CapsuleMagnet)
        {
            _experimentalAttachmentFormContinuation = null;
        }
        if (HasExperimentalCapsuleMagnet)
        {
            DetachExperimentalWindowAttachment(savePosition: true);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisableExperimentalWindowTether()
    {
        EndTopBarDragGesture(
            commit: false,
            TopBarDragKind.WindowBinding);
        if (_experimentalAttachmentFormContinuation?.Session.Owner ==
            ExperimentalAttachmentOwner.WindowTether)
        {
            _experimentalAttachmentFormContinuation = null;
        }
        if (HasExperimentalWindowTether)
        {
            DetachExperimentalWindowAttachment(savePosition: true);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisableExperimentalTetherVisibilityLink()
    {
        RefreshExperimentalTetherVisibilityOptions();
    }

    internal void RefreshExperimentalTetherVisibilityOptions()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether ||
            !ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot))
        {
            return;
        }

        if (snapshot.IsMinimized ||
            snapshot.IsCloaked ||
            !snapshot.IsVisible)
        {
            SuppressExperimentalTetherPresentation(snapshot);
            return;
        }

        RestoreExperimentalTetherPresentation();
    }

    internal void RefreshExperimentalWindowTetherOptions()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether ||
            _paper.IsCollapsed)
        {
            return;
        }

        if (!ExternalWindowNative.TryGetSnapshot(
                session.ExternalWindow,
                out var snapshot) ||
            !snapshot.IsUsableTarget ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds) ||
            !TryGetTargetMonitor(snapshot, out var monitor) ||
            !ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                currentBounds,
                snapshot,
                monitor,
                _controller.State.ExperimentalWindowTetherPreferredEdge,
                _controller.State.ExperimentalWindowTetherGap,
                ExperimentalTetherDragRegionTopDip,
                ExperimentalTetherDragRegionHeightDip,
                out var plan))
        {
            _experimentalTetherReplanPending = true;
            return;
        }

        SetExperimentalWindowAttachment(plan.Session);
        _experimentalTetherReplanPending = false;
        ApplyExperimentalAttachmentBounds(plan.WindowBounds);
        SaveGeometryForCurrentPresentation();
    }

    internal void DetachExperimentalWindowAttachment(
        bool savePosition,
        bool reconcileDeepCapsules = true)
    {
        _experimentalAttachmentFormContinuation = null;
        var session = _experimentalWindowAttachment;
        if (session == null)
        {
            return;
        }

        var wasWindowTether =
            session.Owner == ExperimentalAttachmentOwner.WindowTether;
        if (wasWindowTether)
        {
            EndTitleBarDragGesture();
            RestoreExperimentalTetherPresentation();
        }
        if (!wasWindowTether || _paper.IsCollapsed)
        {
            PrepareExperimentalCapsuleFollowForDetach();
        }
        SetExperimentalWindowAttachment(null);
        _experimentalTetherReplanPending = false;
        ClearExperimentalCapsuleFollowPresentation();
        if (savePosition &&
            !HasDeepCapsuleSlotPlacement &&
            IsVisible)
        {
            SaveGeometryForCurrentPresentation();
        }
        if (wasWindowTether && reconcileDeepCapsules)
        {
            // Clearing the session makes the paper eligible for its configured
            // expanded edge slot again. Let the queue coordinator restore it.
            _controller.ArrangeDeepCapsules(animate: false);
        }
        RefreshExperimentalAttachmentMenus();
    }

    internal void DisposeExperimentalWindowAttachment()
    {
        EndExperimentalCapsuleMagnetDragPreview();
        EndTopBarDragGesture(
            commit: false,
            TopBarDragKind.WindowBinding);
        CancelExperimentalTetherPresentation(showMain: false);
        SetExperimentalWindowAttachment(null);
        _experimentalAttachmentFormContinuation = null;
        _experimentalTetherReplanPending = false;
        ClearExperimentalCapsuleFollowPresentation();
    }

    internal void RestoreExperimentalTetherPresentationForExplicitShow()
    {
        if (!_experimentalTetherPresentationSuppressed)
        {
            return;
        }

        // Explicit user show requests break a temporarily hidden binding instead
        // of reviving the paper while its target is still unavailable.
        DetachExperimentalWindowAttachment(savePosition: true);
    }

    private void ReconcileExperimentalWindowAttachment(
        ExternalWindowSnapshot snapshot,
        bool animateCapsulePresentation = false)
    {
        var session = _experimentalWindowAttachment;
        if (session == null ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds))
        {
            return;
        }

        var desired = ExperimentalWindowAttachmentGeometry.Resolve(
            session,
            snapshot.Bounds,
            currentBounds,
            snapshot.DpiScale);
        var usesCapsuleFollow = false;
        if (_paper.IsCollapsed &&
            session.TargetKind ==
                ExperimentalAttachmentTargetKind.ExternalWindow &&
            TryGetTargetMonitor(snapshot, out var capsuleMonitor))
        {
            var followPlan =
                ExperimentalWindowAttachmentGeometry
                    .ResolveCapsuleFollow(
                        session,
                        snapshot.Bounds,
                        currentBounds,
                        capsuleMonitor,
                        WindowWorkAreaHelper
                            .ConnectedMonitorGeometries(),
                        reveal:
                            _capsuleShell?.IsMouseOver == true);
            desired = followPlan.Bounds;
            usesCapsuleFollow = true;
            SetExperimentalCapsuleFollowPresentation(
                followPlan,
                animateCapsulePresentation);
        }
        else if (session.Owner == ExperimentalAttachmentOwner.WindowTether &&
            TryGetTargetMonitor(snapshot, out var monitor))
        {
            var shouldReplan =
                _experimentalTetherReplanPending ||
                !ExperimentalWindowAttachmentGeometry
                    .KeepsTetherHandleReachable(
                        desired,
                        monitor.WorkArea,
                        Math.Max(monitor.DpiScaleX, monitor.DpiScaleY),
                        ExperimentalTetherDragRegionTopDip,
                        ExperimentalTetherDragRegionHeightDip);
            if (shouldReplan &&
                ExperimentalWindowAttachmentGeometry.TryPlanWindowTether(
                    currentBounds,
                    snapshot,
                    monitor,
                    _controller.State.ExperimentalWindowTetherPreferredEdge,
                    _controller.State.ExperimentalWindowTetherGap,
                    ExperimentalTetherDragRegionTopDip,
                    ExperimentalTetherDragRegionHeightDip,
                    out var replanned))
            {
                session = replanned.Session;
                desired = replanned.WindowBounds;
                _experimentalTetherReplanPending = false;
            }
            else if (shouldReplan)
            {
                desired =
                    ExperimentalWindowAttachmentGeometry
                        .KeepTetherHandleVisible(
                            desired,
                            monitor.WorkArea,
                            Math.Max(monitor.DpiScaleX, monitor.DpiScaleY),
                            ExperimentalTetherDragRegionTopDip,
                            ExperimentalTetherDragRegionHeightDip);
            }
            ClearExperimentalCapsuleFollowPresentation();
        }
        else
        {
            ClearExperimentalCapsuleFollowPresentation();
        }
        SetExperimentalWindowAttachment(session with
        {
            LastTargetBounds = snapshot.Bounds,
            TargetTitle = snapshot.Title
        });
        if (!usesCapsuleFollow &&
            session.Owner != ExperimentalAttachmentOwner.CapsuleMagnet)
        {
            ApplyExperimentalAttachmentBounds(desired);
        }
    }

    private bool TryGetTargetMonitor(
        ExternalWindowSnapshot snapshot,
        out MonitorGeometry monitor)
    {
        var center = new DeviceScreenPoint(
            snapshot.Bounds.Left + snapshot.Bounds.Width / 2.0,
            snapshot.Bounds.Top + snapshot.Bounds.Height / 2.0);
        return WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            center,
            this,
            out monitor);
    }

    private void ApplyExperimentalAttachmentBounds(DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        MoveWindowWithoutGeometrySave(() =>
            WindowNative.TryMoveWindowDevicePosition(
                this,
                new DeviceScreenPoint(bounds.Left, bounds.Top)));
    }

    private void SuppressExperimentalTetherPresentation(
        ExternalWindowSnapshot snapshot)
    {
        if (_experimentalTetherPresentationSuppressed ||
            !HasExperimentalWindowTether ||
            !_paper.IsVisible ||
            !IsVisible ||
            _windowLifecycle != PaperWindowLifecycleState.Alive)
        {
            return;
        }

        if (snapshot.IsUsableTarget)
        {
            ReconcileExperimentalWindowAttachment(snapshot);
        }
        SaveGeometryForCurrentPresentation();
        _experimentalTetherPresentationSuppressed = true;
        CloseExperimentalTetherCapsule();
        MoveWindowWithoutGeometrySave(Hide);
        ReleaseHiddenNoteImages();
    }

    private void ShowExperimentalTetherCapsule()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether ||
            !_experimentalTetherPresentationSuppressed)
        {
            return;
        }

        CloseExperimentalTetherCapsule();
        var restingOpacity =
            _controller.State.ExperimentalRestingCapsuleOpacity
                ? ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                    ExperimentalOpacityLevels.DefaultRestingCapsule)
                : 1.0;
        var capsule = new ExperimentalTetherCapsuleWindow(
            Strings.Format(
                "LabsTetherCapsuleLabelFormat",
                _controller.PaperCapsuleTitle(_paper)),
            Strings.Format(
                "LabsTetherCapsuleTargetTipFormat",
                session.TargetTitle),
            ActivateExperimentalTetherTarget,
            normalTopmost: true,
            restingOpacity: restingOpacity);
        capsule.UpdateRestingOpacity(
            restingOpacity,
            _controller.State.ExperimentalRestingCapsuleOpacity &&
            _controller.State.ExperimentalRestingCapsuleOpacityAlways);
        _experimentalTetherCapsule = capsule;
        capsule.UnexpectedlyClosed += (_, _) =>
        {
            if (!ReferenceEquals(_experimentalTetherCapsule, capsule))
            {
                return;
            }

            _experimentalTetherCapsule = null;
            RestoreExperimentalTetherPresentation();
        };
        capsule.SetExperimentalPassive(IsExperimentalPassive);

        var anchorBounds = WindowNative.TryGetWindowDeviceBounds(
                this,
                out var currentBounds)
            ? currentBounds
            : session.LastTargetBounds;
        capsule.SetFullscreenAvoidance(
            _controller.FullscreenAvoidanceWindowFor(this));
        capsule.ShowAt(anchorBounds);
        capsule.SetFullscreenAvoidance(
            _controller.FullscreenAvoidanceWindowFor(capsule));
    }

    private void ActivateExperimentalTetherTarget()
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner != ExperimentalAttachmentOwner.WindowTether)
        {
            CancelExperimentalTetherPresentation(showMain: true);
            return;
        }

        if (!ExternalWindowNative.RestoreAndActivate(
                session.ExternalWindow))
        {
            DetachExperimentalWindowAttachment(savePosition: true);
            return;
        }

        RestoreExperimentalTetherPresentation();
    }

    private void RestoreExperimentalTetherPresentation()
    {
        CancelExperimentalTetherPresentation(showMain: true);
    }

    private void CancelExperimentalTetherPresentation(bool showMain)
    {
        var wasSuppressed = _experimentalTetherPresentationSuppressed;
        _experimentalTetherPresentationSuppressed = false;
        CloseExperimentalTetherCapsule();
        if (!wasSuppressed ||
            !showMain ||
            !_paper.IsVisible ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsVisible)
        {
            return;
        }

        var showActivated = ShowActivated;
        ShowActivated = false;
        try
        {
            MoveWindowWithoutGeometrySave(Show);
        }
        finally
        {
            ShowActivated = showActivated;
        }
        PrepareForShow();
        RefreshEffectiveTopmost();
        if (!IsExperimentalPassive)
        {
            WindowNative.BringToFrontNoActivate(this);
        }
    }

    private void BringExperimentalTetherAboveTargetNoActivate()
    {
        if (!HasExperimentalWindowTether ||
            _paper.IsCollapsed ||
            _experimentalTetherPresentationSuppressed ||
            !IsVisible ||
            IsExperimentalPassive)
        {
            return;
        }

        RefreshEffectiveTopmost();
        WindowNative.BringToFrontNoActivate(this);
    }

    private void CloseExperimentalTetherCapsule()
    {
        var capsule = _experimentalTetherCapsule;
        _experimentalTetherCapsule = null;
        capsule?.CloseForOwner();
    }

    private void RefreshExperimentalAttachmentMenus()
    {
        if (!_isShellBuilt)
        {
            return;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
        RefreshWindowBindingButton();
    }

    internal void RefreshExperimentalAttachmentMenu()
    {
        RefreshExperimentalAttachmentMenus();
    }

}
