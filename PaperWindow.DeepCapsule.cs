using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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

        _deepCapsuleSlotDragScale = new ScaleTransform(1, 1);
        _deepCapsuleSlotHostRoot = new Grid
        {
            Background = null,
            ClipToBounds = true,
            Opacity = 1,
            RenderTransform = _deepCapsuleSlotDragScale,
            RenderTransformOrigin = new Point(0.5, 0.5)
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
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = !_controller.SuppressTopmostForFullscreenForeground,
            Width = CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            Height = PaperLayoutDefaults.CapsuleHeight,
            Content = _deepCapsuleSlotHostRoot
        };
        host.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(host);
            if (PresentationSource.FromVisual(host) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }
        };
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
            Text = CapsuleIconText(),
            Foreground = BrightWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = CapsuleIconFontSizeForCurrentPaper(),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_deepCapsuleSlotIconText, 0);
        leftStack.Children.Add(_deepCapsuleSlotIconText);

        _deepCapsuleSlotLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
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
                // Start tracking on either axis, but keep the capsule magneted to its edge first.
                // A larger outward pull below unlocks the cross-edge / cross-monitor drag.
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
                ClearCapsuleInteractionKeyboardFocus();
                e.Handled = true;
                return;
            }

            if (IsDeepCapsuleSlotPendingClick)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                leftArea.ReleaseMouseCapture();
                try
                {
                    ActivateFromDeepCapsuleSlot();
                }
                finally
                {
                    ClearCapsuleInteractionKeyboardFocus();
                }
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
                ClearCapsuleInteractionKeyboardFocus();
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
            FontFamily = AppTypography.SymbolFontFamily,
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
            ClearCapsuleInteractionKeyboardFocus();
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
        if (TryRunScriptCapsule())
        {
            return;
        }

        if (_paper.IsCollapsed)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false, alignExpandedToDockedEdge: true);
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
        MoveWindowWithoutGeometrySave(() =>
        {
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
        });
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

        SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
        MoveWindowWithoutGeometrySave(() =>
        {
            Left = RoundToDevicePixelX(rememberedGeometry.Left);
            Top = RoundToDevicePixelY(rememberedGeometry.Top);
            Width = RoundToDevicePixelX(rememberedGeometry.Width);
            Height = RoundToDevicePixelY(rememberedGeometry.Height);
        });
        return true;
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
            UpdateDeepCapsuleSlotOutlineState();
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            _deepCapsuleSlotLabelText.Foreground = WeakTextBrush;
        }

        if (_deepCapsuleSlotIconText != null)
        {
            _deepCapsuleSlotIconText.Text = CapsuleIconText();
            _deepCapsuleSlotIconText.FontSize = CapsuleIconFontSizeForCurrentPaper();
            _deepCapsuleSlotIconText.Foreground = BrightWeakTextBrush;
        }

        if (_deepCapsuleSlotCloseGlyph != null)
        {
            _deepCapsuleSlotCloseGlyph.Foreground = WeakTextBrush;
        }

    }

    private void UpdateDeepCapsuleSlotOutlineState()
    {
        if (_deepCapsuleSlotOutline == null)
        {
            return;
        }

        _deepCapsuleSlotOutline.Visibility = IsDeepCapsuleSlotActive
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MoveExpandedDeepCapsuleSlotHost(
        double targetLeft,
        double targetTop,
        double visibleWidth,
        bool animate,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        var host = EnsureDeepCapsuleSlotHost();
        var rightEdge = targetLeft + visibleWidth;
        var viewportWidth = visibleWidth;
        var leftEdge = MyDeepCapsuleIsLeftEdge;
        var targetHostLeft = leftEdge
            ? RoundToDevicePixelX(targetLeft)
            : RoundToDevicePixelX(rightEdge - viewportWidth);
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
        var wallExactViewportWidth = RoundToDevicePixelX(targetLeft + viewportWidth) - targetHostLeft;
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
            host.Top = targetTop;
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactViewportWidth);
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
            host.Top = targetTop;
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactViewportWidth);
            _deepCapsuleSlotTop = targetTop;
            return;
        }

        var currentTop = double.IsNaN(host.Top) || double.IsInfinity(host.Top) ? targetTop : RoundToDevicePixelY(host.Top);
        var currentHostLeft = double.IsNaN(host.Left) || double.IsInfinity(host.Left) ? targetHostLeft : RoundToDevicePixelX(host.Left);
        var currentViewportWidth = double.IsNaN(host.Width) || double.IsInfinity(host.Width) || host.Width <= 0
            ? wallExactViewportWidth
            : RoundToDevicePixelX(host.Width);
        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        host.BeginAnimation(Window.LeftProperty, null);
        var targetRight = targetHostLeft + wallExactViewportWidth;
        var currentRight = currentHostLeft + currentViewportWidth;
        var needsHorizontalAnimation =
            Math.Abs(currentHostLeft - targetHostLeft) >= 0.5 ||
            Math.Abs(currentRight - targetRight) >= 0.5 ||
            Math.Abs(currentViewportWidth - wallExactViewportWidth) >= 0.5;
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
                SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactViewportWidth);
            };
            BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, horizontalAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactViewportWidth);
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

        animate = animate && _controller.State.EnableAnimations;
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
        _deepCapsuleCrossQueueDragVisualActive = false;
        _deepCapsuleCrossQueueDragUnlocked = false;
        _deepCapsuleSlotHost.Content = null;
        _deepCapsuleSlotHost.Close();
        _deepCapsuleSlotHost = null;
        _deepCapsuleSlotHostRoot = null;
        _deepCapsuleSlotDragScale = null;
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
            SetDeepCapsuleSlotContextMenuOpen(true);
            StartDeepCapsuleContextMenuGuards();
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu = null;
                SetDeepCapsuleSlotContextMenuOpen(false);
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
        SetDeepCapsuleSlotContextMenuOpen(false);
        StopDeepCapsuleContextMenuGuards();
    }

    private void SetDeepCapsuleSlotContextMenuOpen(bool open)
    {
        if (_deepCapsuleSlotContextMenuOpen == open)
        {
            RefreshDeepCapsuleSlotTopmost();
            return;
        }

        _deepCapsuleSlotContextMenuOpen = open;
        if (open)
        {
            _controller.BeginDeepCapsuleContextMenu();
        }
        else
        {
            _controller.EndDeepCapsuleContextMenu();
        }

        RefreshDeepCapsuleSlotTopmost();
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

    private double MyTopForIndex(int index, int slotCount)
    {
        return DeepCapsuleLayout.TopForIndex(
            index,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            DeepCapsuleWorkArea(),
            slotCount);
    }

    private void ApplyDeepCapsuleSlotHostViewport(double viewportWidth, bool updateFixedLayout = true)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleSlotHost.Width = DeepCapsuleSlotViewportWidth(viewportWidth);
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        if (updateFixedLayout)
        {
            ApplyDeepCapsuleSlotFixedLayout();
        }
    }

    private void SetDeepCapsuleSlotHostHorizontalBounds(double left, double viewportWidth, bool updateFixedLayout = true)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (MyDeepCapsuleIsLeftEdge)
        {
            _deepCapsuleSlotHost.Left = left;
            ApplyDeepCapsuleSlotHostViewport(viewportWidth, updateFixedLayout);
        }
        else
        {
            // Top-level Window Left/Width changes can be presented as separate native moves.
            // On the right edge, keep the wall side covered between those native updates:
            // grow first when sliding out, but move first when shrinking back.
            var targetViewportWidth = DeepCapsuleSlotViewportWidth(viewportWidth);
            var currentViewportWidth = double.IsNaN(_deepCapsuleSlotHost.Width) ||
                double.IsInfinity(_deepCapsuleSlotHost.Width) ||
                _deepCapsuleSlotHost.Width <= 0
                    ? targetViewportWidth
                    : _deepCapsuleSlotHost.Width;
            var shrinking = targetViewportWidth < currentViewportWidth - 0.01;
            if (shrinking)
            {
                _deepCapsuleSlotHost.Left = left;
                ApplyDeepCapsuleSlotHostViewport(viewportWidth, updateFixedLayout);
            }
            else
            {
                ApplyDeepCapsuleSlotHostViewport(viewportWidth, updateFixedLayout);
                _deepCapsuleSlotHost.Left = left;
            }
        }

        _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
    }

    private void SetDeepCapsuleCrossQueueDragVisual(bool active, bool animate)
    {
        if (_deepCapsuleCrossQueueDragVisualActive == active)
        {
            return;
        }

        _deepCapsuleCrossQueueDragVisualActive = active;
        if (active)
        {
            _deepCapsuleCrossQueueDragWidth = DeepCapsuleCrossQueueDragWidth();
        }
        else
        {
            _appliedSlotEdge = null;
            _deepCapsuleCrossQueueDragWidth = PaperLayoutDefaults.CapsuleHeight;
        }

        ApplyDeepCapsuleSlotFixedLayout();
        AnimateDeepCapsuleCrossQueueDragVisual(active, animate && _controller.State.EnableAnimations);
    }

    private double DeepCapsuleCrossQueueDragWidth()
    {
        var shellWidthWithoutClose = Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth() +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth() +
            CapsuleRightPadding);
        return Math.Max(PaperLayoutDefaults.CapsuleWidth, shellWidthWithoutClose + WindowChromeInset);
    }

    private void ApplyDeepCapsuleCrossQueueDragHostBounds(double left, double top)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleSlotHost.Left = left;
        _deepCapsuleSlotHost.Top = top;
        _deepCapsuleSlotHost.Width = _deepCapsuleCrossQueueDragWidth;
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        _deepCapsuleSlotLeft = left;
        _deepCapsuleSlotTop = top;
    }

    private void AnimateDeepCapsuleCrossQueueDragVisual(bool active, bool animate)
    {
        AnimateElementOpacity(_deepCapsuleSlotLabelText, 1.0, animate);
        AnimateElementOpacity(_deepCapsuleSlotCloseArea, active ? 0.0 : 1.0, animate);

        if (_deepCapsuleSlotDragScale == null)
        {
            return;
        }

        _deepCapsuleSlotDragScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _deepCapsuleSlotDragScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate || !active)
        {
            _deepCapsuleSlotDragScale.ScaleX = 1.0;
            _deepCapsuleSlotDragScale.ScaleY = 1.0;
            return;
        }

        _deepCapsuleSlotDragScale.ScaleX = DeepCapsuleCrossQueueDragScaleFrom;
        _deepCapsuleSlotDragScale.ScaleY = DeepCapsuleCrossQueueDragScaleFrom;
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = DeepCapsuleCrossQueueDragScaleFrom,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleCrossQueueDragMorphMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        _deepCapsuleSlotDragScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        _deepCapsuleSlotDragScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateElementOpacity(UIElement? element, double to, bool animate)
    {
        if (element == null)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animate)
        {
            element.Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleCrossQueueDragMorphMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyDeepCapsuleSlotFixedLayout()
    {
        if (_deepCapsuleCrossQueueDragVisualActive)
        {
            ApplyDeepCapsuleCrossQueueDragFixedLayout();
            return;
        }

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
            _deepCapsuleSlotChrome.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotChrome.Width = Math.Max(0, fullWidth - WindowChromeMargin);
            _deepCapsuleSlotChrome.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset);
            _deepCapsuleSlotChrome.CornerRadius = new CornerRadius(CapsuleChromeCornerRadius);
        }
        if (_deepCapsuleSlotShell != null)
        {
            _deepCapsuleSlotShell.Margin = leftEdge
                ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
                : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotShell.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _deepCapsuleSlotShell.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
            _deepCapsuleSlotShell.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset);
        }
        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.Margin = leftEdge
                ? new Thickness(0, outlineMargin, outlineMargin, outlineMargin)
                : new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
            _deepCapsuleSlotOutline.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _deepCapsuleSlotOutline.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotOutline.Width = Math.Max(0, fullWidth - outlineMargin);
            _deepCapsuleSlotOutline.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - outlineMargin * 2);
            _deepCapsuleSlotOutline.CornerRadius = new CornerRadius(CapsuleChromeCornerRadius + DeepCapsuleSlotOutlineThickness - DeepCapsuleSlotOutlineOverlap);
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
            _deepCapsuleSlotShell == null ||
            _deepCapsuleSlotLeftArea == null ||
            _deepCapsuleSlotCloseArea == null ||
            _deepCapsuleSlotLeftStack == null)
        {
            return;
        }

        _appliedSlotEdge = edge;
        var leftEdge = edge == DeepCapsuleEdge.Left;

        // Content (icon + title) on the interior side; close on the wall side.
        _deepCapsuleSlotLeftArea.Cursor = Cursors.Hand;
        _deepCapsuleSlotCloseArea.IsHitTestVisible = true;

        if (_deepCapsuleSlotShell.ColumnDefinitions.Count >= 2)
        {
            _deepCapsuleSlotShell.ColumnDefinitions[0].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            _deepCapsuleSlotShell.ColumnDefinitions[1].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
        }

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

        if (_deepCapsuleSlotLeftStack.ColumnDefinitions.Count >= 2)
        {
            _deepCapsuleSlotLeftStack.ColumnDefinitions[0].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            _deepCapsuleSlotLeftStack.ColumnDefinitions[1].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
        }

        if (_deepCapsuleSlotIconText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotIconText, leftEdge ? 1 : 0);
            _deepCapsuleSlotIconText.HorizontalAlignment = leftEdge
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            _deepCapsuleSlotIconText.TextAlignment = leftEdge
                ? TextAlignment.Right
                : TextAlignment.Left;
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotLabelText, leftEdge ? 0 : 1);
            _deepCapsuleSlotLabelText.Margin = leftEdge
                ? new Thickness(0, 0, CapsuleIconGap, 0)
                : new Thickness(CapsuleIconGap, 0, 0, 0);
            _deepCapsuleSlotLabelText.TextAlignment = leftEdge
                ? TextAlignment.Right
                : TextAlignment.Left;
        }

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
        SetDeepCapsuleSlotHostHorizontalBounds(roundedLeft, roundedRight - roundedLeft, updateFixedLayout: false);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private double DeepCapsuleSlotShellLayoutWidth()
    {
        return Math.Max(1, CapsuleShellWidth(usesDeepCapsulePresentation: true));
    }

    private double DeepCapsuleContentWindowWidth()
    {
        return Math.Min(
            CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            Math.Max(1, CapsuleShellWidth(usesDeepCapsulePresentation: true) + WindowChromeInset));
    }

    private double DeepCapsuleSlotViewportWidth(double viewportWidth)
    {
        return Math.Clamp(viewportWidth, 1, CapsuleWindowWidth(usesDeepCapsulePresentation: true));
    }

    private double DeepCapsuleTopForIndex(int index)
    {
        return MyTopForIndex(index, _deepCapsuleSlotCount);
    }

    private void MoveDeepCapsuleToCurrentTarget(
        bool animate = false,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false,
        bool forceRestingOffset = false,
        double? targetTopOverride = null,
        bool allowCollapseAllRetracted = false)
    {
        if (!HasDeepCapsuleSlotPlacement || (_isCollapseAllRetracted && !allowCollapseAllRetracted))
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
        var targetTop = RoundToDevicePixelY(targetTopOverride ?? DeepCapsuleTopForIndex(_deepCapsuleIndex + _deepCapsuleVisualOffset));

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
            DeepCapsuleContentWindowWidth(),
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
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: false);
        RefreshEffectiveTopmost();

        var targetTop = RoundToDevicePixelY(anchorTop);

        MoveDeepCapsuleToCurrentTarget(
            animate,
            DeepCapsuleLayout.SlotRetractMoveMilliseconds,
            keepHiding: true,
            forceRestingOffset: true,
            targetTopOverride: targetTop,
            allowCollapseAllRetracted: true);
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
        MoveDeepCapsuleToCurrentTarget(animate: _controller.State.EnableAnimations);
    }

    public void ApplyDeepCapsulePlacement(int index, bool animate = false, int visualOffset = 0, int slotCount = 1)
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
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        _deepCapsuleSlotCount = Math.Max(1, slotCount);
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

    public void ApplyExpandedDeepCapsuleSlotPlacement(int index, bool animate = false, int visualOffset = 0, int slotCount = 1)
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
        SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
        SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        _deepCapsuleSlotCount = Math.Max(1, slotCount);
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
        if (!_isApplyingCollapsedState && shouldSaveExpandedGeometry)
        {
            _controller.UpdateGeometry(_paper, this);
        }
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
        // When retracting from Active to Resting, keep the active visuals until the move finishes;
        // dropping them before moving reads as a hard cut.
        if (!_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (wasActive && !_isApplyingCollapsedState && !keepActiveUntilRetracted)
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
            if (keepActiveUntilRetracted)
            {
                ClearDeepCapsuleSlotActiveAfterMove(DeepCapsuleLayout.SlotRetractMoveMilliseconds);
            }
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
            _deepCapsuleSlotCount = 1;
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
        animate = animate && _controller.State.EnableAnimations;
        _deepCapsuleCrossQueueDragUnlocked = false;
        SetDeepCapsuleCrossQueueDragVisual(false, animate: false);

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
            _deepCapsuleSlotCount = 1;
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
            SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.Normal);
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

    private Point DeepCapsuleScreenPointToDip(Point screenPos)
    {
        return WindowWorkAreaHelper.DeviceScreenPointToDip(screenPos);
    }

    private void StartDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() || _deepCapsuleSlotHost == null)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Reordering);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Hovered);
        var currentDip = DeepCapsuleScreenPointToDip(currentScreenPos);
        _deepCapsuleDragStartDip = DeepCapsuleScreenPointToDip(_deepCapsuleSlotMouseDownScreenPos);
        _deepCapsuleDragLastDip = currentDip;
        _deepCapsuleDragLastScreenPos = currentScreenPos;
        _deepCapsuleDragStartMonitorDeviceName = WindowWorkAreaHelper
            .MonitorAtDeviceScreenPoint(_deepCapsuleSlotMouseDownScreenPos)?.DeviceName ?? "";
        _deepCapsuleCrossQueueDragUnlocked = false;
        _deepCapsuleDragMouseOffsetY = currentDip.Y - _deepCapsuleSlotHost.Top;
        _deepCapsuleDragMouseOffsetX = currentDip.X - _deepCapsuleSlotHost.Left;

        _deepCapsuleSlotHost.BeginAnimation(Window.LeftProperty, null);
        _deepCapsuleSlotHost.BeginAnimation(Window.TopProperty, null);
        ClearDeepCapsuleSlotHorizontalAnimation();
        SetDeepCapsuleCrossQueueDragVisual(false, animate: false);

        var area = DeepCapsuleWorkArea();
        var visibleWidth = ExpandedDeepCapsuleVisibleWidth();
        _deepCapsuleDragLeft = RoundToDevicePixelX(MyDockedLeft(area, visibleWidth));

        SetDeepCapsuleSlotHostHorizontalBounds(_deepCapsuleDragLeft, visibleWidth);

        Mouse.OverrideCursor = Cursors.SizeAll;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        var cursorDip = DeepCapsuleScreenPointToDip(currentScreenPos);
        _deepCapsuleDragLastDip = cursorDip;
        _deepCapsuleDragLastScreenPos = currentScreenPos;

        if (_deepCapsuleSlotHost != null)
        {
            if (!_deepCapsuleCrossQueueDragUnlocked &&
                ShouldUnlockDeepCapsuleCrossQueueDrag(cursorDip, currentScreenPos))
            {
                _deepCapsuleCrossQueueDragUnlocked = true;
                _deepCapsuleCrossQueueDragWidth = DeepCapsuleCrossQueueDragWidth();
                _deepCapsuleDragMouseOffsetX = _deepCapsuleCrossQueueDragWidth / 2.0;
                _deepCapsuleDragMouseOffsetY = PaperLayoutDefaults.CapsuleHeight / 2.0;
                ApplyDeepCapsuleCrossQueueDragHostBounds(
                    RoundToDevicePixelX(cursorDip.X - _deepCapsuleDragMouseOffsetX),
                    RoundToDevicePixelY(cursorDip.Y - _deepCapsuleDragMouseOffsetY));
                SetDeepCapsuleCrossQueueDragVisual(true, animate: true);
                Mouse.OverrideCursor = Cursors.SizeAll;
            }

            var targetTop = RoundToDevicePixelY(cursorDip.Y - _deepCapsuleDragMouseOffsetY);
            if (_deepCapsuleCrossQueueDragUnlocked)
            {
                ApplyDeepCapsuleCrossQueueDragHostBounds(
                    RoundToDevicePixelX(cursorDip.X - _deepCapsuleDragMouseOffsetX),
                    targetTop);
            }
            else
            {
                _deepCapsuleSlotHost.Left = _deepCapsuleDragLeft;
                _deepCapsuleSlotHost.Top = targetTop;
                _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
                _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
            }
        }
    }

    private void ApplyDeepCapsuleCrossQueueDragFixedLayout()
    {
        var bodyWidth = Math.Max(1, _deepCapsuleCrossQueueDragWidth - WindowChromeInset);
        var bodyHeight = Math.Max(1, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset);
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var outlineWidth = Math.Max(1, _deepCapsuleCrossQueueDragWidth - outlineMargin * 2);
        var outlineHeight = Math.Max(1, PaperLayoutDefaults.CapsuleHeight - outlineMargin * 2);

        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Margin = new Thickness(WindowChromeMargin);
            _deepCapsuleSlotChrome.HorizontalAlignment = HorizontalAlignment.Center;
            _deepCapsuleSlotChrome.VerticalAlignment = VerticalAlignment.Center;
            _deepCapsuleSlotChrome.Width = bodyWidth;
            _deepCapsuleSlotChrome.Height = bodyHeight;
            _deepCapsuleSlotChrome.CornerRadius = new CornerRadius(bodyHeight / 2.0);
        }

        if (_deepCapsuleSlotShell != null)
        {
            _deepCapsuleSlotShell.Margin = new Thickness(WindowChromeMargin);
            _deepCapsuleSlotShell.HorizontalAlignment = HorizontalAlignment.Center;
            _deepCapsuleSlotShell.VerticalAlignment = VerticalAlignment.Center;
            _deepCapsuleSlotShell.Width = bodyWidth;
            _deepCapsuleSlotShell.Height = bodyHeight;
            if (_deepCapsuleSlotShell.ColumnDefinitions.Count >= 2)
            {
                _deepCapsuleSlotShell.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                _deepCapsuleSlotShell.ColumnDefinitions[1].Width = new GridLength(0);
            }
        }

        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.Margin = new Thickness(outlineMargin);
            _deepCapsuleSlotOutline.HorizontalAlignment = HorizontalAlignment.Center;
            _deepCapsuleSlotOutline.VerticalAlignment = VerticalAlignment.Center;
            _deepCapsuleSlotOutline.Width = outlineWidth;
            _deepCapsuleSlotOutline.Height = outlineHeight;
            _deepCapsuleSlotOutline.CornerRadius = new CornerRadius(outlineHeight / 2.0);
        }

        if (_deepCapsuleSlotLeftArea != null)
        {
            Grid.SetColumn(_deepCapsuleSlotLeftArea, 0);
            _deepCapsuleSlotLeftArea.CornerRadius = new CornerRadius(bodyHeight / 2.0);
            _deepCapsuleSlotLeftArea.Cursor = Cursors.SizeAll;
        }

        if (_deepCapsuleSlotCloseArea != null)
        {
            Grid.SetColumn(_deepCapsuleSlotCloseArea, 1);
            _deepCapsuleSlotCloseArea.Width = 0;
            _deepCapsuleSlotCloseArea.Margin = new Thickness(0);
            _deepCapsuleSlotCloseArea.IsHitTestVisible = false;
        }

        if (_deepCapsuleSlotLeftStack != null)
        {
            _deepCapsuleSlotLeftStack.Margin = new Thickness(CapsuleLeftPadding, 0, CapsuleRightPadding, 0);
            if (_deepCapsuleSlotLeftStack.ColumnDefinitions.Count >= 2)
            {
                _deepCapsuleSlotLeftStack.ColumnDefinitions[0].Width = GridLength.Auto;
                _deepCapsuleSlotLeftStack.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }
        }

        if (_deepCapsuleSlotIconText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotIconText, 0);
            _deepCapsuleSlotIconText.HorizontalAlignment = HorizontalAlignment.Left;
            _deepCapsuleSlotIconText.TextAlignment = TextAlignment.Left;
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotLabelText, 1);
            _deepCapsuleSlotLabelText.Margin = new Thickness(CapsuleIconGap, 0, 0, 0);
            _deepCapsuleSlotLabelText.HorizontalAlignment = HorizontalAlignment.Stretch;
            _deepCapsuleSlotLabelText.TextAlignment = TextAlignment.Left;
        }
    }

    private bool ShouldUnlockDeepCapsuleCrossQueueDrag(Point cursorDip, Point currentScreenPos)
    {
        var horizontalDelta = Math.Abs(cursorDip.X - _deepCapsuleDragStartDip.X);
        if (horizontalDelta >= DeepCapsuleCrossQueueDragUnlockDistance)
        {
            return true;
        }

        return HasDeepCapsuleDragEnteredAnotherMonitor(currentScreenPos);
    }

    private bool HasDeepCapsuleDragEnteredAnotherMonitor(Point currentScreenPos)
    {
        if (string.IsNullOrEmpty(_deepCapsuleDragStartMonitorDeviceName))
        {
            return false;
        }

        var currentMonitor = WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(currentScreenPos);
        return currentMonitor.HasValue &&
            !string.IsNullOrEmpty(currentMonitor.Value.DeviceName) &&
            !string.Equals(
                currentMonitor.Value.DeviceName,
                _deepCapsuleDragStartMonitorDeviceName,
                StringComparison.Ordinal);
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        var crossQueueDragUnlocked = _deepCapsuleCrossQueueDragUnlocked;
        _deepCapsuleCrossQueueDragUnlocked = false;
        _deepCapsuleDragStartMonitorDeviceName = "";
        SetDeepCapsuleCrossQueueDragVisual(false, animate: crossQueueDragUnlocked);

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
        Mouse.OverrideCursor = null;
        SetDeepCapsuleVisualState(
            _deepCapsuleSlotShell?.IsMouseOver == true
                ? DeepCapsuleVisualState.Hovered
                : DeepCapsuleVisualState.Resting);

        if (commit)
        {
            if (!crossQueueDragUnlocked)
            {
                _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                return;
            }

            // Resolve the (monitor, edge) queue under the drop point. If it differs from this
            // paper's current queue, reassign it (cross-edge / cross-monitor move). Otherwise it's
            // a plain vertical reorder within the same queue.
            var resolved = WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(_deepCapsuleDragLastScreenPos);
            var dropDip = WindowWorkAreaHelper.DeviceScreenPointToDip(_deepCapsuleDragLastScreenPos);
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
            var targetSide = dropDip.X < targetArea.Left + targetArea.Width / 2
                ? DeepCapsuleSides.Left
                : DeepCapsuleSides.Right;

            var queueChanged = targetSide != _paper.CapsuleSide ||
                !string.Equals(targetMonitor, _paper.CapsuleMonitorDeviceName, StringComparison.Ordinal);

            if (queueChanged)
            {
                _controller.MoveCapsuleToQueue(_paper, targetMonitor, targetSide, dropDip.Y);
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
