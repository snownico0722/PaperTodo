using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal readonly record struct ProgrammaticPaperExpansionOrigin(double Left, double Top);
    internal readonly record struct DeepCapsuleModeHandoff(double Left, double Top);

    private int _deepCapsuleDevicePlacementGeneration;

    internal bool TryCaptureDeepCapsuleModeHandoff(out DeepCapsuleModeHandoff handoff)
    {
        handoff = default;
        if (!_paper.IsVisible ||
            !_paper.IsCollapsed ||
            !HasDeepCapsuleSlotPlacement ||
            !_edgeCapsule.Placement.IsPlaced)
        {
            return false;
        }

        var layout = CaptureEdgeCapsuleLayoutSnapshot();
        if (!layout.IsUsable)
        {
            return false;
        }

        // Use the queue target rather than a hovered/retracted applied frame, and keep the
        // mixed-DPI wall conversion in the shared physical geometry calculator.
        var targetBounds = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            layout.Monitor,
            layout.Edge,
            layout.NormalTopDip,
            DesiredCapsuleWindowWidth,
            0,
            PaperLayoutDefaults.CapsuleHeight)).Bounds;
        var targetOrigin = WindowWorkAreaHelper.DeviceScreenPointToDip(new DeviceScreenPoint(
            targetBounds.Left,
            targetBounds.Top));

        handoff = new DeepCapsuleModeHandoff(targetOrigin.X, targetOrigin.Y);
        return true;
    }

    internal bool RestoreCollapsedSurfaceAfterDeepCapsuleModeDisabled(
        DeepCapsuleModeHandoff handoff)
    {
        if (!_paper.IsVisible ||
            !_paper.IsCollapsed ||
            !_controller.State.UseCapsuleMode ||
            _controller.State.UseDeepCapsuleMode ||
            HasDeepCapsuleSlotPlacement)
        {
            return false;
        }

        EnsureShellBuilt();
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        var width = DesiredCapsuleWindowWidth;
        MoveWindowWithoutGeometrySave(() =>
        {
            ShowActivated = false;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            MinWidth = width;
            MinHeight = PaperLayoutDefaults.CapsuleHeight;
            ResizeMode = ResizeMode.NoResize;
            Width = width;
            Height = PaperLayoutDefaults.CapsuleHeight;
            Left = handoff.Left;
            Top = handoff.Top;
            if (!IsVisible)
            {
                Show();
            }
        });

        RefreshEffectiveTopmost();
        SaveGeometryForCurrentPresentation();
        return true;
    }

    internal void ActivateFromEdgeShortcut()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (TryRunScriptCapsule())
        {
            return;
        }

        if (_paper.IsCollapsed)
        {
            if (TryGetEdgeShortcutCursorOrigin(out var placement))
            {
                ShowMainWindowForDeepCapsuleActivation(placement);
                SetCollapsedStateCore(
                    collapsed: false,
                    animate: true,
                    saveGeometry: true,
                    alignExpandedToDockedEdge: false,
                    activateOnExpand: true,
                    programmaticOrigin: placement);
            }
            else
            {
                ShowMainWindowForDeepCapsuleActivation();
                SetCollapsedState(false, alignExpandedToDockedEdge: true, activateOnExpand: true);
            }
            return;
        }

        // Collapse-on-click must run before any cursor placement: writing paper.X/Y here would
        // clobber the expanded geometry even though this invocation only folds the paper.
        if (_controller.State.CollapseExpandedDeepCapsuleOnClick &&
            (HoldsDeepCapsuleSlotWhileExpanded || HasExpandedDeepCapsuleSlotReservation))
        {
            SetCollapsedState(true, alignExpandedToDockedEdge: true);
            return;
        }

        if (TryGetEdgeShortcutCursorOrigin(out var movePlacement))
        {
            MoveWindowWithoutGeometrySave(() => MoveMainWindowToProgrammaticExpansionOrigin(movePlacement));
            _controller.UpdateGeometry(_paper, this);
            _controller.BringPaperToFront(_paper);
            return;
        }

        EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
        _controller.BringPaperToFront(_paper);
    }

    private bool TryGetEdgeShortcutCursorOrigin(out ProgrammaticPaperExpansionOrigin origin)
    {
        origin = default;
        if (!_controller.State.OpenEdgeCapsuleShortcutAtCursor ||
            !_controller.TryCreateCursorPaperPlacement(
                Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth),
                Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight),
                out var cursorLeft,
                out var cursorTop))
        {
            return false;
        }

        origin = new ProgrammaticPaperExpansionOrigin(cursorLeft, cursorTop);
        return true;
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

    public bool TryHandleLinkedPaperRepeatedOpenAsDeepCapsuleToggle()
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
        EnsureShellBuilt();
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

        var bootstrapBounds = programmaticOrigin == null
            ? DeepCapsuleMainWindowBootstrapBounds()
            : default;
        var useNativeBootstrap = !bootstrapBounds.IsEmpty;

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = useNativeBootstrap ? 0 : 1.0;
        try
        {
            MoveWindowWithoutGeometrySave(() =>
            {
                Width = DesiredCapsuleWindowWidth;
                Height = PaperLayoutDefaults.CapsuleHeight;
                if (programmaticOrigin is { } targetPlacement)
                {
                    MoveMainWindowToProgrammaticExpansionOrigin(targetPlacement);
                }
                else if (!useNativeBootstrap && _edgeCapsuleHost != null)
                {
                    var slotOrigin = WindowWorkAreaHelper.DeviceScreenPointToDip(
                        _edgeCapsuleHost.ScreenOrigin());
                    Left = slotOrigin.X;
                    Top = slotOrigin.Y;
                }
                else if (!useNativeBootstrap)
                {
                    Left = _paper.X;
                    Top = _paper.Y;
                }

                Show();
                if (useNativeBootstrap &&
                    !TryApplyDeepCapsuleDeviceBounds(bootstrapBounds))
                {
                    if (_edgeCapsuleHost != null)
                    {
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
                }
            });
        }
        finally
        {
            Opacity = 1.0;
            // Show() 已同步完成,清掉 Hide 时设的旗标,后续 fullRenderPanel 的 Unloaded 按真实销毁处理。
            _suppressFullRenderPanelUnloadCleanup = false;
        }
    }

    private void MoveMainWindowToProgrammaticExpansionOrigin(ProgrammaticPaperExpansionOrigin placement)
    {
        _deepCapsuleDevicePlacementGeneration++;
        Left = placement.Left;
        Top = placement.Top;
    }

    private DeviceScreenRect DeepCapsuleMainWindowBootstrapBounds()
    {
        var frame = EdgeCapsulePresentationFrame.Hidden;
        var hasCommittedFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out frame) == true;
        if (hasCommittedFrame && frame.Visible && !frame.Bounds.IsEmpty)
        {
            return EdgeCapsuleGeometry.PaperBoundsForDockedEdge(
                DeepCapsuleMonitorGeometry(),
                frame.Edge,
                frame.Bounds.Top,
                DesiredCapsuleWindowWidth,
                PaperLayoutDefaults.CapsuleHeight,
                edgeInsetDip: 0,
                verticalMarginDip: 0);
        }

        if (!TryGetDeepCapsuleTarget(out var layout, out var targetBounds))
        {
            return default;
        }

        return EdgeCapsuleGeometry.PaperBoundsForDockedEdge(
            layout.Monitor,
            layout.Edge,
            targetBounds.Top,
            DesiredCapsuleWindowWidth,
            PaperLayoutDefaults.CapsuleHeight,
            edgeInsetDip: 0,
            verticalMarginDip: 0);
    }

    private bool TryGetDeepCapsuleTarget(
        out EdgeCapsuleLayoutSnapshot layout,
        out DeviceScreenRect bounds)
    {
        layout = default;
        bounds = default;
        if (!HasDeepCapsuleSlotPlacement)
        {
            return false;
        }

        layout = CaptureEdgeCapsuleLayoutSnapshot();
        if (!layout.IsUsable)
        {
            return false;
        }

        bounds = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            layout.Monitor,
            layout.Edge,
            IsDeepCapsuleRetractedIntoMaster
                ? layout.MasterTopDip
                : layout.NormalTopDip,
            layout.RestingWidthDip,
            0,
            PaperLayoutDefaults.CapsuleHeight)).Bounds;
        return !bounds.IsEmpty;
    }

    private bool TryApplyDeepCapsuleDeviceBounds(DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        if (WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds) &&
            EdgeCapsuleGeometry.DeviceBoundsMatch(currentBounds, bounds, tolerance: 1))
        {
            return true;
        }

        return WindowNative.TrySetWindowDeviceBounds(this, bounds);
    }

    private void QueueDeepCapsuleDeviceBoundsConfirmation(DeviceScreenRect bounds)
    {
        var generation = ++_deepCapsuleDevicePlacementGeneration;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _deepCapsuleDevicePlacementGeneration ||
                    _paper.IsCollapsed ||
                    !IsVisible)
                {
                    return;
                }

                _ = TryApplyDeepCapsuleDeviceBounds(bounds);
            }),
            System.Windows.Threading.DispatcherPriority.Render);
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
        // Window.Hide 会让 visual tree 异步派发 Unloaded;设旗标让 OnFullRenderPanelUnloaded
        // 跳过清理,展开后图片仍能正常显示。Show 完成后清除旗标。
        _suppressFullRenderPanelUnloadCleanup = true;
        try
        {
            HideWithoutGeometrySave();
        }
        catch
        {
            _suppressFullRenderPanelUnloadCleanup = false;
            throw;
        }
    }

    internal void HideMainWindowForDeepCapsuleMode()
    {
        HideMainWindowForDeepCapsuleRest();
    }

    public void EnsureExpandedSurfaceGeometry(bool alignToDockedEdge = false)
    {
        EnsureShellBuilt();
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
        var monitor = DeepCapsuleMonitorGeometry();
        var width = Math.Max(targetWidth, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(targetHeight, PaperLayoutDefaults.MinHeight);
        var edgeInset = Math.Max(
            Math.Max(DeepCapsuleExpandedEdgeInset, requiredEdgeInset),
            _controller.VisibleDeepCapsuleRestingWidthForQueue(_paper) + DeepCapsuleGap);
        var committedFrame = EdgeCapsulePresentationFrame.Hidden;
        var hasCommittedFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out committedFrame) == true;
        var appliedBounds = hasCommittedFrame
            ? committedFrame.Bounds
            : default;
        var hasQueueTarget = TryGetDeepCapsuleTarget(
            out _,
            out var queueTargetBounds);
        var anchorTop = !appliedBounds.IsEmpty
            ? appliedBounds.Top
            : hasQueueTarget
                ? queueTargetBounds.Top
                : WindowNative.TryGetWindowDeviceBounds(this, out var currentBounds)
                    ? currentBounds.Top
                    : monitor.WorkArea.Top;
        var targetBounds = EdgeCapsuleGeometry.PaperBoundsForDockedEdge(
            monitor,
            MyDeepCapsuleEdge,
            anchorTop,
            width,
            height,
            edgeInset,
            DeepCapsuleTopMargin);
        if (TryApplyDeepCapsuleDeviceBounds(targetBounds))
        {
            // Width/Height are written by the form transition immediately after this method.
            // Confirm the same physical rectangle after that layout pass, before rendering.
            QueueDeepCapsuleDeviceBoundsConfirmation(targetBounds);
            return;
        }

        var area = DeepCapsuleWorkArea();
        var fallbackInset = Math.Min(edgeInset, Math.Max(0, area.Width - width));
        var targetTop = Math.Clamp(
            Top,
            area.Top + DeepCapsuleTopMargin,
            Math.Max(
                area.Top + DeepCapsuleTopMargin,
                area.Bottom - height - DeepCapsuleTopMargin));
        Left = RoundToDevicePixelX(MyDeepCapsuleIsLeftEdge
            ? area.Left + fallbackInset
            : area.Right - width - fallbackInset);
        Top = RoundToDevicePixelY(targetTop);
    }
}
