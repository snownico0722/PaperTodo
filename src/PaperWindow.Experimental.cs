using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

[Flags]
internal enum ExperimentalPassiveReason
{
    None = 0,
    CurrentPaper = 1,
    AllSurfaces = 2
}

public sealed partial class PaperWindow
{
    private const int ExperimentalOpacityTransitionMilliseconds = 120;
    private ExperimentalPassiveReason _experimentalPassiveReasons;
    private bool _experimentalPassiveNativeApplied;
    private bool _experimentalPassiveHadNoActivateStyle;
    private bool _experimentalPassiveNeedsZOrderRestore;
    private int _experimentalAutoCollapseGeneration;

    internal bool IsExperimentalPassive =>
        _experimentalPassiveReasons != ExperimentalPassiveReason.None;

    internal bool CanEnterCurrentExperimentalPassive =>
        !IsClosed &&
        IsVisible &&
        WindowState != WindowState.Minimized;

    internal void SetExperimentalPassiveReason(
        ExperimentalPassiveReason reason,
        bool enabled)
    {
        if (reason == ExperimentalPassiveReason.None)
        {
            return;
        }

        var previous = _experimentalPassiveReasons;
        var wasPassive = previous != ExperimentalPassiveReason.None;
        var next = enabled
            ? previous | reason
            : previous & ~reason;
        if (previous == next)
        {
            return;
        }
        // A queue proxy is controller-owned and cannot be demoted/raised with one paper's native
        // passive state. Handoff first; eligibility checks keep a retained retry non-interactive.
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        _experimentalPassiveReasons = next;

        if (!wasPassive && IsExperimentalPassive)
        {
            CancelExperimentalAutoCollapse();
        }

        if (_experimentalPassiveReasons != ExperimentalPassiveReason.None)
        {
            AbortAllInteractions(InteractionAbortReason.Deactivated);
            if (IsActive)
            {
                WindowNative.ClearCurrentThreadKeyboardFocus();
            }
        }

        ApplyExperimentalPassiveNativeState();
        ApplyExperimentalAuxiliaryPassiveState();
        if (wasPassive && !IsExperimentalPassive)
        {
            _experimentalPassiveNeedsZOrderRestore = true;
        }
        RefreshEffectiveTopmost();
    }

    internal void SetExperimentalAllSurfacesPassive(bool enabled)
    {
        SetExperimentalPassiveReason(
            ExperimentalPassiveReason.AllSurfaces,
            enabled);
    }

    private void ApplyExperimentalAuxiliaryPassiveState()
    {
        var passive = IsExperimentalPassive;
        _edgeCapsuleHost?.SetExperimentalPassive(passive);
        _experimentalTetherCapsule?.SetExperimentalPassive(passive);
    }

    private void ApplyExperimentalPassiveNativeState()
    {
        var passive = _experimentalPassiveReasons != ExperimentalPassiveReason.None;
        if (passive)
        {
            if (!_experimentalPassiveNativeApplied)
            {
                _experimentalPassiveHadNoActivateStyle =
                    WindowNative.HasNoActivateStyle(this);
            }

            WindowNative.SetNoActivateStyle(this, enabled: true);
            WindowNative.SetInputPassthrough(this, enabled: true);
            _experimentalPassiveNativeApplied = true;
            return;
        }

        if (!_experimentalPassiveNativeApplied)
        {
            return;
        }

        WindowNative.SetInputPassthrough(this, enabled: false);
        if (!_experimentalPassiveHadNoActivateStyle)
        {
            WindowNative.SetNoActivateStyle(this, enabled: false);
        }

        _experimentalPassiveNativeApplied = false;
        _experimentalPassiveHadNoActivateStyle = false;
    }

    internal void UpdateExperimentalOpacitySettings(bool animate = true)
    {
        _experimentalTetherCapsule?.UpdateRestingOpacity(
            _controller.State.ExperimentalRestingCapsuleOpacity
                ? ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                    ExperimentalOpacityLevels.DefaultRestingCapsule)
                : 1.0,
            _controller.State.ExperimentalRestingCapsuleOpacity &&
            _controller.State.ExperimentalRestingCapsuleOpacityAlways);
        if (_isShellBuilt)
        {
            if (_controller.State.ExperimentalRestingCapsuleOpacity ||
                _controller.IsAdvancedCapsuleTransparent(_paper))
            {
                AttachCapsuleShellToExperimentalOpacityHost();
            }
            else
            {
                AttachCapsuleShellDirectlyToWindowHost();
            }
        }

        RefreshExperimentalOpacity(animate);
        if (_edgeCapsuleHost != null || HasDeepCapsuleSlotPlacement)
        {
            RequestEdgeCapsulePresentation(
                animate,
                EdgeCapsuleTransitionReason.State);
        }
    }

    private void RefreshExperimentalOpacity(bool animate = true)
    {
        if (!_isShellBuilt)
        {
            return;
        }

        var ownMenuOpen = HasOpenOwnedContextMenu();
        var expandedPaperInteractive =
            IsActive ||
            IsBuiltInFindOpen ||
            ownMenuOpen ||
            _titleBarDragSession != null ||
            _todoDrag?.IsDragging == true ||
            _topBarDrag?.IsDragging == true;
        var paperOpacity = _controller.IsAdvancedPaperTransparent(_paper)
            ? _controller.AdvancedShortcutOpacity
            : !_controller.State.ExperimentalInactivePaperOpacity ||
              _paper.IsCollapsed ||
              expandedPaperInteractive
                ? 1.0
                : ExperimentalOpacityLevels.Normalize(
                    _controller.State.ExperimentalInactivePaperOpacityLevel,
                    ExperimentalOpacityLevels.DefaultInactivePaper);
        SetExperimentalVisualOpacity(_paperChrome, paperOpacity, animate);

        if ((_controller.State.ExperimentalRestingCapsuleOpacity ||
             _controller.IsAdvancedCapsuleTransparent(_paper)) &&
            _capsuleOpacityHost != null)
        {
            var ordinaryCapsuleInteractive =
                _capsuleOpacityHost.IsMouseOver ||
                _capsulePointerState != CapsulePointerState.Idle ||
                ownMenuOpen;
            var capsuleOpacity = _controller.IsAdvancedCapsuleTransparent(_paper)
                ? _controller.AdvancedShortcutOpacity
                : !_paper.IsCollapsed ||
                  (ordinaryCapsuleInteractive &&
                   !_controller.State.ExperimentalRestingCapsuleOpacityAlways)
                    ? 1.0
                    : ExperimentalOpacityLevels.Normalize(
                        _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                        ExperimentalOpacityLevels.DefaultRestingCapsule);
            SetExperimentalVisualOpacity(
                _capsuleOpacityHost,
                capsuleOpacity,
                animate);
        }

        RefreshExperimentalFocusPresentation(animate);
    }

    private bool HasOpenOwnedContextMenu()
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (_themedContextMenus[i].TryGetTarget(out var menu))
            {
                if (menu.IsOpen)
                {
                    return true;
                }
            }
            else
            {
                _themedContextMenus.RemoveAt(i);
            }
        }

        return false;
    }

    private void CancelExperimentalAutoCollapse()
    {
        _experimentalAutoCollapseGeneration++;
    }

    private void ScheduleExperimentalAutoCollapse(bool blockedAtDeactivation)
    {
        var generation = ++_experimentalAutoCollapseGeneration;
        if (blockedAtDeactivation ||
            !_controller.State.ExperimentalCollapsePaperOnDeactivate ||
            !_controller.State.UseCapsuleMode ||
            _paper.IsCollapsed)
        {
            return;
        }

        var timer = new DispatcherTimer(
            DispatcherPriority.ContextIdle,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            timer.Stop();
            if (tick != null)
            {
                timer.Tick -= tick;
            }

            if (generation != _experimentalAutoCollapseGeneration ||
                !CanAutomaticallyCollapseNow())
            {
                return;
            }

            SetCollapsedState(true);
        };
        timer.Tick += tick;
        timer.Start();
    }

    private bool CanAutomaticallyCollapseNow() =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        _controller.State.ExperimentalCollapsePaperOnDeactivate &&
        _controller.State.UseCapsuleMode &&
        !IsActive &&
        IsVisible &&
        _paper.IsVisible &&
        !_paper.IsCollapsed &&
        WindowState != WindowState.Minimized &&
        !HasExperimentalAutoCollapseBlocker() &&
        CanDisplayAsCapsule();

    private bool HasExperimentalAutoCollapseBlocker() =>
        IsExperimentalPassive ||
        _advancedInteractionLocked ||
        IsBuiltInFindOpen ||
        _isEditingTitle ||
        _titleBarDragSession != null ||
        _todoDrag?.IsDragging == true ||
        _topBarDrag?.IsDragging == true ||
        (Mouse.Captured is DependencyObject captured &&
            IsDescendantOf(captured, this)) ||
        HasOpenOwnedContextMenu() ||
        HasOpenComboBox(_paperChrome);

    private static bool HasOpenComboBox(DependencyObject? root)
    {
        if (root == null)
        {
            return false;
        }
        if (root is ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            if (HasOpenComboBox(VisualTreeHelper.GetChild(root, index)))
            {
                return true;
            }
        }
        return false;
    }

    private void SetExperimentalVisualOpacity(
        UIElement? element,
        double target,
        bool animate)
    {
        if (element == null)
        {
            return;
        }

        target = Math.Clamp(target, 0, 1);
        var opacityBase = (double)element.GetAnimationBaseValue(
            UIElement.OpacityProperty);
        if (Math.Abs(element.Opacity - target) < 0.001 &&
            Math.Abs(opacityBase - target) < 0.001)
        {
            return;
        }

        if (animate &&
            _controller.State.EnableAnimations &&
            IsVisible)
        {
            AnimationHelper.FadeTo(
                element,
                target,
                ExperimentalOpacityTransitionMilliseconds,
                AnimationHelper.QuickEase);
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
    }
}
