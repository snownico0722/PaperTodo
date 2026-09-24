namespace PaperTodo;

/// <summary>
/// Pure transition policy. It retargets from the last applied frame; pointer intent is never
/// locked behind an obsolete transition.
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
            applied.HostBounds.IsEmpty ||
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
            var targetFrame = transition.Target.ToFrame();
#if DEBUG
            if (transition.Start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
                transition.Target.Surface != EdgeCapsuleSurfaceKind.DockedPreview)
            {
                TracePreviewTerminalSample(
                    "transition-complete",
                    transition,
                    rawProgress,
                    1,
                    targetFrame);
            }
#endif
            return new EdgeCapsuleTransitionSample(
                targetFrame,
                true);
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
        var bounds = new DeviceScreenRect(left, top, right, top + height);
        var bodyWindowWidthDevice =
            LerpDevice(start.BodyWindowWidthDevice, target.BodyWindowWidthDevice, progress);

        // An outgoing preview stays authoritative while any visible device-pixel geometry is still
        // shrinking. Once Bounds and body width have both quantized to the compact endpoint, the
        // preview viewport is already visually collapsed. Release the Preview surface identity on
        // that same frame instead of keeping an invisible preview tree/compact anchor alive until
        // raw progress reaches 1; that delayed structural flip produced the isolated terminal nudge.
        var outgoingPreview =
            start.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
            target.Surface != EdgeCapsuleSurfaceKind.DockedPreview;
        var outgoingPreviewGeometrySettled =
            outgoingPreview &&
            bounds == target.Bounds &&
            bodyWindowWidthDevice == target.BodyWindowWidthDevice;
        var surface = outgoingPreview && !outgoingPreviewGeometrySettled
            ? start.Surface
            : target.Surface;

        // Input remains suppressed for the whole outgoing transition. Surface identity may be
        // released as soon as compact geometry is pixel-identical, but pointer authority is not
        // re-enabled until the transition itself completes.
        var hitTestVisible = target.IsHitTestVisible && !outgoingPreview;
        var interactiveBounds = hitTestVisible
            ? EdgeCapsuleGeometry.InteractiveBoundsForAppliedBounds(
                bounds,
                target.Edge,
                target.DpiScaleX,
                target.DpiScaleY,
                EdgeCapsuleLayout.WindowChromeMargin)
            : default;
        var frame = new EdgeCapsulePresentationFrame(
            true,
            surface,
            bounds,
            target.HostBounds,
            interactiveBounds,
            target.Edge,
            bodyWindowWidthDevice,
            target.WallDeviceX,
            target.DpiScaleX,
            target.DpiScaleY,
            target.MaximumCloseWidthDip,
            Lerp(start.Opacity, target.Opacity, progress),
            Lerp(start.ContentOpacity, target.ContentOpacity, progress),
            target.OutlineVisible,
            hitTestVisible,
            target.CloseSegmentActsAsContent,
            start.TitleVisible || target.TitleVisible);
#if DEBUG
        if (outgoingPreview &&
            Math.Abs(bounds.Width - target.Bounds.Width) <= 2 &&
            Math.Abs(bounds.Height - target.Bounds.Height) <= 2 &&
            Math.Abs(bounds.Top - target.Bounds.Top) <= 2 &&
            Math.Abs(bodyWindowWidthDevice - target.BodyWindowWidthDevice) <= 2)
        {
            TracePreviewTerminalSample(
                outgoingPreviewGeometrySettled ? "geometry-release" : "tail-sample",
                transition,
                rawProgress,
                progress,
                frame);
        }
#endif
        return new EdgeCapsuleTransitionSample(frame, false);
    }

    public static EdgeCapsulePresentationFrame ResolveSettledFrame(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target)
    {
        if (FramesMatch(applied, target))
        {
            return applied;
        }

        return target.ToFrame();
    }

    public static bool FramesMatch(
        EdgeCapsulePresentationFrame applied,
        EdgeCapsuleTargetPresentation target) =>
        applied.Visible == target.Visible &&
        applied.Surface == target.Surface &&
        applied.Bounds == target.Bounds &&
        applied.HostBounds == target.HostBounds &&
        applied.InteractiveBounds == target.InteractiveBounds &&
        applied.Edge == target.Edge &&
        applied.BodyWindowWidthDevice == target.BodyWindowWidthDevice &&
        applied.WallDeviceX == target.WallDeviceX &&
        Math.Abs(applied.DpiScaleX - target.DpiScaleX) < 0.001 &&
        Math.Abs(applied.DpiScaleY - target.DpiScaleY) < 0.001 &&
        Math.Abs(applied.MaximumCloseWidthDip - target.MaximumCloseWidthDip) < 0.001 &&
        Math.Abs(applied.Opacity - target.Opacity) < 0.001 &&
        Math.Abs(applied.ContentOpacity - target.ContentOpacity) < 0.001 &&
        applied.OutlineVisible == target.OutlineVisible &&
        applied.IsHitTestVisible == target.IsHitTestVisible &&
        applied.CloseSegmentActsAsContent == target.CloseSegmentActsAsContent &&
        applied.TitleVisible == target.TitleVisible;

#if DEBUG
    private static void TracePreviewTerminalSample(
        string phase,
        EdgeCapsuleTransition transition,
        double rawProgress,
        double easedProgress,
        EdgeCapsulePresentationFrame frame)
    {
        var target = transition.Target;
        var targetFrame = target.ToFrame();
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.terminal phase={phase} reason={transition.Reason} " +
            $"raw={rawProgress:F6} eased={easedProgress:F6} " +
            $"surface={frame.Surface} targetSurface={targetFrame.Surface} " +
            $"bounds={FormatRect(frame.Bounds)} target={FormatRect(targetFrame.Bounds)} " +
            $"deltaTop={frame.Bounds.Top - targetFrame.Bounds.Top} " +
            $"deltaWidth={frame.Bounds.Width - targetFrame.Bounds.Width} " +
            $"deltaHeight={frame.Bounds.Height - targetFrame.Bounds.Height} " +
            $"body={frame.BodyWindowWidthDevice} " +
            $"targetBody={targetFrame.BodyWindowWidthDevice} " +
            $"deltaBody={frame.BodyWindowWidthDevice - targetFrame.BodyWindowWidthDevice} " +
            $"interactive={FormatRect(frame.InteractiveBounds)} " +
            $"targetInteractive={FormatRect(targetFrame.InteractiveBounds)} " +
            $"hitTest={frame.IsHitTestVisible} targetHitTest={targetFrame.IsHitTestVisible} " +
            $"opacity={frame.Opacity:F6} targetOpacity={targetFrame.Opacity:F6} " +
            $"deltaOpacity={frame.Opacity - targetFrame.Opacity:F6} " +
            $"contentOpacity={frame.ContentOpacity:F6} " +
            $"targetContentOpacity={targetFrame.ContentOpacity:F6} " +
            $"deltaContentOpacity={frame.ContentOpacity - targetFrame.ContentOpacity:F6} " +
            $"outline={frame.OutlineVisible} targetOutline={targetFrame.OutlineVisible} " +
            $"closeSegment={frame.CloseSegmentActsAsContent} " +
            $"targetCloseSegment={targetFrame.CloseSegmentActsAsContent} " +
            $"frameMatch={frame == targetFrame} " +
            $"host={FormatRect(target.HostBounds)} edge={target.Edge}");
    }

    private static string FormatRect(DeviceScreenRect rect) =>
        $"{rect.Left},{rect.Top},{rect.Width}x{rect.Height}";
#endif

    private static int LerpDevice(int from, int to, double progress) =>
        (int)Math.Round(Lerp(from, to, progress), MidpointRounding.AwayFromZero);

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;

    private static double EaseOutCubic(double progress) =>
        1.0 - Math.Pow(1.0 - progress, 3.0);
}
