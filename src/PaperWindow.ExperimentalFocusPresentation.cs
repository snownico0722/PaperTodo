using System;
using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _experimentalFocusPresentationInitialized;
    private InactiveTitleBarMask? _inactiveTitleBarMask;

    internal void UpdateExperimentalFocusPresentationSettings()
    {
        InitializeExperimentalFocusPresentation();
        RefreshExperimentalFocusPresentation();
    }

    internal void RestoreExperimentalInactiveTitleBarPresentation()
    {
        if (_inactiveTitleBarMask == null)
        {
            return;
        }

        _inactiveTitleBarMask.SetOpacity(1, 0);
        _windowHost.OpacityMask = null;
        _windowHost.LayoutUpdated -= OnInactiveTitleBarLayoutUpdated;
        _inactiveTitleBarMask = null;
        _topBarHost!.IsHitTestVisible = true;
    }

    private void InitializeExperimentalFocusPresentation()
    {
        if (_experimentalFocusPresentationInitialized)
        {
            return;
        }

        _experimentalFocusPresentationInitialized = true;
        // Hover reveals optional action buttons; the whole title bar follows focus.
        MouseEnter += (_, _) => RefreshExperimentalFocusPresentation();
        MouseLeave += (_, _) => RefreshExperimentalFocusPresentation();
        IsVisibleChanged += (_, _) => RefreshExperimentalFocusPresentation(animate: false);
    }

    private void RefreshExperimentalFocusPresentation(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var interactionReveal =
            IsActive ||
            IsBuiltInFindOpen ||
            HasOpenOwnedContextMenu() ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _topBarDrag?.IsDragging == true;

        var eligible = CanFadeInactiveTitleBar();
        SetInactiveTitleBarHidden(
            _controller.State.ExperimentalHideInactiveTitleBar && !interactionReveal && eligible,
            animate && eligible);

        if (_topBarActionButtonsHost != null)
        {
            var hideButtons =
                _controller.State.ExperimentalHideInactiveTopBarButtons &&
                !(interactionReveal || IsMouseOver);
            _topBarActionButtonsHost.IsHitTestVisible = !hideButtons;
            SetExperimentalVisualOpacity(
                _topBarActionButtonsHost,
                hideButtons ? 0.0 : 1.0,
                animate);
        }
    }

    private bool CanFadeInactiveTitleBar() =>
        AllowsTransparency &&
        IsVisible &&
        !_paper.IsCollapsed &&
        WindowState == WindowState.Normal &&
        !_isSnappedPresentation &&
        !IsPaperFormTransitioning;

    private void SetInactiveTitleBarHidden(bool hidden, bool animate)
    {
        if (_topBarHost == null)
        {
            return;
        }

        _topBarHost.IsHitTestVisible = !hidden;
        if (!hidden && _inactiveTitleBarMask == null)
        {
            return;
        }

        if (!hidden && (!animate || !_controller.State.EnableAnimations))
        {
            RestoreExperimentalInactiveTitleBarPresentation();
            return;
        }

        if (_inactiveTitleBarMask == null)
        {
            _inactiveTitleBarMask = new InactiveTitleBarMask();
            // Apply outside _paperChrome.Effect. Masking only the controls leaves the
            // paper background/shadow nonzero and the layered HWND still blocks clicks.
            _windowHost.LayoutUpdated += OnInactiveTitleBarLayoutUpdated;
        }

        UpdateInactiveTitleBarMaskBounds();
        _inactiveTitleBarMask.SetOpacity(
            hidden ? 0 : 1,
            animate && _controller.State.EnableAnimations
                ? ExperimentalOpacityTransitionMilliseconds : 0,
            hidden ? null : RestoreExperimentalInactiveTitleBarPresentation);
    }

    private void OnInactiveTitleBarLayoutUpdated(object? sender, EventArgs e)
    {
        // Typography, zoom, resize and DPI changes can move the boundary. Layout stays
        // owned by the original shell; this only updates the mask after layout settles.
        if (!CanFadeInactiveTitleBar())
        {
            RestoreExperimentalInactiveTitleBarPresentation();
            return;
        }
        UpdateInactiveTitleBarMaskBounds();
    }

    private void UpdateInactiveTitleBarMaskBounds()
    {
        if (_inactiveTitleBarMask == null || _topBarHost == null ||
            _shell.RowDefinitions.Count == 0 || _shell.RowDefinitions[0].ActualHeight <= 0)
        {
            return;
        }

        var boundary = _shell.TransformToAncestor(_windowHost).Transform(
            new Point(0, _shell.RowDefinitions[0].ActualHeight)).Y;
        var chromeBounds = _paperChrome.TransformToAncestor(_windowHost).TransformBounds(
            new Rect(_paperChrome.RenderSize));
        var topCornerRadius = Math.Min(
            _paperChrome.CornerRadius.TopLeft,
            _paperChrome.CornerRadius.TopRight);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        _inactiveTitleBarMask.UpdateBounds(
            _windowHost.RenderSize,
            Math.Round(boundary * dpi) / dpi,
            chromeBounds,
            topCornerRadius);
        _windowHost.OpacityMask = _inactiveTitleBarMask.MaskBrush;
    }
}
