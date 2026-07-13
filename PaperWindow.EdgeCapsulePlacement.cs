namespace PaperTodo;

public sealed partial class PaperWindow
{
    private double DeepCapsuleTopForIndex(int index)
    {
        return MyTopForIndex(index, _edgeCapsule.Placement.SlotCount);
    }

    private double DeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth(DeepCapsuleSlotDpi().PixelsPerDip);
    }

    private double DeepCapsuleVisibleWidth(double pixelsPerDip)
    {
        // A resting edge tag owns exactly the pixels it renders: one interior shadow margin plus
        // icon/title content and its padding. There is no hidden full-width pill behind it.
        var bodyWidth = Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth(pixelsPerDip) +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth(
                limitForDeepCapsule: true,
                pixelsPerDip: pixelsPerDip) +
            CapsuleRightPadding);
        return Math.Max(34, bodyWidth + WindowChromeMargin);
    }

    private double ExpandedDeepCapsuleVisibleWidth()
    {
        return DeepCapsuleVisibleWidth() + CapsuleCloseWidth;
    }

    // Slide this capsule up to the master's slot and fade it out. The window stays shown
    // (so it keeps counting as a deep-capsule member) but, being a per-pixel transparent
    // window at Opacity 0, it is fully click-through and never blocks the master pill.
    internal void RetractIntoMaster(EdgeCapsulePlacement placement, bool animate)
    {
        if (!_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible ||
            !_controller.CanPaperDisplayAsCapsule(_paper))
        {
            ClearDeepCapsulePlacement();
            return;
        }

        if (!AttachEdgeCapsuleToQueue(
                placement,
                _paper.IsCollapsed ? EdgeCapsulePaperForm.Collapsed : EdgeCapsulePaperForm.Expanded,
                retracted: true))
        {
            return;
        }
        UpdateDeepCapsuleSlotHostTheme();
        RefreshEffectiveTopmost();

        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Retraction,
            EdgeCapsuleLayout.SlotRetractMoveMilliseconds,
            refreshLayout: true);
        if (_paper.IsCollapsed)
        {
            HideMainWindowForDeepCapsuleRest();
        }
    }

    internal void ApplyDeepCapsulePlacement(EdgeCapsulePlacement placement, bool animate = false)
    {
        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        if (!AttachEdgeCapsuleToQueue(
                placement,
                EdgeCapsulePaperForm.Collapsed,
                retracted: false))
        {
            return;
        }
        RefreshCapsuleLabel();
        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
        if (!IsPaperFormTransitioning)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        RefreshEffectiveTopmost();
    }

    internal void PreviewDeepCapsulePlacement(EdgeCapsulePlacement placement)
    {
        if (!HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost?.IsVisible != true ||
            IsDeepCapsuleReordering ||
            IsDeepCapsuleRetractedIntoMaster)
        {
            return;
        }

        if (!UpdateEdgeCapsuleQueuePlacement(placement))
        {
            return;
        }
        RequestEdgeCapsulePresentation(
            animate: true,
            EdgeCapsuleTransitionReason.Placement,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
    }

    internal void ApplyExpandedDeepCapsuleSlotPlacement(EdgeCapsulePlacement placement, bool animate = false)
    {
        var shouldReserveWhileExpanded = _controller.State.ShowDeepCapsuleWhileExpanded &&
            _controller.CanPaperDisplayAsCapsule(_paper);
        if (_paper.IsCollapsed ||
            !shouldReserveWhileExpanded ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible)
        {
            ClearExpandedDeepCapsuleSlotPlacement();
            return;
        }

        var shouldSaveExpandedGeometry = ShouldSaveDeepCapsuleExpandedGeometry;
        if (!AttachEdgeCapsuleToQueue(
                placement,
                EdgeCapsulePaperForm.Expanded,
                retracted: false))
        {
            return;
        }
        MarkEdgeCapsuleOpenedFromEdge();
        RefreshCapsuleLabel();
        UpdateDeepCapsuleSlotHostTheme();

        RefreshDeepCapsuleSlotLabel();

        var firstShow = _edgeCapsuleHost?.IsVisible != true;
        if (firstShow)
        {
            FlushEdgeCapsulePresentation(
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
        }
        else
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.Placement,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true);
        }
        RefreshEffectiveTopmost();
        UpdateToolTipSetting();
        if (!IsPaperFormTransitioning && shouldSaveExpandedGeometry)
        {
            _controller.UpdateGeometry(_paper, this);
        }
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        ChangeEdgeCapsulePaperForm(
            _paper.IsCollapsed ? EdgeCapsulePaperForm.Collapsed : EdgeCapsulePaperForm.Expanded,
            reserveWhileExpanded: false);
        UpdateDeepCapsuleSlotHostTheme();
        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.State,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
        }
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        animate = animate && _controller.State.EnableAnimations;
        if (animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            _edgeCapsule.Placement.IsPlaced &&
            !IsDeepCapsuleSlotRetracting)
        {
            if (!BeginEdgeCapsuleRetraction())
            {
                return;
            }
            RequestEdgeCapsulePresentation(
                animate: true,
                EdgeCapsuleTransitionReason.Retraction,
                EdgeCapsuleLayout.SlotRetractMoveMilliseconds);
            return;
        }

        DetachEdgeCapsuleFromQueue();
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Retraction);
    }

    public void ClearDeepCapsulePlacement(bool animate = false)
    {
        CancelDeepCapsuleReorderDrag();
        animate = animate && _controller.State.EnableAnimations;

        var shouldRetractBeforeHide = animate &&
            _edgeCapsuleHost?.IsVisible == true &&
            HasDeepCapsuleSlotPlacement &&
            !IsDeepCapsuleRetractedIntoMaster;

        if (shouldRetractBeforeHide)
        {
            HideExpandedDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            UpdateCapsuleClosePlacement();
            HideExpandedDeepCapsuleSlotHost(animate);
        }

        // A capsule may have been faded out while retracted behind the master; never leave
        // a live (expanded or free-floating) window invisible.
        if (Math.Abs(Opacity - 1.0) > 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearEdgeCapsuleOpenOrigin();
        }
    }

    // Fully remove this window from the edge stack, including any expanded reservation, and hide
    // the docked host. Controller code uses this single operation when a paper leaves the stack.
    public void DetachFromDeepCapsuleStack(bool animate = false)
    {
        ClearDeepCapsulePlacement(animate: animate);
    }

    public void UpdateDeepCapsuleMode()
    {
        if (!_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
        }
        else if (!_paper.IsCollapsed)
        {
            ClearDeepCapsulePlacement();
        }
        else
        {
            RequestEdgeCapsulePresentation(
                animate: false,
                EdgeCapsuleTransitionReason.State);
        }

        RefreshEffectiveTopmost();
    }

    public void UpdateDeepCapsuleExpandedSlotMode()
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            DetachEdgeCapsuleFromQueue();
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            if (!ChangeEdgeCapsulePaperForm(
                    EdgeCapsulePaperForm.Expanded,
                    reserveWhileExpanded: true))
            {
                return;
            }
            RefreshCapsuleLabel();
            UpdateDeepCapsuleSlotHostTheme();
            RequestEdgeCapsulePresentation(
                animate: _controller.State.EnableAnimations,
                EdgeCapsuleTransitionReason.State);
            return;
        }

        if (!_controller.State.ShowDeepCapsuleWhileExpanded && HoldsDeepCapsuleSlotWhileExpanded)
        {
            ClearDeepCapsulePlacement(animate: _controller.State.EnableAnimations);
        }
    }
}
