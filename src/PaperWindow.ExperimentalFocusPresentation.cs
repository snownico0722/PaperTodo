using System;
using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _experimentalFocusPresentationInitialized;
    private bool _inactiveTitleBarPresentationAttached;

    internal void UpdateExperimentalFocusPresentationSettings()
    {
        InitializeExperimentalFocusPresentation();
        RefreshExperimentalFocusPresentation();
    }

    internal void RestoreExperimentalInactiveTitleBarPresentation()
    {
        if (!_inactiveTitleBarPresentationAttached)
        {
            return;
        }

        _paperChrome.SetHeaderOpacity(1, 0);
        _paperChrome.SetHeaderExtent(0);
        _windowHost.LayoutUpdated -= OnInactiveTitleBarLayoutUpdated;
        _inactiveTitleBarPresentationAttached = false;
        _topBarHost!.IsHitTestVisible = true;
    }

    private void InitializeExperimentalFocusPresentation()
    {
        if (_experimentalFocusPresentationInitialized || !_isShellBuilt)
        {
            return;
        }

        _experimentalFocusPresentationInitialized = true;
        _topBarHost!.SetBinding(OpacityProperty, new System.Windows.Data.Binding
        {
            Source = _paperChrome,
            Path = new PropertyPath(PaperChromeBorder.HeaderOpacityProperty)
        });
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
        if (!hidden && !_inactiveTitleBarPresentationAttached)
        {
            return;
        }

        if (!hidden && (!animate || !_controller.State.EnableAnimations))
        {
            RestoreExperimentalInactiveTitleBarPresentation();
            return;
        }

        if (!_inactiveTitleBarPresentationAttached)
        {
            _inactiveTitleBarPresentationAttached = true;
            _windowHost.LayoutUpdated += OnInactiveTitleBarLayoutUpdated;
        }

        UpdateInactiveTitleBarExtent();
        _paperChrome.SetHeaderOpacity(
            hidden ? 0 : 1,
            animate && _controller.State.EnableAnimations
                ? ExperimentalOpacityTransitionMilliseconds : 0,
            hidden ? null : RestoreExperimentalInactiveTitleBarPresentation);
    }

    private void OnInactiveTitleBarLayoutUpdated(object? sender, EventArgs e)
    {
        // Typography and DPI changes can change the title row's extent. The shell
        // retains its original layout; only the background surface gets shorter.
        if (!CanFadeInactiveTitleBar())
        {
            RestoreExperimentalInactiveTitleBarPresentation();
            return;
        }
        UpdateInactiveTitleBarExtent();
    }

    private void UpdateInactiveTitleBarExtent()
    {
        if (!_inactiveTitleBarPresentationAttached || _topBarHost == null ||
            _shell.RowDefinitions.Count == 0 || _shell.RowDefinitions[0].ActualHeight <= 0)
        {
            return;
        }

        _paperChrome.SetHeaderExtent(_shell.RowDefinitions[0].ActualHeight);
    }
}
