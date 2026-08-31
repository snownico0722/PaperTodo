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
    double HostWidthDip,
    double HeightDip,
    bool CloseSegmentActsAsContent)
{
    public bool IsUsable =>
        !Monitor.WorkArea.IsEmpty &&
        RestingWidthDip > 0 &&
        HostWidthDip > 0 &&
        HeightDip > 0;
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
/// One immutable docked target. Bounds is the real visible capsule rectangle; HostBounds is the
/// stable transparent composition surface that can hold the fully expanded capsule. Body width is
/// distinct from the visible width; the only permitted close segment is visible device width minus
/// BodyWindowWidthDevice.
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
    bool CloseSegmentActsAsContent)
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
        CloseSegmentActsAsContent);
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
/// Complete Host.Apply contract. HostBounds is native capacity while Bounds is the actual visible
/// capsule. Interactive bounds, body/close segmentation, opacity and input state still advance from
/// this one frame; horizontal animation never resizes the native host.
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
    bool CloseSegmentActsAsContent)
{
    public static EdgeCapsulePresentationFrame Hidden =>
        EdgeCapsuleTargetPresentation.Hidden.ToFrame();

    public bool IsUsable => !Visible || (!Bounds.IsEmpty && !HostBounds.IsEmpty);
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
