namespace PaperTodo;

internal enum EdgeCapsuleMotionKind
{
    Snap,
    Animate,
    Preserve
}

internal enum EdgeCapsuleTransitionReason
{
    State,
    Pointer,
    Preview,
    Placement,
    Measure,
    DisplayMetrics,
    Drag,
    Retraction,
    FloatingTransfer
}

internal enum EdgeCapsuleSurfaceKind
{
    Hidden,
    DockedResting,
    DockedHovered,
    DockedActive,
    DockedPreview,
    DockedSuppressed,
    DockedRetracted,
    DockedRetracting,
    FloatingFree
}

internal readonly record struct EdgeCapsuleMotion(
    EdgeCapsuleMotionKind Kind,
    int DurationMilliseconds,
    EdgeCapsuleTransitionReason Reason)
{
    public static EdgeCapsuleMotion Snap(EdgeCapsuleTransitionReason reason) =>
        new(EdgeCapsuleMotionKind.Snap, 0, reason);

    public static EdgeCapsuleMotion Animate(
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds) =>
        new(EdgeCapsuleMotionKind.Animate, Math.Max(1, durationMilliseconds), reason);

    public static EdgeCapsuleMotion Preserve(EdgeCapsuleTransitionReason reason) =>
        new(EdgeCapsuleMotionKind.Preserve, 0, reason);
}

/// <summary>
/// Environment facts and stable presentation options captured for the target monitor. This value
/// contains no reducer state decisions; the shape planner owns all Resting/Hover/Active/Floating policy.
/// </summary>
internal readonly record struct EdgeCapsuleLayoutSnapshot(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    double NormalTopDip,
    double MasterTopDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HeightDip,
    double PreviewWidthDip,
    double PreviewHeightDip,
    bool CloseSegmentActsAsContent,
    double RestingContentOpacity,
    double? ForcedContentOpacity,
    double HostCapacityWidthDip = 0,
    double HostCapacityHeightDip = 0,
    double ExpandedWidthDip = 0,
    bool HideRestingTitle = false)
{
    public bool IsUsable =>
        !Monitor.WorkArea.IsEmpty &&
        RestingWidthDip > 0 &&
        HeightDip > 0 &&
        PreviewWidthDip > 0 &&
        PreviewHeightDip > 0;
}

internal readonly record struct EdgeCapsuleFloatingShape(
    bool Visible,
    EdgeCapsuleSurfaceKind Kind,
    double WindowWidthDip,
    double WindowHeightDip,
    double BodyHeightDip,
    double CornerRadiusDip,
    bool OutlineVisible)
{
    public static EdgeCapsuleFloatingShape Hidden => new(
        false,
        EdgeCapsuleSurfaceKind.Hidden,
        0,
        0,
        0,
        0,
        false);
}

/// <summary>
/// One immutable docked target. Bounds is the visible capsule rectangle and HostBounds is the
/// stable per-paper bounded HWND capacity. Body width is distinct from visible
/// width; the only permitted close segment is visible width minus BodyWindowWidthDevice.
/// </summary>
internal readonly record struct EdgeCapsuleTargetPresentation(
    bool Visible,
    EdgeCapsuleSurfaceKind Surface,
    DeviceScreenRect Bounds,
    DeviceScreenRect HostBounds,
    DeviceScreenRect InteractiveBounds,
    EdgeCapsuleEdge Edge,
    int BodyWindowWidthDevice,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    double MaximumCloseWidthDip,
    double Opacity,
    double ContentOpacity,
    bool OutlineVisible,
    bool IsHitTestVisible,
    bool CloseSegmentActsAsContent,
    bool TitleVisible = true)
{
    public static EdgeCapsuleTargetPresentation Hidden => new(
        false,
        EdgeCapsuleSurfaceKind.Hidden,
        default,
        default,
        default,
        EdgeCapsuleEdge.Right,
        0,
        0,
        1,
        1,
        0,
        0,
        0,
        false,
        false,
        false);

    public EdgeCapsulePresentationFrame ToFrame() => new(
        Visible,
        Surface,
        Bounds,
        HostBounds,
        InteractiveBounds,
        Edge,
        BodyWindowWidthDevice,
        WallDeviceX,
        DpiScaleX,
        DpiScaleY,
        MaximumCloseWidthDip,
        Opacity,
        ContentOpacity,
        OutlineVisible,
        IsHitTestVisible,
        CloseSegmentActsAsContent,
        TitleVisible);
}

internal readonly record struct EdgeCapsulePresentationPlan(
    EdgeCapsuleTargetPresentation Docked,
    EdgeCapsuleFloatingShape Floating)
{
    public static EdgeCapsulePresentationPlan Hidden => new(
        EdgeCapsuleTargetPresentation.Hidden,
        EdgeCapsuleFloatingShape.Hidden);
}

/// <summary>
/// Complete Host.Apply contract. HostBounds is the real native endpoint while Bounds is the visual
/// capsule. Bounds remains inside HostBounds at settle and during WPF morphs; an active queue
/// proxy overrides only the global screen translation. Interactive bounds,
/// body/close segmentation, opacity and input state remain one immutable frame contract.
/// </summary>
internal readonly record struct EdgeCapsulePresentationFrame(
    bool Visible,
    EdgeCapsuleSurfaceKind Surface,
    DeviceScreenRect Bounds,
    DeviceScreenRect HostBounds,
    DeviceScreenRect InteractiveBounds,
    EdgeCapsuleEdge Edge,
    int BodyWindowWidthDevice,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    double MaximumCloseWidthDip,
    double Opacity,
    double ContentOpacity,
    bool OutlineVisible,
    bool IsHitTestVisible,
    bool CloseSegmentActsAsContent,
    bool TitleVisible = true)
{
    public static EdgeCapsulePresentationFrame Hidden =>
        EdgeCapsuleTargetPresentation.Hidden.ToFrame();

    public bool IsUsable => !Visible || (
        !Bounds.IsEmpty &&
        !HostBounds.IsEmpty &&
        Bounds.Width <= HostBounds.Width &&
        Bounds.Height <= HostBounds.Height &&
        (Edge == EdgeCapsuleEdge.Left
            ? Bounds.Left == WallDeviceX && HostBounds.Left == WallDeviceX
            : Bounds.Right == WallDeviceX && HostBounds.Right == WallDeviceX));
}

internal readonly record struct EdgeCapsuleTransition(
    EdgeCapsulePresentationFrame Start,
    EdgeCapsuleTargetPresentation Target,
    long StartedAtTimestamp,
    long DurationTimestampTicks,
    EdgeCapsuleTransitionReason Reason);

internal readonly record struct EdgeCapsuleTransitionSample(
    EdgeCapsulePresentationFrame Frame,
    bool IsComplete);
