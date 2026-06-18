using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Window EnsureDeepCapsuleSlotHost()
    {
        if (_deepCapsuleSlotHost != null)
        {
            return _deepCapsuleSlotHost;
        }

        _deepCapsuleSlotHostRoot = new Grid
        {
            Background = null,
            ClipToBounds = true,
            Opacity = 1
        };

        _deepCapsuleSlotChrome = new Border
        {
            Margin = new Thickness(WindowChromeMargin),
            CornerRadius = new CornerRadius(CapsuleChromeCornerRadius),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_deepCapsuleSlotChrome, 0);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotChrome);

        _deepCapsuleSlotShell = BuildDeepCapsuleSlotShell();
        Panel.SetZIndex(_deepCapsuleSlotShell, 10);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotShell);

        _deepCapsuleSlotOutline = new Border
        {
            Margin = new Thickness(WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap),
            CornerRadius = new CornerRadius(CapsuleChromeCornerRadius + DeepCapsuleSlotOutlineThickness - DeepCapsuleSlotOutlineOverlap),
            BorderThickness = new Thickness(DeepCapsuleSlotOutlineThickness),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_deepCapsuleSlotOutline, 20);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotOutline);

        var host = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Segoe UI"),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = !_controller.SuppressTopmostForFullscreenForeground,
            Width = CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            Height = PaperLayoutDefaults.CapsuleHeight,
            Content = _deepCapsuleSlotHostRoot
        };
        host.SourceInitialized += (_, _) => WindowNative.ApplyNoActivateStyle(host);
        host.Deactivated += (_, _) => CloseDeepCapsuleSlotContextMenu();
        _deepCapsuleSlotHost = host;
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private Grid BuildDeepCapsuleSlotShell()
    {
        var shell = new Grid
        {
            Width = DeepCapsuleSlotShellLayoutWidth(),
            Height = 30,
            Margin = new Thickness(WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };
        _deepCapsuleSlotLeftArea = leftArea;

        var leftStack = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(CapsuleLeftPadding, 0, 0, 0)
        };
        _deepCapsuleSlotLeftStack = leftStack;
        leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _deepCapsuleSlotIconText = new TextBlock
        {
            Text = _paper.Type == PaperTypes.Note ? "✎" : "✓",
            Foreground = BrightWeakTextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleIconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_deepCapsuleSlotIconText, 0);
        leftStack.Children.Add(_deepCapsuleSlotIconText);

        _deepCapsuleSlotLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleLabelFontSize,
            Margin = new Thickness(CapsuleIconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_deepCapsuleSlotLabelText, 1);
        leftStack.Children.Add(_deepCapsuleSlotLabelText);
        leftArea.Child = leftStack;

        leftArea.MouseEnter += (_, _) => leftArea.Background = HoverBrush;
        leftArea.MouseLeave += (_, _) => leftArea.Background = Brushes.Transparent;
        shell.MouseEnter += (_, _) => SetDeepCapsuleHover(true);
        shell.MouseLeave += (_, _) => SetDeepCapsuleHover(false);
        leftArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _deepCapsuleSlotMouseDownScreenPos = DeepCapsuleSlotPointerScreenPosition(e);
            SetDeepCapsuleGestureState(DeepCapsuleGestureState.PendingClick);
            leftArea.CaptureMouse();
            e.Handled = true;
        };
        leftArea.PreviewMouseMove += (_, e) =>
        {
            if (IsDeepCapsuleReordering)
            {
                UpdateDeepCapsuleReorderDrag(DeepCapsuleSlotPointerScreenPosition(e));
                e.Handled = true;
                return;
            }

            if (!IsDeepCapsuleSlotPendingClick)
            {
                return;
            }

            var currentScreenPos = DeepCapsuleSlotPointerScreenPosition(e);
            var deltaX = Math.Abs(currentScreenPos.X - _deepCapsuleSlotMouseDownScreenPos.X);
            var deltaY = Math.Abs(currentScreenPos.Y - _deepCapsuleSlotMouseDownScreenPos.Y);
            if (CanReorderDeepCapsuleSlot())
            {
                // Start the drag on EITHER axis: vertical reorders within the queue, horizontal
                // carries the capsule toward another edge / monitor (resolved on release).
                if (deltaY >= SystemParameters.MinimumVerticalDragDistance + DeepCapsuleReorderDragExtraThreshold ||
                    deltaX >= SystemParameters.MinimumHorizontalDragDistance + DeepCapsuleReorderDragExtraThreshold)
                {
                    SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                    StartDeepCapsuleReorderDrag(currentScreenPos);
                    e.Handled = true;
                }

                return;
            }

            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
                deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                leftArea.ReleaseMouseCapture();
            }
        };
        leftArea.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (IsDeepCapsuleReordering)
            {
                EndDeepCapsuleReorderDrag(commit: true);
                leftArea.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (IsDeepCapsuleSlotPendingClick)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                leftArea.ReleaseMouseCapture();
                ActivateFromDeepCapsuleSlot();
                e.Handled = true;
            }
        };
        leftArea.LostMouseCapture += (_, _) =>
        {
            if (IsDeepCapsuleSlotPendingClick)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
            }
            if (IsDeepCapsuleReordering && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                EndDeepCapsuleReorderDrag(commit: false);
            }
        };
        leftArea.ContextMenu = BuildDeepCapsuleSlotContextMenu();

        Grid.SetColumn(leftArea, 0);
        shell.Children.Add(leftArea);

        var closeGlyphOffset = new TranslateTransform(CapsuleCloseGlyphDeepOffset, 0);
        _deepCapsuleSlotCloseGlyphOffset = closeGlyphOffset;
        _deepCapsuleSlotCloseGlyph = new TextBlock
        {
            Text = "×",
            Foreground = WeakTextBrush,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = closeGlyphOffset
        };

        _deepCapsuleSlotCloseArea = new Border
        {
            Width = CapsuleCloseWidth,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipHideThisPaper"),
            Child = _deepCapsuleSlotCloseGlyph
        };
        _deepCapsuleSlotCloseArea.MouseEnter += (_, _) =>
        {
            leftArea.Background = Brushes.Transparent;
            _deepCapsuleSlotCloseArea.Background = HoverBrush;
            _deepCapsuleSlotCloseGlyph.Foreground = TextBrush;
        };
        _deepCapsuleSlotCloseArea.MouseLeave += (_, _) =>
        {
            _deepCapsuleSlotCloseArea.Background = Brushes.Transparent;
            _deepCapsuleSlotCloseGlyph.Foreground = WeakTextBrush;
            _deepCapsuleSlotCloseArea.Opacity = 1.0;
        };
        _deepCapsuleSlotCloseArea.MouseLeftButtonDown += (_, e) =>
        {
            _deepCapsuleSlotCloseArea.Opacity = 0.72;
            e.Handled = true;
        };
        _deepCapsuleSlotCloseArea.MouseLeftButtonUp += (_, e) =>
        {
            _deepCapsuleSlotCloseArea.Opacity = 1.0;
            _controller.HidePaper(_paper);
            e.Handled = true;
        };

        Grid.SetColumn(_deepCapsuleSlotCloseArea, 1);
        shell.Children.Add(_deepCapsuleSlotCloseArea);

        RefreshDeepCapsuleSlotLabel();
        return shell;
    }

    private Point DeepCapsuleSlotPointerScreenPosition(MouseEventArgs e)
    {
        if (_deepCapsuleSlotShell != null && PresentationSource.FromVisual(_deepCapsuleSlotShell) != null)
        {
            return _deepCapsuleSlotShell.PointToScreen(e.GetPosition(_deepCapsuleSlotShell));
        }

        return PointToScreen(e.GetPosition(this));
    }

    private void ActivateFromDeepCapsuleSlot()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (_paper.IsCollapsed)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false, alignExpandedToDockedEdge: true);
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            _controller.BringPaperToFront(_paper);
        }
    }

    private void ShowMainWindowForDeepCapsuleActivation()
    {
        if (IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        Width = DesiredCapsuleWindowWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
        if (_deepCapsuleSlotHost != null)
        {
            Left = RoundToDevicePixelX(_deepCapsuleSlotHost.Left);
            Top = RoundToDevicePixelY(_deepCapsuleSlotHost.Top);
        }
        else
        {
            Left = _paper.X;
            Top = _paper.Y;
        }
        Show();
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
        Hide();
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
            _isApplyingCollapsedState ||
            _isTransitionVisualsActive ||
            Width <= DesiredCapsuleWindowWidth + 8 ||
            Height <= PaperLayoutDefaults.CapsuleHeight + 8 ||
            _shell.Visibility != Visibility.Visible ||
            _capsuleShell.Visibility == Visibility.Visible;
        if (!needsRestore)
        {
            return;
        }

        BeginAnimation(TransitionProgressProperty, null);
        _shell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        ResetTransitionVisuals();

        _isApplyingCollapsedState = false;
        _shell.Width = double.NaN;
        _shell.Height = double.NaN;
        _shell.Visibility = Visibility.Visible;
        _shell.Opacity = 1.0;
        _capsuleShell.Visibility = Visibility.Collapsed;
        _capsuleShell.Opacity = 0.0;
        MinWidth = PaperLayoutDefaults.MinWidth;
        MinHeight = PaperLayoutDefaults.MinHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var targetWidth = RoundToDevicePixelX(Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth));
        var targetHeight = RoundToDevicePixelY(Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight));
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = targetWidth;
            Height = targetHeight;
            if (alignToDockedEdge)
            {
                var requiredEdgeInset = _controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper)
                    ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                    : 0;
                AlignExpandedToDockedEdge(targetWidth, targetHeight, requiredEdgeInset);
            }
        });

        if (!IsVisible)
        {
            Opacity = 1.0;
            Show();
        }

        RefreshEffectiveTopmost();
    }

    public void ExpandForProgrammaticOpen()
    {
        if (!_paper.IsCollapsed)
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            return;
        }

        if (_controller.State.UseCapsuleMode &&
            _controller.State.UseDeepCapsuleMode &&
            HasDeepCapsuleSlotPlacement)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false);
            return;
        }

        if (!IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            Left = _paper.X;
            Top = _paper.Y;
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            Show();
        }

        SetCollapsedState(false);
    }

    private void UpdateDeepCapsuleSlotHostTheme()
    {
        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Background = PaperBrush;
            _deepCapsuleSlotChrome.BorderBrush = PaperBorderBrush;
        }

        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.BorderBrush = Theme.CapsuleFocusBorderBrush;
            _deepCapsuleSlotOutline.Visibility = IsDeepCapsuleSlotActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            _deepCapsuleSlotLabelText.Foreground = WeakTextBrush;
        }

        if (_deepCapsuleSlotIconText != null)
        {
            _deepCapsuleSlotIconText.Foreground = BrightWeakTextBrush;
        }

        if (_deepCapsuleSlotCloseGlyph != null)
        {
            _deepCapsuleSlotCloseGlyph.Foreground = WeakTextBrush;
        }

    }

    private void MoveExpandedDeepCapsuleSlotHost(
        double targetLeft,
        double targetTop,
        double visibleWidth,
        bool animate,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false)
    {
        var host = EnsureDeepCapsuleSlotHost();
        var rightEdge = targetLeft + visibleWidth;
        var viewportWidth = visibleWidth;
        var targetHostLeft = RoundToDevicePixelX(rightEdge - viewportWidth);
        // Pin the docked WALL edge to the device-pixel grid every frame.
        //
        // Rounding is non-linear: Round(Left) + Round(Width) != Round(Left + Width). The old code
        // set host.Left = Round(left) and host.Width = Round(width) independently, so the host's
        // right edge (Left + Width) landed at Round(left)+Round(width), which drifts up to ~1px
        // around the true wall as the animated width changes frame to frame. At 125% DPI (0.8 DIP
        // grid) the right edge jittered ~2047.6 <-> 2048.4 against a 2048 wall — a sub-pixel gap
        // that, combined with the pill's 1px border, rendered as a thin seam flickering off the
        // wall during the reveal/move animation.
        //
        // This bit ONLY the right dock, and the asymmetry is the giveaway: the left dock anchors
        // host.Left = Round(area.Left) = Round(0) = 0 — a single rounded point that is exact by
        // construction, no addition involved. The right dock has no directly-settable wall edge;
        // it can only be reached as Left + Width, i.e. a sum of two independent roundings. So the
        // wall edge that is *derived* (right) jitters, while the wall edge that is *set* (left)
        // never does.
        //
        // Fix: round the two screen edges, then derive width from their difference. host.Left +
        // wallExactViewportWidth == Round(rightEdge), so the docked wall edge is pixel-exact on
        // both sides (right dock: rightEdge == area.Right; left dock: targetHostLeft == area.Left).
        // All settle / first-show / animation-completed paths below use wallExactViewportWidth so
        // the wall never drifts in any state.
        var wallExactViewportWidth = RoundToDevicePixelX(rightEdge) - targetHostLeft;
        host.Height = PaperLayoutDefaults.CapsuleHeight;
        if (!keepHiding)
        {
            if (IsDeepCapsuleSlotRetracting)
            {
                SetDeepCapsuleSlotState(_paper.IsCollapsed
                    ? DeepCapsuleSlotState.CollapsedDocked
                    : DeepCapsuleSlotState.None);
            }
        }
        _deepCapsuleSlotTop = targetTop;
        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHostRoot.Opacity = 1;
            _deepCapsuleSlotHostRoot.IsHitTestVisible = !keepHiding;
        }

        if (!host.IsVisible)
        {
            host.BeginAnimation(Window.OpacityProperty, null);
            host.Left = targetHostLeft;
            host.Top = targetTop;
            ApplyDeepCapsuleSlotHostViewport(wallExactViewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
            host.Opacity = _isCollapseAllRetracted ? 0 : 1;
            host.Show();
            RefreshEffectiveTopmost();
            return;
        }

        host.BeginAnimation(Window.OpacityProperty, null);
        if (!_isCollapseAllRetracted)
        {
            host.Opacity = 1;
        }

        var generation = ++_deepCapsuleSlotMoveGeneration;
        if (!animate)
        {
            host.BeginAnimation(Window.LeftProperty, null);
            host.BeginAnimation(Window.TopProperty, null);
            ClearDeepCapsuleSlotHorizontalAnimation();
            host.Left = targetHostLeft;
            host.Top = targetTop;
            ApplyDeepCapsuleSlotHostViewport(wallExactViewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
            _deepCapsuleSlotTop = targetTop;
            return;
        }

        var currentTop = double.IsNaN(host.Top) || double.IsInfinity(host.Top) ? targetTop : RoundToDevicePixelY(host.Top);
        var currentHostLeft = double.IsNaN(host.Left) || double.IsInfinity(host.Left) ? targetHostLeft : RoundToDevicePixelX(host.Left);
        var currentViewportWidth = double.IsNaN(host.Width) || double.IsInfinity(host.Width) || host.Width <= 0
            ? viewportWidth
            : RoundToDevicePixelX(host.Width);
        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        host.BeginAnimation(Window.LeftProperty, null);
        var targetRight = targetHostLeft + viewportWidth;
        var currentRight = currentHostLeft + currentViewportWidth;
        var needsHorizontalAnimation =
            Math.Abs(currentHostLeft - targetHostLeft) >= 0.5 ||
            Math.Abs(currentRight - targetRight) >= 0.5 ||
            Math.Abs(currentViewportWidth - viewportWidth) >= 0.5;
        if (needsHorizontalAnimation)
        {
            _deepCapsuleSlotTargetLeft = targetHostLeft;
            _deepCapsuleSlotStartViewportWidth = currentViewportWidth;
            // Use the wall-exact target width (not the raw viewportWidth) so the animator's
            // per-frame right edge — a constant targetLeft + targetViewportWidth — lands on
            // Round(rightEdge) = the wall every frame. With the raw width the seam reappeared
            // DURING the hover reveal animation, then snapped flush only when Completed ran.
            _deepCapsuleSlotTargetViewportWidth = wallExactViewportWidth;

            ApplyDeepCapsuleSlotHostViewport(currentViewportWidth);
            ApplyDeepCapsuleSlotHorizontalProgress(0.0);
            var horizontalAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = easeOut
            };
            horizontalAnim.Completed += (_, _) =>
            {
                if (generation != _deepCapsuleSlotMoveGeneration)
                {
                    return;
                }

                ClearDeepCapsuleSlotHorizontalAnimation();
                ApplyDeepCapsuleSlotHostViewport(wallExactViewportWidth);
                host.Left = targetHostLeft;
                _deepCapsuleSlotLeft = targetHostLeft;
            };
            BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, horizontalAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            host.Left = targetHostLeft;
            ApplyDeepCapsuleSlotHostViewport(wallExactViewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
        }

        if (Math.Abs(currentTop - targetTop) >= 0.5)
        {
            var topAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = currentTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = easeOut
            };
            topAnim.Completed += (_, _) =>
            {
                if (generation != _deepCapsuleSlotMoveGeneration)
                {
                    return;
                }

                host.BeginAnimation(Window.TopProperty, null);
                host.Top = targetTop;
                _deepCapsuleSlotTop = targetTop;
            };
            host.BeginAnimation(Window.TopProperty, topAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            host.BeginAnimation(Window.TopProperty, null);
            host.Top = targetTop;
            _deepCapsuleSlotTop = targetTop;
        }
    }

    private void AnimateSlotHostOpacity(double to, bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (!animate || Math.Abs(_deepCapsuleSlotHost.Opacity - to) < 0.001)
        {
            _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, null);
            _deepCapsuleSlotHost.Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = _deepCapsuleSlotHost.Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotOpacityFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        anim.Completed += (_, _) =>
        {
            _deepCapsuleSlotHost?.BeginAnimation(Window.OpacityProperty, null);
            if (_deepCapsuleSlotHost != null)
            {
                _deepCapsuleSlotHost.Opacity = to;
            }
        };
        _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, anim);
    }

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (!_paper.IsCollapsed && _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
        }
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        ClearDeepCapsuleSlotHorizontalAnimation();
        _deepCapsuleSlotHost.Content = null;
        _deepCapsuleSlotHost.Close();
        _deepCapsuleSlotHost = null;
        _deepCapsuleSlotHostRoot = null;
        _deepCapsuleSlotChrome = null;
        _deepCapsuleSlotOutline = null;
        _deepCapsuleSlotShell = null;
        _deepCapsuleSlotIconText = null;
        _deepCapsuleSlotCloseArea = null;
        _deepCapsuleSlotCloseGlyph = null;
        _deepCapsuleSlotCloseGlyphOffset = null;
        _deepCapsuleSlotLabelText = null;
    }

    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);

        menu.Opened += (_, _) =>
        {
            if (_deepCapsuleSlotContextMenu != null && !ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu.IsOpen = false;
            }

            _deepCapsuleSlotContextMenu = menu;
            StartDeepCapsuleContextMenuGuards();
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu = null;
                StopDeepCapsuleContextMenuGuards();
            }
        };

        return menu;
    }

    private void CloseDeepCapsuleSlotContextMenu()
    {
        var menu = _deepCapsuleSlotContextMenu;
        if (menu != null)
        {
            menu.IsOpen = false;
        }

        _deepCapsuleSlotContextMenu = null;
        StopDeepCapsuleContextMenuGuards();
    }

    private void StartDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook == IntPtr.Zero)
        {
            _deepCapsuleForegroundHookProc = OnDeepCapsuleForegroundChanged;
            _deepCapsuleForegroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _deepCapsuleForegroundHookProc,
                0,
                0,
                WineventOutOfContext);
        }

        if (_deepCapsuleMouseHook == IntPtr.Zero)
        {
            _deepCapsuleMouseHookProc = OnDeepCapsuleMouseHook;
            _deepCapsuleMouseHook = SetWindowsHookEx(WhMouseLl, _deepCapsuleMouseHookProc, GetModuleHandle(null), 0);
        }
    }

    private void StopDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_deepCapsuleForegroundHook);
            _deepCapsuleForegroundHook = IntPtr.Zero;
        }

        _deepCapsuleForegroundHookProc = null;

        if (_deepCapsuleMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_deepCapsuleMouseHook);
            _deepCapsuleMouseHook = IntPtr.Zero;
        }

        _deepCapsuleMouseHookProc = null;
    }

    private void OnDeepCapsuleForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_deepCapsuleSlotContextMenu?.IsOpen != true || hwnd == IntPtr.Zero || IsWindowFromCurrentProcess(hwnd))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
    }

    private IntPtr OnDeepCapsuleMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam) && _deepCapsuleSlotContextMenu?.IsOpen == true)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var screenPoint = new Point(hook.Point.X, hook.Point.Y);
            if (!IsPointInsideDeepCapsuleContextSurface(screenPoint))
            {
                Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
            }
        }

        return CallNextHookEx(_deepCapsuleMouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideDeepCapsuleContextSurface(Point screenPoint)
    {
        if (IsPointInsideElement(_deepCapsuleSlotContextMenu, screenPoint))
        {
            return true;
        }

        return _deepCapsuleSlotHost?.IsVisible == true && IsPointInsideWindow(_deepCapsuleSlotHost, screenPoint);
    }

    private static bool IsPointInsideElement(FrameworkElement? element, Point screenPoint)
    {
        if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localPoint = element.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= element.ActualWidth &&
                localPoint.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPointInsideWindow(Window window, Point screenPoint)
    {
        try
        {
            var localPoint = window.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= window.ActualWidth &&
                localPoint.Y <= window.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsMouseButtonDownMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown ||
            value == WmRButtonDown ||
            value == WmMButtonDown ||
            value == WmXButtonDown;
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    private void ClearDeepCapsuleSlotHorizontalAnimation()
    {
        BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, null);
    }

    private bool IsDeepCapsuleSlotHorizontalAnimating => !double.IsNaN(DeepCapsuleSlotHorizontalProgress);

    // ── This window's OWN queue identity. A queue is (monitor, edge); each docked capsule
    // resolves its geometry against its own queue, not a single global anchor. This is what
    // lets one capsule sit on the left edge of monitor A while another sits on the right of B.
    private DeepCapsuleEdge MyDeepCapsuleEdge =>
        _paper.CapsuleSide == DeepCapsuleSides.Left ? DeepCapsuleEdge.Left : DeepCapsuleEdge.Right;

    private bool MyDeepCapsuleIsLeftEdge => MyDeepCapsuleEdge == DeepCapsuleEdge.Left;

    private Rect DeepCapsuleWorkArea()
    {
        return DeepCapsuleLayout.WorkAreaForQueue(_paper.CapsuleMonitorDeviceName);
    }

    private double MyDockedLeft(Rect area, double visibleWidth)
    {
        return DeepCapsuleLayout.DockedLeft(area, visibleWidth, MyDeepCapsuleEdge);
    }

    private double MyTopForIndex(int index)
    {
        return DeepCapsuleLayout.TopForIndex(index, _controller.DeepCapsuleStartTopMarginFor(_paper), DeepCapsuleWorkArea());
    }

    private void ApplyDeepCapsuleSlotHostViewport(double viewportWidth)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleSlotHost.Width = DeepCapsuleSlotViewportWidth(viewportWidth);
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        ApplyDeepCapsuleSlotFixedLayout();
    }

    private void ApplyDeepCapsuleSlotFixedLayout()
    {
        ApplyDeepCapsuleSlotEdgeLayout();
        var fullWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var leftEdge = MyDeepCapsuleIsLeftEdge;

        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Margin = leftEdge
                ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
                : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            // Anchor the pill body to its INTERIOR edge and let it overflow toward the wall, where
            // the host's ClipToBounds cuts it flush. Stretch+fixed-Width is ambiguous (can center,
            // leaving a gap from the wall), so pin explicitly: left dock→Right, right dock→Left.
            _deepCapsuleSlotChrome.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _deepCapsuleSlotChrome.Width = Math.Max(0, fullWidth - WindowChromeMargin);
        }
        if (_deepCapsuleSlotShell != null)
        {
            _deepCapsuleSlotShell.Margin = leftEdge
                ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
                : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotShell.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }
        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.Margin = leftEdge
                ? new Thickness(0, outlineMargin, outlineMargin, outlineMargin)
                : new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
            _deepCapsuleSlotOutline.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _deepCapsuleSlotOutline.Width = Math.Max(0, fullWidth - outlineMargin);
        }
    }

    // Flip the slot shell's internal layout to the anchored edge: content cap toward the
    // interior, close button tucked past the wall. Right edge (default) keeps content in
    // column 0 / close in column 1; left edge swaps them and mirrors the rounded caps.
    private void ApplyDeepCapsuleSlotEdgeLayout()
    {
        // This window's OWN queue edge — NOT the global anchor. With per-edge queues a capsule on
        // the left edge must flip its column order / close area / corner radii independently of
        // whatever edge other queues use.
        var edge = MyDeepCapsuleEdge;
        if (_appliedSlotEdge == edge ||
            _deepCapsuleSlotLeftArea == null ||
            _deepCapsuleSlotCloseArea == null ||
            _deepCapsuleSlotLeftStack == null)
        {
            return;
        }

        _appliedSlotEdge = edge;
        var leftEdge = edge == DeepCapsuleEdge.Left;

        // Content (icon + title) on the interior side; close on the wall side.
        Grid.SetColumn(_deepCapsuleSlotLeftArea, leftEdge ? 1 : 0);
        Grid.SetColumn(_deepCapsuleSlotCloseArea, leftEdge ? 0 : 1);

        _deepCapsuleSlotLeftArea.CornerRadius = leftEdge
            ? new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0)
            : new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius);
        _deepCapsuleSlotCloseArea.CornerRadius = leftEdge
            ? new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius)
            : new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0);

        _deepCapsuleSlotLeftStack.Margin = leftEdge
            ? new Thickness(0, 0, CapsuleLeftPadding, 0)
            : new Thickness(CapsuleLeftPadding, 0, 0, 0);

        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: false);
    }

    private void ApplyDeepCapsuleSlotHorizontalProgress(double progress)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        progress = Math.Clamp(progress, 0.0, 1.0);
        var viewportWidth = Lerp(_deepCapsuleSlotStartViewportWidth, _deepCapsuleSlotTargetViewportWidth, progress);
        // Right edge: pin the right side (target + width) and grow leftward.
        // Left edge:  the docked left is fixed at the screen edge; grow rightward.
        var left = MyDeepCapsuleIsLeftEdge
            ? _deepCapsuleSlotTargetLeft
            : (_deepCapsuleSlotTargetLeft + _deepCapsuleSlotTargetViewportWidth) - viewportWidth;
        var right = left + viewportWidth;

        // Round the two screen edges, then derive width from their difference — NOT Round(left)
        // plus an independently Round(width), which lets host.Right drift ~1px off the wall as the
        // animated width changes (non-linear rounding: Round(a)+Round(b) != Round(a+b)). Only the
        // right dock showed it; the left dock's wall is host.Left = Round(0), exact by construction.
        // Same wall-exact rule as the settle paths in MoveExpandedDeepCapsuleSlotHost — see the
        // detailed comment there.
        var roundedLeft = RoundToDevicePixelX(left);
        var roundedRight = RoundToDevicePixelX(right);
        _deepCapsuleSlotHost.Left = roundedLeft;
        _deepCapsuleSlotHost.Width = DeepCapsuleSlotViewportWidth(roundedRight - roundedLeft);
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private double DeepCapsuleSlotShellLayoutWidth()
    {
        var fullWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        return Math.Max(0, Math.Max(
            CapsuleShellWidth(usesDeepCapsulePresentation: true),
            fullWidth - WindowChromeMargin));
    }

    private double DeepCapsuleSlotViewportWidth(double viewportWidth)
    {
        return Math.Clamp(viewportWidth, 1, CapsuleWindowWidth(usesDeepCapsulePresentation: true));
    }

    private double DeepCapsuleTopForIndex(int index)
    {
        return MyTopForIndex(index);
    }

    private void MoveDeepCapsuleToCurrentTarget(
        bool animate = false,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false,
        bool forceRestingOffset = false)
    {
        if (!HasDeepCapsuleSlotPlacement || _isCollapseAllRetracted)
        {
            return;
        }

        var area = DeepCapsuleWorkArea();
        var capsuleWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        var deepCapsuleVisibleWidth = DeepCapsuleVisibleWidth();
        var shouldUseActiveOffset = !keepHiding &&
            !forceRestingOffset &&
            (_deepCapsuleVisualState is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active);
        var visibleWidth = shouldUseActiveOffset
            ? ExpandedDeepCapsuleVisibleWidth()
            : deepCapsuleVisibleWidth;
        var targetLeft = RoundToDevicePixelX(MyDockedLeft(area, visibleWidth));
        var targetTop = RoundToDevicePixelY(DeepCapsuleTopForIndex(_deepCapsuleIndex + _deepCapsuleVisualOffset));

        MoveExpandedDeepCapsuleSlotHost(
            targetLeft,
            targetTop,
            visibleWidth,
            animate,
            durationMs,
            keepHiding);
    }

    private double DeepCapsuleVisibleWidth()
    {
        var capsuleWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        // Resting edge-attached state is a docked tag, not a cropped full capsule. Keep the
        // close area fully off-screen and size the visible part to the icon + title only.
        return Math.Clamp(
            WindowChromeMargin + CapsuleLeftPadding + MeasureCapsuleIconWidth() + CapsuleIconGap + MeasureCapsuleTitleWidth() + 4,
            34,
            Math.Max(34, capsuleWidth - WindowChromeMargin - 24));
    }

    private double ExpandedDeepCapsuleVisibleWidth()
    {
        return DeepCapsuleLayout.FocusVisibleWidth(
            CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            DeepCapsuleVisibleWidth());
    }

    // Slide this capsule up to the master's slot and fade it out. The window stays shown
    // (so it keeps counting as a deep-capsule member) but, being a per-pixel transparent
    // window at Opacity 0, it is fully click-through and never blocks the master pill.
    public void RetractIntoMaster(double anchorTop, bool animate)
    {
        if (!OccupiesDeepCapsuleSlot || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode || !_paper.IsVisible)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
        _isCollapseAllRetracted = true;
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        RefreshEffectiveTopmost();

        var area = DeepCapsuleWorkArea();
        var currentSlotVisible = _deepCapsuleSlotHost?.IsVisible == true &&
            !double.IsNaN(_deepCapsuleSlotHost.Width) &&
            !double.IsInfinity(_deepCapsuleSlotHost.Width) &&
            _deepCapsuleSlotHost.Width > 0;
        var visibleWidth = currentSlotVisible
            ? DeepCapsuleSlotViewportWidth(_deepCapsuleSlotHost!.Width)
            : DeepCapsuleVisibleWidth();
        var targetLeft = currentSlotVisible &&
            !double.IsNaN(_deepCapsuleSlotHost!.Left) &&
            !double.IsInfinity(_deepCapsuleSlotHost.Left)
                ? RoundToDevicePixelX(_deepCapsuleSlotHost.Left)
                : RoundToDevicePixelX(MyDockedLeft(area, visibleWidth));
        var targetTop = RoundToDevicePixelY(anchorTop);

        MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate);
        if (_deepCapsuleSlotHost != null)
        {
            AnimateSlotHostOpacity(0.0, animate);
        }
        if (_paper.IsCollapsed)
        {
            HideMainWindowForDeepCapsuleRest();
        }
    }

    private void SetDeepCapsuleHover(bool hovering)
    {
        if (IsDeepCapsuleReordering || !HasDeepCapsuleSlotPlacement || !_paper.IsCollapsed || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (IsDeepCapsuleSlotActive)
        {
            return;
        }

        SetDeepCapsuleVisualState(hovering ? DeepCapsuleVisualState.Hovered : DeepCapsuleVisualState.Resting);
        MoveDeepCapsuleToCurrentTarget(animate: true);
    }

    public void ApplyDeepCapsulePlacement(int index, bool animate = false, int visualOffset = 0)
    {
        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        var keepActiveUntilRetracted = animate &&
            IsDeepCapsuleSlotActive &&
            _deepCapsuleSlotHost?.IsVisible == true;

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
        if (!_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        RefreshCapsuleLabel();
        MoveDeepCapsuleToCurrentTarget(
            animate,
            keepActiveUntilRetracted ? DeepCapsuleLayout.SlotRetractMoveMilliseconds : DeepCapsuleLayout.SlotMoveMilliseconds,
            forceRestingOffset: keepActiveUntilRetracted);
        if (keepActiveUntilRetracted)
        {
            ClearDeepCapsuleSlotActiveAfterMove(DeepCapsuleLayout.SlotRetractMoveMilliseconds);
        }
        AnimateSlotHostOpacity(1.0, animate);
        if (!_isApplyingCollapsedState)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        RefreshEffectiveTopmost();
    }

    public void ApplyExpandedDeepCapsuleSlotPlacement(int index, bool animate = false, int visualOffset = 0)
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

        SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
        SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        RefreshCapsuleLabel();
        UpdateDeepCapsuleSlotHostTheme();

        var area = DeepCapsuleWorkArea();
        var visibleWidth = ExpandedDeepCapsuleVisibleWidth();
        var targetLeft = RoundToDevicePixelX(MyDockedLeft(area, visibleWidth));
        var targetTop = RoundToDevicePixelY(DeepCapsuleTopForIndex(index + visualOffset));
        RefreshDeepCapsuleSlotLabel();

        var firstShow = _deepCapsuleSlotHost?.IsVisible != true;
        if (firstShow)
        {
            MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate: false);
        }
        else
        {
            MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate);
        }
        UpdateDeepCapsuleSlotClosePlacement();
        AnimateSlotHostOpacity(1.0, animate);
        RefreshEffectiveTopmost();
        UpdateToolTipSetting();
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        var wasActive = IsDeepCapsuleSlotActive;
        var keepActiveUntilRetracted = animate &&
            wasActive &&
            _paper.IsCollapsed &&
            HasDeepCapsuleSlotPlacement &&
            _deepCapsuleSlotHost?.IsVisible == true;
        _deepCapsuleSlotMoveGeneration++;
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        if (!_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (wasActive && !_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.Retracting)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: !_paper.IsCollapsed || !HasDeepCapsuleSlotPlacement);

        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            MoveDeepCapsuleToCurrentTarget(
                animate,
                keepActiveUntilRetracted ? DeepCapsuleLayout.SlotRetractMoveMilliseconds : DeepCapsuleLayout.SlotMoveMilliseconds,
                forceRestingOffset: keepActiveUntilRetracted);
        }
    }

    private void ClearDeepCapsuleSlotActiveAfterMove(int durationMs)
    {
        var generation = _deepCapsuleSlotMoveGeneration;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(durationMs + 20)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _deepCapsuleSlotMoveGeneration)
            {
                return;
            }

            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
        };
        timer.Start();
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (!animate || !_deepCapsuleSlotHost.IsVisible || _deepCapsuleSlotHostRoot == null)
        {
            if (IsDeepCapsuleSlotRetracting)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            _deepCapsuleSlotHostRoot?.BeginAnimation(UIElement.OpacityProperty, null);
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
            }
            ClearDeepCapsuleSlotHorizontalAnimation();
            _deepCapsuleSlotHost.Hide();
            return;
        }

        if (IsDeepCapsuleSlotRetracting)
        {
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.Retracting);
        _deepCapsuleSlotHostRoot.IsHitTestVisible = false;
        var hideGeneration = _deepCapsuleSlotMoveGeneration;
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = _deepCapsuleSlotHostRoot.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotRetractFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (hideGeneration != _deepCapsuleSlotMoveGeneration || _deepCapsuleSlotHost == null)
            {
                return;
            }

            if (_deepCapsuleSlotState == DeepCapsuleSlotState.Retracting)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            _deepCapsuleSlotHostRoot?.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHost.Hide();
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
                _deepCapsuleSlotHostRoot.IsHitTestVisible = true;
            }
        };
        _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void RetractAndHideDeepCapsuleSlotHost(bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        var root = _deepCapsuleSlotHostRoot;
        if (!animate || !_deepCapsuleSlotHost.IsVisible || root == null || _deepCapsuleSlotHost.Opacity < 0.05)
        {
            HideExpandedDeepCapsuleSlotHost(animate: false);
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.Retracting);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: false);

        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1.0;
        root.IsHitTestVisible = false;

        MoveDeepCapsuleToCurrentTarget(animate: true, durationMs: DeepCapsuleLayout.SlotRetractMoveMilliseconds, keepHiding: true);
        var generation = _deepCapsuleSlotMoveGeneration;

        var finishTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotReleaseSettleMilliseconds)
        };
        finishTimer.Tick += (_, _) =>
        {
            finishTimer.Stop();
            BeginDeepCapsuleSlotHideFade(generation);
        };
        finishTimer.Start();
    }

    private void BeginDeepCapsuleSlotHideFade(int generation)
    {
        if (_deepCapsuleSlotHostRoot == null)
        {
            return;
        }

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotReleaseFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (generation != _deepCapsuleSlotMoveGeneration || _deepCapsuleSlotHost == null)
            {
                return;
            }

            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            _isCollapseAllRetracted = false;
            _deepCapsuleVisualOffset = 0;
            _deepCapsuleIndex = -1;
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
            _deepCapsuleSlotHost.Hide();
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
                _deepCapsuleSlotHostRoot.IsHitTestVisible = true;
            }
        };
        _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    public void ClearDeepCapsulePlacement(bool restoreCollapsedPosition = true, bool animate = false)
    {
        var shouldRetractBeforeHide = animate &&
            _deepCapsuleSlotHost?.IsVisible == true &&
            _deepCapsuleSlotHostRoot != null &&
            HasDeepCapsuleSlotPlacement &&
            !_isCollapseAllRetracted;

        if (shouldRetractBeforeHide)
        {
            RetractAndHideDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            _isCollapseAllRetracted = false;
            _deepCapsuleVisualOffset = 0;
            _deepCapsuleIndex = -1;
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
    }

    public void ClearDeepCapsuleSlotReservation(bool animate = false)
    {
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        ClearExpandedDeepCapsuleSlotPlacement(animate);
    }

    // Fully remove this window from the deep-capsule stack: tears down the docked/collapsed slot
    // AND any expanded reservation, and hides the slot-host window. This is the single correct
    // call for "this paper is leaving the stack" (hidden, mode off, surface restored). Callers
    // must NOT also call ClearDeepCapsuleSlotReservation afterward — ClearDeepCapsulePlacement
    // already resets the reservation, so the pair was a redundant no-op and an easy mis-pairing.
    public void DetachFromDeepCapsuleStack(bool animate = false)
    {
        ClearDeepCapsulePlacement(animate: animate);
    }

    public void UpdateDeepCapsuleMode()
    {
        if (!_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            ClearDeepCapsulePlacement();
        }
        else if (!_paper.IsCollapsed)
        {
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false);
        }
        else
        {
            MoveDeepCapsuleToCurrentTarget();
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
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
            _isCollapseAllRetracted = false;
            RefreshCapsuleLabel();
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
            return;
        }

        if (!_controller.State.ShowDeepCapsuleWhileExpanded && HoldsDeepCapsuleSlotWhileExpanded)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false, animate: _controller.State.EnableAnimations);
        }
    }

    private void StartDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() || _deepCapsuleSlotHost == null)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Reordering);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Hovered);
        // currentScreenPos is in physical device pixels (PointToScreen); Top is in DIPs.
        // Convert to DIPs so the capsule tracks the cursor 1:1 at any DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiScaleY = dpi.DpiScaleY;
        var dpiScaleX = Math.Max(0.1, dpi.DpiScaleX);
        _deepCapsuleDragMouseOffsetY = currentScreenPos.Y / dpiScaleY - _deepCapsuleSlotHost.Top;
        _deepCapsuleDragMouseOffsetX = currentScreenPos.X / dpiScaleX - _deepCapsuleSlotHost.Left;

        _deepCapsuleSlotHost.BeginAnimation(Window.LeftProperty, null);
        _deepCapsuleSlotHost.BeginAnimation(Window.TopProperty, null);

        var area = DeepCapsuleWorkArea();
        var visibleWidth = ExpandedDeepCapsuleVisibleWidth();
        _deepCapsuleDragLeft = RoundToDevicePixelX(MyDockedLeft(area, visibleWidth));

        _deepCapsuleSlotHost.Left = _deepCapsuleDragLeft;
        ApplyDeepCapsuleSlotHostViewport(visibleWidth);
        _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;

        Mouse.OverrideCursor = Cursors.SizeAll;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        // Free 2D follow: the dragged capsule tracks the cursor across the desktop so it can be
        // carried to another edge or monitor. It is NOT free-placed — on release it snaps to the
        // (monitor, edge) queue under the cursor. While dragging it floats under the pointer.
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiScaleX = Math.Max(0.1, dpi.DpiScaleX);
        var dpiScaleY = Math.Max(0.1, dpi.DpiScaleY);
        var cursorDipX = currentScreenPos.X / dpiScaleX;
        var cursorDipY = currentScreenPos.Y / dpiScaleY;
        _deepCapsuleDragLastDip = new Point(cursorDipX, cursorDipY);

        if (_deepCapsuleSlotHost != null)
        {
            _deepCapsuleSlotHost.Left = RoundToDevicePixelX(cursorDipX - _deepCapsuleDragMouseOffsetX);
            _deepCapsuleSlotHost.Top = RoundToDevicePixelY(cursorDipY - _deepCapsuleDragMouseOffsetY);
            _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
            _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
        }
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
        Mouse.OverrideCursor = null;
        SetDeepCapsuleVisualState(
            _deepCapsuleSlotShell?.IsMouseOver == true
                ? DeepCapsuleVisualState.Hovered
                : DeepCapsuleVisualState.Resting);

        if (commit)
        {
            // Resolve the (monitor, edge) queue under the drop point. If it differs from this
            // paper's current queue, reassign it (cross-edge / cross-monitor move). Otherwise it's
            // a plain vertical reorder within the same queue.
            var resolved = WindowWorkAreaHelper.MonitorAtScreenPoint(_deepCapsuleDragLastDip);
            string targetMonitor;
            Rect targetArea;
            if (resolved.HasValue)
            {
                targetMonitor = resolved.Value.DeviceName;
                targetArea = resolved.Value.WorkArea;
            }
            else
            {
                targetMonitor = _paper.CapsuleMonitorDeviceName;
                targetArea = DeepCapsuleWorkArea();
            }

            // Nearer edge of the target monitor by the drop X (left half => left, else right).
            var targetSide = _deepCapsuleDragLastDip.X < targetArea.Left + targetArea.Width / 2
                ? DeepCapsuleSides.Left
                : DeepCapsuleSides.Right;

            var queueChanged = targetSide != _paper.CapsuleSide ||
                !string.Equals(targetMonitor, _paper.CapsuleMonitorDeviceName, StringComparison.Ordinal);

            if (queueChanged)
            {
                _controller.MoveCapsuleToQueue(_paper, targetMonitor, targetSide, _deepCapsuleDragLastDip.Y);
            }
            else
            {
                _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
            }
            return;
        }

        MoveDeepCapsuleToCurrentTarget();
    }

    private bool CanReorderDeepCapsuleSlot()
    {
        return HasDeepCapsuleSlotPlacement &&
            _deepCapsuleSlotHost?.IsVisible == true &&
            (_paper.IsCollapsed || (_controller.State.ShowDeepCapsuleWhileExpanded && IsDeepCapsuleSlotActive));
    }

    private int DeepCapsuleDropIndexForCurrentPosition()
    {
        var count = _controller.VisibleDeepCapsuleCountForQueue(_paper);
        if (count <= 1)
        {
            return 0;
        }

        var centerY = (_deepCapsuleSlotHost?.Top ?? _deepCapsuleSlotTop) + (PaperLayoutDefaults.CapsuleHeight / 2);
        var area = DeepCapsuleWorkArea();
        // Real capsules start at slot _deepCapsuleVisualOffset when the master capsule occupies slot 0.
        var firstCenterY = DeepCapsuleTopForIndex(_deepCapsuleVisualOffset) + (PaperLayoutDefaults.CapsuleHeight / 2);
        var slotHeight = PaperLayoutDefaults.CapsuleHeight + DeepCapsuleGap;
        var originalIndex = Math.Clamp(_deepCapsuleIndex, 0, count - 1);
        var rawIndex = (centerY - firstCenterY) / slotHeight;
        var index = rawIndex >= originalIndex
            ? (int)Math.Floor(rawIndex)
            : (int)Math.Ceiling(rawIndex);
        return Math.Clamp(index, 0, count - 1);
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

    private void RegisterNameSafe(string name, object scopedElement)
    {
        try
        {
            UnregisterName(name);
        }
        catch
        {
            // Name may not exist yet.
        }

        try
        {
            RegisterName(name, scopedElement);
        }
        catch
        {
            // Duplicate names are non-fatal for this small UI.
        }
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject target)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
            current = GetSafeParent(current);
        }
        return false;
    }

    private static bool IsScrollBarInteractionSource(DependencyObject? current, DependencyObject scope)
    {
        while (current != null)
        {
            if (current is ScrollBar or Thumb or Track or RepeatButton)
            {
                return true;
            }

            if (ReferenceEquals(current, scope))
            {
                return false;
            }

            current = GetSafeParent(current);
        }

        return false;
    }

}
