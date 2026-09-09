namespace PaperTodo;

/// <summary>
/// Pure desired-model to shape/layout planner. FloatingFree is a first-class shape and therefore
/// cannot inherit the docked wall-side close segment from a constructor parameter.
/// </summary>
internal static class EdgeCapsuleTargetPlanner
{
    public static EdgeCapsulePresentationPlan Calculate(
        EdgeCapsuleModel model,
        EdgeCapsuleLayoutSnapshot layout)
    {
        if (model.State.Slot == EdgeCapsuleSlotState.None ||
            !model.Placement.IsPlaced ||
            !layout.IsUsable)
        {
            return EdgeCapsulePresentationPlan.Hidden;
        }

        var retracted = model.State.Slot is
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractedExpanded or
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded;
        var retracting = model.State.Slot is
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded;
        var ownsFloatingHost = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering or
            EdgeCapsuleGestureState.DockingHandoff or
            EdgeCapsuleGestureState.DockingReveal;
        var dockedSuppressed = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering or
            EdgeCapsuleGestureState.DockingHandoff;
        var keepPermanentEndpointCompact = model.State.Gesture is
            EdgeCapsuleGestureState.FloatingTransfer or
            EdgeCapsuleGestureState.FloatingReordering;
        var preview = !retracted &&
            !dockedSuppressed &&
            model.Preview == EdgeCapsulePreviewState.Open;
        // Free floating drag keeps the hidden permanent endpoint compact. Once docking hand-off
        // begins, that endpoint becomes the authoritative physical target even while suppressed,
        // so Handoff, Reveal and the final interactive frame must share one Hovered/Active geometry.
        var expanded = !keepPermanentEndpointCompact &&
            !preview &&
            !retracted &&
            (model.State.Visual is
                EdgeCapsuleVisualState.Hovered or
                EdgeCapsuleVisualState.Active);
        var top = model.DockedDragTopDipOverride ??
            (retracted ? layout.MasterTopDip : layout.NormalTopDip);

        // Preview is its own surface and keeps its close control. For the ordinary hover/active
        // surface, "hide close button" means there is no close segment at all — not an invisible
        // reserved strip that still makes the pill longer.
        var closeSegmentVisible =
            preview ||
            (expanded && !layout.CloseSegmentActsAsContent);
        var closeWidth = closeSegmentVisible
            ? layout.MaximumCloseWidthDip
            : 0;
        var visibleHeight = preview
            ? layout.PreviewHeightDip
            : layout.HeightDip;
        var expandedWidth = Math.Max(layout.RestingWidthDip, layout.ExpandedWidthDip);
        // PreviewWidthDip remains the total preview envelope, so its body always excludes the
        // preview close strip. Ordinary hover width uses ExpandedWidthDip as the body directly.
        var previewBodyWidth = Math.Max(
            1,
            layout.PreviewWidthDip - layout.MaximumCloseWidthDip);
        var bodyWidth = preview
            ? previewBodyWidth
            : expanded ? expandedWidth : layout.RestingWidthDip;
        var geometry = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            layout.Monitor,
            layout.Edge,
            top,
            bodyWidth,
            closeWidth,
            visibleHeight));
        // V3 Lite keeps one stable local capacity per paper. The
        // PaperWindow grows it to the largest real preview requested on the
        // current monitor/DPI/edge and never shrinks it during that host
        // generation.
        var hostVisibleWidth = Math.Max(
            expandedWidth + layout.MaximumCloseWidthDip,
            Math.Max(
                layout.PreviewWidthDip,
                layout.HostCapacityWidthDip));
        var hostVisibleHeight = Math.Max(
            layout.HeightDip,
            Math.Max(
                layout.PreviewHeightDip,
                layout.HostCapacityHeightDip));
        var hostBodyWidth = Math.Max(
            layout.RestingWidthDip,
            hostVisibleWidth -
                layout.MaximumCloseWidthDip);
        var hostGeometry = EdgeCapsuleGeometry.Calculate(
            new EdgeCapsuleGeometryInput(
                layout.Monitor,
                layout.Edge,
                top,
                hostBodyWidth,
                layout.MaximumCloseWidthDip,
                hostVisibleHeight));
        var surface = SurfaceFor(
            model,
            preview,
            retracted,
            retracting,
            dockedSuppressed);
        var hitTest = !retracted && !ownsFloatingHost;
        var interactiveBounds = hitTest ? geometry.InteractiveBounds : default;
        var contentOpacity = layout.ForcedContentOpacity is { } forcedOpacity
            ? Math.Clamp(forcedOpacity, 0, 1)
            : surface == EdgeCapsuleSurfaceKind.DockedResting
                ? Math.Clamp(layout.RestingContentOpacity, 0, 1)
                : 1;
        var docked = new EdgeCapsuleTargetPresentation(
            true,
            surface,
            geometry.Bounds,
            hostGeometry.Bounds,
            interactiveBounds,
            layout.Edge,
            geometry.RestingWidthDevice,
            geometry.WallDeviceX,
            geometry.DpiScaleX,
            geometry.DpiScaleY,
            layout.MaximumCloseWidthDip,
            retracted ? 0 : 1,
            dockedSuppressed ? 0 : contentOpacity,
            !retracted && !dockedSuppressed &&
                model.State.Visual == EdgeCapsuleVisualState.Active,
            hitTest,
            false,
            !layout.HideRestingTitle || (expanded && expandedWidth > layout.RestingWidthDip));

        var floatingShape = ownsFloatingHost
            ? CreateFloatingShape(layout, model.State.Visual == EdgeCapsuleVisualState.Active)
            : EdgeCapsuleFloatingShape.Hidden;
        return new EdgeCapsulePresentationPlan(docked, floatingShape);
    }

    private static EdgeCapsuleSurfaceKind SurfaceFor(
        EdgeCapsuleModel model,
        bool preview,
        bool retracted,
        bool retracting,
        bool dockedSuppressed)
    {
        if (dockedSuppressed)
        {
            return EdgeCapsuleSurfaceKind.DockedSuppressed;
        }
        if (retracting)
        {
            return EdgeCapsuleSurfaceKind.DockedRetracting;
        }
        if (retracted)
        {
            return EdgeCapsuleSurfaceKind.DockedRetracted;
        }
        if (preview)
        {
            return EdgeCapsuleSurfaceKind.DockedPreview;
        }
        return model.State.Visual switch
        {
            EdgeCapsuleVisualState.Active => EdgeCapsuleSurfaceKind.DockedActive,
            EdgeCapsuleVisualState.Hovered => EdgeCapsuleSurfaceKind.DockedHovered,
            _ => EdgeCapsuleSurfaceKind.DockedResting
        };
    }

    internal static EdgeCapsuleFloatingShape CreateFloatingShape(
        EdgeCapsuleLayoutSnapshot layout,
        bool outlineVisible)
    {
        var height = layout.HeightDip;
        var bodyHeight = Math.Max(1, height - EdgeCapsuleLayout.WindowChromeMargin * 2);
        return new EdgeCapsuleFloatingShape(
            true,
            EdgeCapsuleSurfaceKind.FloatingFree,
            Math.Max(
                PaperLayoutDefaults.CapsuleWidth,
                layout.RestingWidthDip + EdgeCapsuleLayout.WindowChromeMargin),
            height,
            bodyHeight,
            Math.Min(EdgeCapsuleLayout.CornerRadius, bodyHeight / 2),
            outlineVisible);
    }
}
