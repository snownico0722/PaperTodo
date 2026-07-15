using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal readonly record struct ProgrammaticPaperExpansionOrigin(double Left, double Top);

    internal void ActivateFromEdgeShortcut()
    {
        ActivateFromDeepCapsuleSlot();
    }

    private void ActivateFromDeepCapsuleSlot()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (TryRunScriptCapsule())
        {
            return;
        }

        if (_paper.IsCollapsed)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false, alignExpandedToDockedEdge: true, activateOnExpand: true);
        }
        else if (_controller.State.CollapseExpandedDeepCapsuleOnClick &&
            (HoldsDeepCapsuleSlotWhileExpanded || HasExpandedDeepCapsuleSlotReservation))
        {
            SetCollapsedState(true, alignExpandedToDockedEdge: true);
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            _controller.BringPaperToFront(_paper);
        }
    }

    public bool TryHandleLinkedNoteRepeatedOpenAsDeepCapsuleToggle()
    {
        if (_paper.IsCollapsed ||
            !_paper.IsVisible ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_controller.State.ShowDeepCapsuleWhileExpanded ||
            !CanDisplayAsCapsule())
        {
            return false;
        }

        if (!HoldsDeepCapsuleSlotWhileExpanded && !HasExpandedDeepCapsuleSlotReservation)
        {
            MarkEdgeCapsuleOpenedFromEdge();
        }

        SetCollapsedState(true, alignExpandedToDockedEdge: true);
        return true;
    }

    private void ShowMainWindowForDeepCapsuleActivation(ProgrammaticPaperExpansionOrigin? programmaticOrigin = null)
    {
        if (IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            if (programmaticOrigin is { } visiblePlacement)
            {
                MoveWindowWithoutGeometrySave(() => MoveMainWindowToProgrammaticExpansionOrigin(visiblePlacement));
            }
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            if (programmaticOrigin is { } targetPlacement)
            {
                MoveMainWindowToProgrammaticExpansionOrigin(targetPlacement);
            }
            else if (_edgeCapsuleHost != null)
            {
                // PointToScreen is physical pixels; convert through the app's global screen-DIP
                // coordinate space. Rounding with this hidden paper HWND's old monitor DPI is the
                // exact mixed-DPI bug this hand-off must avoid.
                var slotOrigin = WindowWorkAreaHelper.DeviceScreenPointToDip(
                    _edgeCapsuleHost.ScreenOrigin());
                Left = slotOrigin.X;
                Top = slotOrigin.Y;
            }
            else
            {
                Left = _paper.X;
                Top = _paper.Y;
            }
            Show();
        });
    }

    private void MoveMainWindowToProgrammaticExpansionOrigin(ProgrammaticPaperExpansionOrigin placement)
    {
        Left = placement.Left;
        Top = placement.Top;
    }

    private void HideMainWindowForDeepCapsuleRest()
    {
        if (!_paper.IsCollapsed || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        HideWithoutGeometrySave();
    }

    internal void HideMainWindowForDeepCapsuleMode()
    {
        HideMainWindowForDeepCapsuleRest();
    }

    public void EnsureExpandedSurfaceGeometry(bool alignToDockedEdge = false)
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        var needsRestore =
            !IsVisible ||
            IsPaperFormTransitioning ||
            Width <= DesiredCapsuleWindowWidth + 8 ||
            Height <= PaperLayoutDefaults.CapsuleHeight + 8 ||
            _shell.Visibility != Visibility.Visible ||
            _capsuleShell.Visibility == Visibility.Visible;
        if (!needsRestore)
        {
            return;
        }

        _collapseTransitionGeneration++;
        BeginAnimation(TransitionProgressProperty, null);
        _shell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        ResetTransitionVisuals();

        CompletePaperFormTransition(collapsed: false);
        _shell.Width = double.NaN;
        _shell.Height = double.NaN;
        _shell.Visibility = Visibility.Visible;
        _shell.Opacity = 1.0;
        _capsuleShell.Visibility = Visibility.Collapsed;
        _capsuleShell.Opacity = 0.0;
        MinWidth = PaperLayoutDefaults.MinWidth;
        MinHeight = PaperLayoutDefaults.MinHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var rawTargetWidth = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
        var rawTargetHeight = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
        Rect? rememberedDeepCapsuleExpandedGeometry = null;
        if (alignToDockedEdge &&
            ExpandedFromDeepCapsuleEdge &&
            _controller.TryGetRememberedDeepCapsuleExpandedGeometry(_paper, rawTargetWidth, rawTargetHeight, out var rememberedGeometry))
        {
            rememberedDeepCapsuleExpandedGeometry = rememberedGeometry;
            rawTargetWidth = rememberedGeometry.Width;
            rawTargetHeight = rememberedGeometry.Height;
        }

        var targetWidth = RoundToDevicePixelX(rawTargetWidth);
        var targetHeight = RoundToDevicePixelY(rawTargetHeight);
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = targetWidth;
            Height = targetHeight;
            if (alignToDockedEdge)
            {
                if (rememberedDeepCapsuleExpandedGeometry is Rect rememberedRect)
                {
                    Left = RoundToDevicePixelX(rememberedRect.Left);
                    Top = RoundToDevicePixelY(rememberedRect.Top);
                }
                else
                {
                    var requiredEdgeInset = _controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper)
                        ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                        : 0;
                    AlignExpandedToDockedEdge(targetWidth, targetHeight, requiredEdgeInset);
                }
            }
        });

        if (!IsVisible)
        {
            Opacity = 1.0;
            Show();
        }

        RefreshEffectiveTopmost();
    }

    public bool TryRestoreRememberedDeepCapsuleExpandedGeometry()
    {
        if (_paper.IsCollapsed ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_controller.State.ShowDeepCapsuleWhileExpanded ||
            !CanDisplayAsCapsule())
        {
            return false;
        }

        var fallbackWidth = Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth);
        var fallbackHeight = Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight);
        if (!_controller.TryGetRememberedDeepCapsuleExpandedGeometry(_paper, fallbackWidth, fallbackHeight, out var rememberedGeometry))
        {
            return false;
        }

        MarkEdgeCapsuleOpenedFromEdge();
        MoveWindowWithoutGeometrySave(() =>
        {
            Left = RoundToDevicePixelX(rememberedGeometry.Left);
            Top = RoundToDevicePixelY(rememberedGeometry.Top);
            Width = RoundToDevicePixelX(rememberedGeometry.Width);
            Height = RoundToDevicePixelY(rememberedGeometry.Height);
        });
        return true;
    }

    internal void ExpandForProgrammaticOpen(ProgrammaticPaperExpansionOrigin? programmaticOrigin = null)
    {
        if (!_paper.IsCollapsed)
        {
            if (programmaticOrigin is { } targetPlacement)
            {
                MoveWindowWithoutGeometrySave(() => MoveMainWindowToProgrammaticExpansionOrigin(targetPlacement));
            }
            else
            {
                EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            }
            return;
        }

        if (_controller.State.UseCapsuleMode &&
            _controller.State.UseDeepCapsuleMode &&
            HasDeepCapsuleSlotPlacement)
        {
            ShowMainWindowForDeepCapsuleActivation(programmaticOrigin);
            SetCollapsedStateCore(
                collapsed: false,
                animate: true,
                saveGeometry: true,
                alignExpandedToDockedEdge: false,
                activateOnExpand: false,
                programmaticOrigin: programmaticOrigin);
            return;
        }

        if (!IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            if (programmaticOrigin is { } targetPlacement)
            {
                MoveMainWindowToProgrammaticExpansionOrigin(targetPlacement);
            }
            else
            {
                Left = _paper.X;
                Top = _paper.Y;
            }
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            Show();
        }

        SetCollapsedStateCore(
            collapsed: false,
            animate: true,
            saveGeometry: true,
            alignExpandedToDockedEdge: false,
            activateOnExpand: false,
            programmaticOrigin: programmaticOrigin);
    }

    private void AlignExpandedToDockedEdge(double targetWidth, double targetHeight, double requiredEdgeInset = 0)
    {
        var area = DeepCapsuleWorkArea();
        var width = Math.Max(targetWidth, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(targetHeight, PaperLayoutDefaults.MinHeight);
        // "rightInset" is really the gap between the expanded window and the docked edge; it
        // reserves room for the resting capsule strip. On the left edge it pushes the window
        // rightward from area.Left; on the right edge it pulls it leftward from area.Right.
        var edgeInset = Math.Min(
            Math.Max(
                Math.Max(DeepCapsuleExpandedEdgeInset, requiredEdgeInset),
                _controller.VisibleDeepCapsuleRestingWidthForQueue(_paper) + DeepCapsuleGap),
            Math.Max(0, area.Width - width));
        var targetTop = Math.Clamp(Top, area.Top + DeepCapsuleTopMargin, Math.Max(area.Top + DeepCapsuleTopMargin, area.Bottom - height - DeepCapsuleTopMargin));

        Left = RoundToDevicePixelX(MyDeepCapsuleIsLeftEdge
            ? area.Left + edgeInset
            : area.Right - width - edgeInset);
        Top = RoundToDevicePixelY(targetTop);
    }

    internal void ReconcileExpandedDeepCapsuleInset()
    {
        if (_paper.IsCollapsed ||
            !HoldsDeepCapsuleSlotWhileExpanded ||
            !_paper.IsVisible ||
            !IsVisible)
        {
            return;
        }

        var width = Math.Max(ActualWidth > 0 ? ActualWidth : Width, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(ActualHeight > 0 ? ActualHeight : Height, PaperLayoutDefaults.MinHeight);
        MoveWindowWithoutGeometrySave(() => AlignExpandedToDockedEdge(
            width,
            height,
            ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap));
        _controller.UpdateGeometry(_paper, this);
    }
}
