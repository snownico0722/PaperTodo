using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const int ExperimentalInactiveTitleBarTransitionMilliseconds = 120;
    private bool _experimentalFocusPresentationInitialized;
    private bool _experimentalInactiveTitleBarCollapsed;
    private double _experimentalInactiveTitleBarExtent;
    private double _experimentalInactiveTitleBarExpandedMinHeight;
    private int _experimentalInactiveTitleBarAnimationGeneration;
    private bool? _experimentalInactiveTitleBarAnimationTargetCollapsed;
    private TranslateTransform? _experimentalInactiveTitleBarTranslate;
    private bool _experimentalInactiveTitleBarShellHeightLocked;
    private double _experimentalInactiveTitleBarShellBaseHeight = double.NaN;

    internal void UpdateExperimentalFocusPresentationSettings()
    {
        InitializeExperimentalFocusPresentation();
        RefreshExperimentalFocusPresentation(animate: true);
    }

    internal void RestoreExperimentalInactiveTitleBarGeometry()
    {
        ExpandExperimentalInactiveTitleBar(animate: false);
    }

    private bool BeginExperimentalInactiveTitleBarLayoutChange()
    {
        if (!_experimentalInactiveTitleBarCollapsed)
        {
            return false;
        }

        ExpandExperimentalInactiveTitleBar(animate: false);
        return true;
    }

    private void EndExperimentalInactiveTitleBarLayoutChange(bool reapply)
    {
        if (!reapply)
        {
            return;
        }

        _shell.UpdateLayout();
        RefreshExperimentalFocusPresentation(animate: false);
    }

    private void InitializeExperimentalFocusPresentation()
    {
        if (_experimentalFocusPresentationInitialized)
        {
            return;
        }

        _experimentalFocusPresentationInitialized = true;
        // Hover still reveals optional action buttons, but title-bar geometry follows focus.
        MouseEnter += (_, _) => RefreshExperimentalFocusPresentation();
        MouseLeave += (_, _) => RefreshExperimentalFocusPresentation();
    }

    private void RefreshExperimentalFocusPresentation(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var interactionReveal =
            IsActive ||
            HasOpenOwnedContextMenu() ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _topBarDrag?.IsDragging == true;

        var hideTitleBar =
            StateHidesInactiveTitleBar() &&
            !interactionReveal &&
            CanUseExperimentalInactiveTitleBarGeometry();
        if (hideTitleBar)
        {
            CollapseExperimentalInactiveTitleBar(animate);
        }
        else
        {
            ExpandExperimentalInactiveTitleBar(animate);
        }

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

    private bool StateHidesInactiveTitleBar() =>
        _controller.State.ExperimentalHideInactiveTitleBar &&
        !_paper.IsCollapsed;

    private bool CanUseExperimentalInactiveTitleBarGeometry()
    {
        // An expanded edge reservation is rendered by its own EdgeCapsuleHost. It no longer
        // shares PaperWindow geometry, so retaining that slot must not disable title-bar collapse.
        return IsVisible &&
            !_paper.IsCollapsed &&
            WindowState == WindowState.Normal &&
            !_isSnappedPresentation &&
            !IsPaperFormTransitioning;
    }

    private double ExperimentalTitleBarExtent()
    {
        if (_shell.RowDefinitions.Count > 0 &&
            _shell.RowDefinitions[0].ActualHeight > 0.5)
        {
            return _shell.RowDefinitions[0].ActualHeight;
        }

        if (_topBarHost != null)
        {
            var measured =
                _topBarHost.ActualHeight +
                _topBarHost.Margin.Top +
                _topBarHost.Margin.Bottom;
            if (measured > 0.5)
            {
                return measured;
            }

            return Math.Max(
                1,
                TitleBarHeight +
                _topBarHost.BorderThickness.Top +
                _topBarHost.BorderThickness.Bottom +
                _topBarHost.Margin.Top +
                _topBarHost.Margin.Bottom);
        }

        return Math.Max(1, TitleBarHeight);
    }

    private TranslateTransform ExperimentalInactiveTitleBarTranslate()
    {
        if (_experimentalInactiveTitleBarTranslate == null)
        {
            _experimentalInactiveTitleBarTranslate = new TranslateTransform();
            _shell.RenderTransform = _experimentalInactiveTitleBarTranslate;
            _shell.RenderTransformOrigin = new Point(0, 0);
        }
        return _experimentalInactiveTitleBarTranslate;
    }

    private void CollapseExperimentalInactiveTitleBar(bool animate = true)
    {
        if (_experimentalInactiveTitleBarAnimationTargetCollapsed == true)
        {
            if (!animate)
            {
                TransitionExperimentalInactiveTitleBar(collapsed: true, animate: false);
            }
            return;
        }

        if (_experimentalInactiveTitleBarCollapsed &&
            _experimentalInactiveTitleBarAnimationTargetCollapsed == null)
        {
            return;
        }

        if (!_experimentalInactiveTitleBarCollapsed)
        {
            _experimentalInactiveTitleBarExtent = ExperimentalTitleBarExtent();
            _experimentalInactiveTitleBarExpandedMinHeight = MinHeight;
        }
        TransitionExperimentalInactiveTitleBar(collapsed: true, animate);
    }

    private void ExpandExperimentalInactiveTitleBar(bool animate = true)
    {
        if (_experimentalInactiveTitleBarAnimationTargetCollapsed == false)
        {
            if (!animate)
            {
                TransitionExperimentalInactiveTitleBar(collapsed: false, animate: false);
            }
            return;
        }

        if (!_experimentalInactiveTitleBarCollapsed &&
            _experimentalInactiveTitleBarAnimationTargetCollapsed == null)
        {
            NormalizeExperimentalInactiveTitleBarExpandedVisual();
            return;
        }
        TransitionExperimentalInactiveTitleBar(collapsed: false, animate);
    }

    private void TransitionExperimentalInactiveTitleBar(
        bool collapsed,
        bool animate)
    {
        if (_topBarHost == null || _shell.RowDefinitions.Count == 0)
        {
            return;
        }

        var extent = Math.Max(1, _experimentalInactiveTitleBarExtent);
        var translate = ExperimentalInactiveTitleBarTranslate();
        var expandingFromCollapsed =
            !collapsed &&
            _experimentalInactiveTitleBarAnimationTargetCollapsed == null;

        var currentTop = Top;
        var currentHeight =
            double.IsFinite(Height) && Height > 0
                ? Height
                : ActualHeight;
        if (!double.IsFinite(currentTop) ||
            !double.IsFinite(currentHeight) ||
            currentHeight <= 1)
        {
            return;
        }

        LockExperimentalInactiveTitleBarShellHeight(
            extent,
            expandingFromCollapsed);

        // A fully-collapsed layout has no row to animate back from. Restore the row while
        // counter-translating the fixed-height shell so the body stays at the same screen position.
        if (expandingFromCollapsed)
        {
            _topBarHost.Visibility = Visibility.Visible;
            _shell.RowDefinitions[0].Height = GridLength.Auto;
            translate.Y = -extent;
            _topBarHost.Opacity = 0;
            _shell.UpdateLayout();
        }

        var currentTranslate = translate.Y;
        var currentOpacity = _topBarHost.Opacity;
        var hidden = Math.Clamp(-currentTranslate, 0, extent);
        var targetHidden = collapsed ? extent : 0;
        var delta = targetHidden - hidden;
        var bottom = currentTop + currentHeight;
        var targetHeight = RoundToDevicePixelY(
            Math.Max(1, currentHeight - delta));
        var targetTop = RoundToDevicePixelY(bottom - targetHeight);
        var targetTranslate = -targetHidden;
        var targetOpacity = collapsed ? 0.0 : 1.0;

        // Preserve the current animated values before cancelling a reversed transition.
        Top = currentTop;
        Height = currentHeight;
        translate.Y = currentTranslate;
        _topBarHost.Opacity = currentOpacity;
        BeginAnimation(TopProperty, null);
        BeginAnimation(HeightProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        _topBarHost.BeginAnimation(OpacityProperty, null);

        _experimentalInactiveTitleBarCollapsed = true; // suppress geometry persistence until expanded
        _experimentalInactiveTitleBarAnimationTargetCollapsed = collapsed;
        _topBarHost.IsHitTestVisible = !collapsed;
        if (collapsed)
        {
            MinHeight = Math.Max(
                1,
                _experimentalInactiveTitleBarExpandedMinHeight - extent);
        }

        var shouldAnimate =
            animate &&
            _controller.State.EnableAnimations &&
            IsVisible &&
            Math.Abs(delta) > 0.5;
        var generation = ++_experimentalInactiveTitleBarAnimationGeneration;
        if (!shouldAnimate)
        {
            Top = targetTop;
            Height = targetHeight;
            translate.Y = targetTranslate;
            _topBarHost.Opacity = targetOpacity;
            CompleteExperimentalInactiveTitleBarTransition(collapsed, generation);
            return;
        }

        var duration = Math.Max(
            40,
            ExperimentalInactiveTitleBarTransitionMilliseconds *
            Math.Abs(delta) / extent);

        BeginAnimation(
            TopProperty,
            ExperimentalInactiveTitleBarAnimation(currentTop, targetTop, duration));
        var heightAnimation = ExperimentalInactiveTitleBarAnimation(
            currentHeight,
            targetHeight,
            duration);
        heightAnimation.Completed += (_, _) =>
            CompleteExperimentalInactiveTitleBarTransition(
                collapsed,
                generation,
                targetTop,
                targetHeight,
                targetTranslate,
                targetOpacity);
        BeginAnimation(HeightProperty, heightAnimation);
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            ExperimentalInactiveTitleBarAnimation(
                currentTranslate,
                targetTranslate,
                duration));
        _topBarHost.BeginAnimation(
            OpacityProperty,
            ExperimentalInactiveTitleBarAnimation(
                currentOpacity,
                targetOpacity,
                duration));
    }

    private void LockExperimentalInactiveTitleBarShellHeight(
        double extent,
        bool expandingFromCollapsed)
    {
        if (_experimentalInactiveTitleBarShellHeightLocked)
        {
            return;
        }

        var currentShellHeight = _shell.ActualHeight;
        if (!double.IsFinite(currentShellHeight) || currentShellHeight <= 0.5)
        {
            return;
        }

        _experimentalInactiveTitleBarShellBaseHeight = _shell.Height;
        _shell.Height = currentShellHeight +
            (expandingFromCollapsed ? extent : 0);
        _experimentalInactiveTitleBarShellHeightLocked = true;
    }

    private void ReleaseExperimentalInactiveTitleBarShellHeight()
    {
        if (!_experimentalInactiveTitleBarShellHeightLocked)
        {
            return;
        }

        _shell.Height = _experimentalInactiveTitleBarShellBaseHeight;
        _experimentalInactiveTitleBarShellBaseHeight = double.NaN;
        _experimentalInactiveTitleBarShellHeightLocked = false;
    }

    private static DoubleAnimation ExperimentalInactiveTitleBarAnimation(
        double from,
        double to,
        double duration) =>
        new(from, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = AnimationHelper.QuickEase,
            FillBehavior = FillBehavior.HoldEnd
        };

    private void CompleteExperimentalInactiveTitleBarTransition(
        bool collapsed,
        int generation,
        double? targetTop = null,
        double? targetHeight = null,
        double? targetTranslate = null,
        double? targetOpacity = null)
    {
        if (generation != _experimentalInactiveTitleBarAnimationGeneration)
        {
            return;
        }

        var translate = ExperimentalInactiveTitleBarTranslate();
        if (targetTop.HasValue)
        {
            Top = targetTop.Value;
        }
        if (targetHeight.HasValue)
        {
            Height = targetHeight.Value;
        }
        if (targetTranslate.HasValue)
        {
            translate.Y = targetTranslate.Value;
        }
        if (targetOpacity.HasValue && _topBarHost != null)
        {
            _topBarHost.Opacity = targetOpacity.Value;
        }

        BeginAnimation(TopProperty, null);
        BeginAnimation(HeightProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        _topBarHost?.BeginAnimation(OpacityProperty, null);
        _experimentalInactiveTitleBarAnimationTargetCollapsed = null;

        if (_topBarHost == null || _shell.RowDefinitions.Count == 0)
        {
            ReleaseExperimentalInactiveTitleBarShellHeight();
            return;
        }

        if (collapsed)
        {
            _topBarHost.Opacity = 0;
            _topBarHost.IsHitTestVisible = false;
            _topBarHost.Visibility = Visibility.Collapsed;
            _shell.RowDefinitions[0].Height = new GridLength(0);
            translate.Y = 0;
            ReleaseExperimentalInactiveTitleBarShellHeight();
            _experimentalInactiveTitleBarCollapsed = true;
        }
        else
        {
            NormalizeExperimentalInactiveTitleBarExpandedVisual();
            MinHeight = Math.Max(1, _experimentalInactiveTitleBarExpandedMinHeight);
            _experimentalInactiveTitleBarCollapsed = false;
            _experimentalInactiveTitleBarExtent = 0;
            _experimentalInactiveTitleBarExpandedMinHeight = 0;
        }
        _shell.UpdateLayout();
    }

    private void NormalizeExperimentalInactiveTitleBarExpandedVisual()
    {
        if (_topBarHost == null || _shell.RowDefinitions.Count == 0)
        {
            ReleaseExperimentalInactiveTitleBarShellHeight();
            return;
        }

        _topBarHost.BeginAnimation(OpacityProperty, null);
        _topBarHost.Opacity = 1;
        _topBarHost.Visibility = Visibility.Visible;
        _topBarHost.IsHitTestVisible = true;
        _shell.RowDefinitions[0].Height = GridLength.Auto;
        if (_experimentalInactiveTitleBarTranslate != null)
        {
            _experimentalInactiveTitleBarTranslate.Y = 0;
        }
        ReleaseExperimentalInactiveTitleBarShellHeight();
    }
}
