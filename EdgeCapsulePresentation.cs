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
/// Effect-surface facts captured by PaperWindow. This contains no desired-state decisions: the
/// planner chooses the visible form, close width, master position and interaction state.
/// </summary>
internal readonly record struct EdgeCapsuleLayoutSnapshot(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    double NormalTopDip,
    double MasterTopDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HeightDip)
{
    public bool IsUsable =>
        !Monitor.WorkArea.IsEmpty &&
        RestingWidthDip > 0 &&
        HeightDip > 0;
}

/// <summary>
/// One immutable target truth. No WPF width, native Left or close-column value is allowed to live
/// beside this value as another independently animated target.
/// </summary>
internal readonly record struct EdgeCapsuleTargetPresentation(
    bool Visible,
    DeviceScreenRect Bounds,
    EdgeCapsuleEdge Edge,
    int RestingWidthDevice,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    double MaximumCloseWidthDip,
    double Opacity,
    double ContentOpacity,
    bool OutlineVisible,
    bool IsHitTestVisible)
{
    public static EdgeCapsuleTargetPresentation Hidden => new(
        false,
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
        false);

    public EdgeCapsulePresentationFrame ToFrame() => new(
        Visible,
        Bounds,
        Edge,
        RestingWidthDevice,
        WallDeviceX,
        DpiScaleX,
        DpiScaleY,
        MaximumCloseWidthDip,
        Opacity,
        ContentOpacity,
        OutlineVisible,
        IsHitTestVisible);
}

/// <summary>
/// The complete frame sent to EdgeCapsuleHost.Apply. Applied presentation is stored only as this
/// value; top, width, opacity and interaction cannot be committed as unrelated sideband fields.
/// </summary>
internal readonly record struct EdgeCapsulePresentationFrame(
    bool Visible,
    DeviceScreenRect Bounds,
    EdgeCapsuleEdge Edge,
    int RestingWidthDevice,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY,
    double MaximumCloseWidthDip,
    double Opacity,
    double ContentOpacity,
    bool OutlineVisible,
    bool IsHitTestVisible)
{
    public static EdgeCapsulePresentationFrame Hidden =>
        EdgeCapsuleTargetPresentation.Hidden.ToFrame();

    public bool IsUsable => !Visible || !Bounds.IsEmpty;
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

/// <summary>
/// Pure desired-state to target-layout planner. It is the only place that decides whether the
/// wall-side close segment exists and whether the docked surface is visible or interactive.
/// </summary>
internal static class EdgeCapsuleTargetPlanner
{
    public static EdgeCapsuleTargetPresentation Calculate(
        EdgeCapsuleModel model,
        EdgeCapsuleLayoutSnapshot layout)
    {
        if (model.State.Slot == EdgeCapsuleSlotState.None ||
            !model.Placement.IsPlaced ||
            !layout.IsUsable)
        {
            return EdgeCapsuleTargetPresentation.Hidden;
        }

        var retracted = model.State.Slot is
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractedExpanded or
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded;
        var floating = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering;
        var expanded = !retracted && model.State.Visual is
            EdgeCapsuleVisualState.Hovered or
            EdgeCapsuleVisualState.Active;
        var top = model.DockedDragTopDipOverride ??
            (retracted ? layout.MasterTopDip : layout.NormalTopDip);
        var closeWidth = expanded ? layout.MaximumCloseWidthDip : 0;
        var geometry = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            layout.Monitor,
            layout.Edge,
            top,
            layout.RestingWidthDip,
            closeWidth,
            layout.HeightDip));

        return new EdgeCapsuleTargetPresentation(
            true,
            geometry.Bounds,
            layout.Edge,
            geometry.RestingWidthDevice,
            geometry.WallDeviceX,
            geometry.DpiScaleX,
            geometry.DpiScaleY,
            layout.MaximumCloseWidthDip,
            retracted ? 0 : 1,
            floating ? 0 : 1,
            !retracted && model.State.Visual == EdgeCapsuleVisualState.Active,
            !retracted && !floating);
    }
}

/// <summary>
/// Pure transition policy. It can retarget from the last applied frame; Hover never has to wait
/// for an obsolete transition to finish.
/// </summary>
internal static class EdgeCapsuleTransitionPolicy
{
    public static EdgeCapsuleTransition? Create(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target,
        EdgeCapsuleMotion motion,
        bool transitionAlreadyActive,
        long nowTimestamp,
        long timestampFrequency)
    {
        if (!target.Visible ||
            !applied.Visible ||
            applied.Bounds.IsEmpty ||
            motion.Kind == EdgeCapsuleMotionKind.Snap ||
            (motion.Kind == EdgeCapsuleMotionKind.Preserve && !transitionAlreadyActive) ||
            applied.Edge != target.Edge ||
            applied.WallDeviceX != target.WallDeviceX ||
            Math.Abs(applied.DpiScaleX - target.DpiScaleX) > 0.001 ||
            Math.Abs(applied.DpiScaleY - target.DpiScaleY) > 0.001)
        {
            return null;
        }

        if (FramesMatch(applied, target))
        {
            return null;
        }

        var durationMilliseconds = motion.Kind == EdgeCapsuleMotionKind.Preserve
            ? EdgeCapsuleLayout.SlotMoveMilliseconds
            : Math.Max(1, motion.DurationMilliseconds);
        var durationTicks = Math.Max(
            1,
            (long)Math.Round(timestampFrequency * durationMilliseconds / 1000.0));
        return new EdgeCapsuleTransition(
            applied,
            target,
            nowTimestamp,
            durationTicks,
            motion.Reason);
    }

    public static EdgeCapsuleTransitionSample Sample(
        EdgeCapsuleTransition transition,
        long nowTimestamp)
    {
        var elapsed = Math.Max(0, nowTimestamp - transition.StartedAtTimestamp);
        var rawProgress = Math.Clamp(
            elapsed / (double)Math.Max(1, transition.DurationTimestampTicks),
            0,
            1);
        if (rawProgress >= 1)
        {
            return new EdgeCapsuleTransitionSample(transition.Target.ToFrame(), true);
        }

        var progress = EaseOutCubic(rawProgress);
        var target = transition.Target;
        var start = transition.Start;
        var width = LerpDevice(start.Bounds.Width, target.Bounds.Width, progress);
        var height = LerpDevice(start.Bounds.Height, target.Bounds.Height, progress);
        var top = LerpDevice(start.Bounds.Top, target.Bounds.Top, progress);
        var left = target.Edge == EdgeCapsuleEdge.Left
            ? target.WallDeviceX
            : target.WallDeviceX - width;
        var right = target.Edge == EdgeCapsuleEdge.Left
            ? target.WallDeviceX + width
            : target.WallDeviceX;
        var frame = new EdgeCapsulePresentationFrame(
            true,
            new DeviceScreenRect(left, top, right, top + height),
            target.Edge,
            LerpDevice(start.RestingWidthDevice, target.RestingWidthDevice, progress),
            target.WallDeviceX,
            target.DpiScaleX,
            target.DpiScaleY,
            target.MaximumCloseWidthDip,
            Lerp(start.Opacity, target.Opacity, progress),
            Lerp(start.ContentOpacity, target.ContentOpacity, progress),
            target.OutlineVisible,
            target.IsHitTestVisible);
        return new EdgeCapsuleTransitionSample(frame, false);
    }

    public static bool FramesMatch(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        applied.Visible == target.Visible &&
        applied.Bounds == target.Bounds &&
        applied.Edge == target.Edge &&
        applied.RestingWidthDevice == target.RestingWidthDevice &&
        applied.WallDeviceX == target.WallDeviceX &&
        Math.Abs(applied.DpiScaleX - target.DpiScaleX) < 0.001 &&
        Math.Abs(applied.DpiScaleY - target.DpiScaleY) < 0.001 &&
        Math.Abs(applied.MaximumCloseWidthDip - target.MaximumCloseWidthDip) < 0.001 &&
        Math.Abs(applied.Opacity - target.Opacity) < 0.001 &&
        Math.Abs(applied.ContentOpacity - target.ContentOpacity) < 0.001 &&
        applied.OutlineVisible == target.OutlineVisible &&
        applied.IsHitTestVisible == target.IsHitTestVisible;

    private static int LerpDevice(int from, int to, double progress) =>
        (int)Math.Round(Lerp(from, to, progress), MidpointRounding.AwayFromZero);

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;

    private static double EaseOutCubic(double progress) =>
        1.0 - Math.Pow(1.0 - progress, 3.0);
}
