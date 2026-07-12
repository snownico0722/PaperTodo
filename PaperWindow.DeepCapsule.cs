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

        _appliedSlotEdge = null;
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
            Width = DeepCapsuleVisibleWidth(),
            Height = PaperLayoutDefaults.CapsuleHeight,
            Content = _deepCapsuleSlotHostRoot
        };
        host.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(host);
            if (PresentationSource.FromVisual(host) is HwndSource source)
            {
                source.AddHook(OnDeepCapsuleSlotHostMessage);
            }
        };
        host.Deactivated += (_, _) => CloseDeepCapsuleSlotContextMenu();
        _deepCapsuleSlotHost = host;
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private IntPtr OnDeepCapsuleSlotHostMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // The slot host is not a paper window. In particular it must not inherit the paper's
        // snap/maximize WM_GETMINMAXINFO handling or minimum tracking size. It only participates
        // in display-metric refresh and then lets WPF process the native message normally.
        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            _deepCapsuleSlotLayoutSettlePending = true;
            ScheduleDeepCapsuleSlotHostLayoutSettle();
            if (IsDeepCapsuleReordering)
            {
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
            }
            else
            {
                _controller.ScheduleDisplayMetricsRefresh();
            }
        }

        return IntPtr.Zero;
    }

    private Grid BuildDeepCapsuleSlotShell()
    {
        var shell = new Grid
        {
            Width = double.NaN,
            Height = 30,
            Margin = new Thickness(WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Stretch,
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

        // Real-size close segment: the glyph is always fully on-screen, so it centers with no
        // offset (the old constant compensated for the half-hidden overflow design).
        var closeGlyphOffset = new TranslateTransform(0, 0);
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
            Width = 0,
            Opacity = 0,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipHideThisPaper"),
            IsHitTestVisible = false,
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
            _deepCapsuleSlotCloseArea.Opacity = Math.Clamp(
                _deepCapsuleSlotCloseArea.Width / CapsuleCloseWidth,
                0,
                1);
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
            SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
        }

        SetCollapsedState(true, alignExpandedToDockedEdge: true);
        return true;
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
        double targetTop,
        double targetCloseWidth,
        bool animate,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false)
    {
        // Docking is incompatible with the free-drag pill visual: the centered fixed-width drag
        // layout inside a docked-width host renders as a detached pill clipped at both ends, and
        // its column order leaves the close segment on the wall side. Every dock request must
        // therefore drop the drag visual first. The ghost drop path already deactivates it before
        // calling in; this is the safety net for any other placement path.
        if (_deepCapsuleCrossQueueDragVisualActive && !IsDeepCapsuleReordering)
        {
            SetDeepCapsuleCrossQueueDragVisual(false, animate: false);
        }

        animate = animate && _controller.State.EnableAnimations;
        var host = EnsureDeepCapsuleSlotHost();
        var requestedBounds = UpdateDeepCapsuleSlotRequestedGeometry(targetTop, targetCloseWidth);
        var targetHostLeft = requestedBounds.Left;
        var targetHostTop = requestedBounds.Top;
        var wallExactWidth = requestedBounds.Width;
        // The rounded edge pair, rather than independently rounded Left and Width, keeps the
        // derived right wall exact on fractional-DPI displays throughout every settle path.
        targetCloseWidth = _deepCapsuleSlotRequestedCloseWidth;
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
        _deepCapsuleSlotTop = targetHostTop;
        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHostRoot.Opacity = 1;
            _deepCapsuleSlotHostRoot.IsHitTestVisible = !keepHiding;
        }

        if (!host.IsVisible)
        {
            _deepCapsuleSlotMoveGeneration++;
            host.BeginAnimation(Window.OpacityProperty, null);
            host.Top = targetHostTop;
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactWidth);
            host.Opacity = _isCollapseAllRetracted ? 0 : 1;
            host.Show();
            _deepCapsuleSlotLayoutSettlePending = true;
            ScheduleDeepCapsuleSlotHostLayoutSettle();
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
            host.Top = targetHostTop;
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactWidth);
            _deepCapsuleSlotTop = targetHostTop;
            return;
        }

        var currentTop = double.IsNaN(host.Top) || double.IsInfinity(host.Top)
            ? targetHostTop
            : RoundDeepCapsuleSlotY(host.Top);
        var currentHostLeft = double.IsNaN(host.Left) || double.IsInfinity(host.Left)
            ? targetHostLeft
            : RoundDeepCapsuleSlotX(host.Left);
        var currentWidth = double.IsNaN(host.Width) || double.IsInfinity(host.Width) || host.Width <= 0
            ? wallExactWidth
            : RoundDeepCapsuleSlotX(host.Width);
        var currentCloseWidth = _deepCapsuleSlotCloseArea?.Width ?? 0;
        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        host.BeginAnimation(Window.LeftProperty, null);
        var targetRight = targetHostLeft + wallExactWidth;
        var currentRight = currentHostLeft + currentWidth;
        var needsHorizontalAnimation =
            Math.Abs(currentHostLeft - targetHostLeft) >= 0.5 ||
            Math.Abs(currentRight - targetRight) >= 0.5 ||
            Math.Abs(currentWidth - wallExactWidth) >= 0.5 ||
            Math.Abs(currentCloseWidth - targetCloseWidth) >= 0.5;
        if (needsHorizontalAnimation)
        {
            _deepCapsuleSlotStartWidth = currentWidth;
            _deepCapsuleSlotTargetWidth = wallExactWidth;
            _deepCapsuleSlotStartCloseWidth = currentCloseWidth;
            _deepCapsuleSlotTargetCloseWidth = targetCloseWidth;
            // Use the wall-exact target width so the animator's per-frame right edge lands on
            // Round(rightEdge) = the wall every frame. With the raw width the seam reappeared
            // DURING the hover reveal animation, then snapped flush only when Completed ran.
            ApplyDeepCapsuleSlotHostWidth(currentWidth);
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
                ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
                SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactWidth);
            };
            BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, horizontalAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            SetDeepCapsuleSlotHostHorizontalBounds(targetHostLeft, wallExactWidth);
        }

        if (Math.Abs(currentTop - targetHostTop) >= 0.5)
        {
            AnimateDeepCapsuleSlotHostTop(host, currentTop, targetHostTop, durationMs, generation);
        }
        else
        {
            host.BeginAnimation(Window.TopProperty, null);
            host.Top = targetHostTop;
            _deepCapsuleSlotTop = targetHostTop;
        }
    }

    private void ScheduleDeepCapsuleSlotHostLayoutSettle()
    {
        var host = _deepCapsuleSlotHost;
        var root = _deepCapsuleSlotHostRoot;
        if (host == null || root == null || _deepCapsuleSlotLayoutSettleScheduled)
        {
            return;
        }

        _deepCapsuleSlotLayoutSettleScheduled = true;
        host.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(host, _deepCapsuleSlotHost) ||
                    !ReferenceEquals(root, _deepCapsuleSlotHostRoot))
                {
                    // A closed host may already have been recreated and scheduled separately.
                    // This stale callback must not clear the new host's pending/scheduled state.
                    return;
                }

                _deepCapsuleSlotLayoutSettleScheduled = false;
                if (!host.IsVisible || !HasDeepCapsuleSlotPlacement)
                {
                    _deepCapsuleSlotLayoutSettlePending = false;
                    return;
                }

                // Native cross-monitor drag owns the HWND until mouse-up. Preserve the request and
                // let EndDeepCapsuleReorderDrag retry after the final queue/edge has been resolved.
                if (IsDeepCapsuleReordering)
                {
                    _deepCapsuleSlotLayoutSettlePending = true;
                    return;
                }

                if (!_deepCapsuleSlotLayoutSettlePending || _deepCapsuleSlotRequestedBounds.IsEmpty)
                {
                    return;
                }

                _deepCapsuleSlotLayoutSettlePending = false;
                // Show()/WM_DPICHANGED has now attached the slot to its target presentation
                // source. Re-measure and re-round with that HWND's DPI instead of replaying the
                // pre-show rectangle that may have been calculated from the hidden paper window.
                var bounds = UpdateDeepCapsuleSlotRequestedGeometry(
                    _deepCapsuleSlotRequestedTop,
                    _deepCapsuleSlotRequestedCloseWidth);
                ClearDeepCapsuleSlotHorizontalAnimation();
                host.BeginAnimation(Window.LeftProperty, null);
                host.BeginAnimation(Window.TopProperty, null);
                _appliedSlotEdge = null;
                ApplyDeepCapsuleSlotCloseWidth(_deepCapsuleSlotRequestedCloseWidth);
                SetDeepCapsuleSlotHostHorizontalBounds(bounds.Left, bounds.Width);
                host.Top = bounds.Top;
                _deepCapsuleSlotTop = bounds.Top;

                host.InvalidateMeasure();
                host.InvalidateArrange();
                root.InvalidateMeasure();
                root.InvalidateArrange();
                host.UpdateLayout();
                root.InvalidateVisual();
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AnimateDeepCapsuleSlotHostTop(Window host, double from, double to, int durationMs, int generation)
    {
        host.BeginAnimation(Window.TopProperty, null);
        host.Top = from;
        _deepCapsuleSlotTop = from;

        var startedAt = DateTimeOffset.UtcNow;
        var duration = Math.Max(1, durationMs);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            if (generation != _deepCapsuleSlotMoveGeneration || !ReferenceEquals(host, _deepCapsuleSlotHost))
            {
                timer.Stop();
                return;
            }

            var progress = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds / duration;
            if (progress >= 1.0)
            {
                host.Top = to;
                _deepCapsuleSlotTop = to;
                timer.Stop();
                return;
            }

            progress = Math.Clamp(progress, 0.0, 1.0);
            var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            var top = RoundDeepCapsuleSlotY(Lerp(from, to, eased));
            host.Top = top;
            _deepCapsuleSlotTop = top;
        };
        timer.Start();
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
        CancelDeepCapsuleReorderDrag();
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
        _deepCapsuleSlotLayoutSettlePending = false;
        _deepCapsuleSlotLayoutSettleScheduled = false;
        _deepCapsuleSlotRequestedBounds = Rect.Empty;
        _deepCapsuleSlotRequestedTop = 0;
        _deepCapsuleSlotRequestedCloseWidth = 0;
        _deepCapsuleSlotMeasureRefreshScheduled = false;
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
        _appliedSlotEdge = null;
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
            SetDeepCapsuleHover(true);
            StartDeepCapsuleContextMenuGuards();
            PromoteDeepCapsuleContextMenu(menu);
            _ = menu.Dispatcher.BeginInvoke(
                () => PromoteDeepCapsuleContextMenu(menu),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu = null;
                SetDeepCapsuleSlotContextMenuOpen(false);
                StopDeepCapsuleContextMenuGuards();
                SetDeepCapsuleHover(_deepCapsuleSlotShell?.IsMouseOver == true);
            }
        };

        return menu;
    }

    private static void PromoteDeepCapsuleContextMenu(ContextMenu menu)
    {
        if (menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            WindowNative.ApplyTopmostZOrder(source.Handle, topmost: true, insertAfter: IntPtr.Zero);
        }
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

    private double MyDockedLeft(Rect area, double width)
    {
        return DeepCapsuleLayout.DockedLeft(area, width, MyDeepCapsuleEdge);
    }

    private DpiScale DeepCapsuleSlotDpi()
    {
        if (_deepCapsuleSlotHostRoot != null &&
            PresentationSource.FromVisual(_deepCapsuleSlotHostRoot)?.CompositionTarget != null)
        {
            return VisualTreeHelper.GetDpi(_deepCapsuleSlotHostRoot);
        }

        if (_deepCapsuleSlotHost != null &&
            PresentationSource.FromVisual(_deepCapsuleSlotHost)?.CompositionTarget != null)
        {
            return VisualTreeHelper.GetDpi(_deepCapsuleSlotHost);
        }

        return VisualTreeHelper.GetDpi(this);
    }

    private double RoundDeepCapsuleSlotX(double value)
    {
        return RoundToDevicePixel(value, DeepCapsuleSlotDpi().DpiScaleX);
    }

    private double RoundDeepCapsuleSlotY(double value)
    {
        return RoundToDevicePixel(value, DeepCapsuleSlotDpi().DpiScaleY);
    }

    private (double Left, double Width) RoundDeepCapsuleHorizontalBounds(double left, double width)
    {
        // Round both screen edges, then derive the width. Rounding Left and Width separately can
        // move the derived right edge by one device pixel on fractional-DPI displays.
        var roundedLeft = RoundDeepCapsuleSlotX(left);
        var roundedRight = RoundDeepCapsuleSlotX(left + width);
        return (roundedLeft, Math.Max(1, roundedRight - roundedLeft));
    }

    private Rect UpdateDeepCapsuleSlotRequestedGeometry(double targetTop, double targetCloseWidth)
    {
        targetCloseWidth = Math.Clamp(targetCloseWidth, 0, CapsuleCloseWidth);
        var targetWidth = DeepCapsuleVisibleWidth() + targetCloseWidth;
        var area = DeepCapsuleWorkArea();
        var (targetLeft, targetHostWidth) = RoundDeepCapsuleHorizontalBounds(
            MyDockedLeft(area, targetWidth),
            targetWidth);

        _deepCapsuleSlotRequestedTop = targetTop;
        _deepCapsuleSlotRequestedCloseWidth = targetCloseWidth;
        _deepCapsuleSlotRequestedBounds = new Rect(
            targetLeft,
            RoundDeepCapsuleSlotY(targetTop),
            targetHostWidth,
            PaperLayoutDefaults.CapsuleHeight);
        return _deepCapsuleSlotRequestedBounds;
    }

    private void ScheduleDeepCapsuleSlotMeasureRefresh()
    {
        var host = _deepCapsuleSlotHost;
        if (host?.IsVisible != true ||
            !HasDeepCapsuleSlotPlacement ||
            _deepCapsuleSlotMeasureRefreshScheduled)
        {
            return;
        }

        _deepCapsuleSlotMeasureRefreshScheduled = true;
        host.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(host, _deepCapsuleSlotHost))
                {
                    return;
                }

                _deepCapsuleSlotMeasureRefreshScheduled = false;
                if (!host.IsVisible ||
                    !HasDeepCapsuleSlotPlacement ||
                    _deepCapsuleCrossQueueDragVisualActive ||
                    IsDeepCapsuleReordering)
                {
                    return;
                }

                MoveDeepCapsuleToCurrentTarget(animate: _controller.State.EnableAnimations);
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private double MyTopForIndex(int index, int slotCount)
    {
        return DeepCapsuleLayout.TopForIndex(
            index,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            DeepCapsuleWorkArea(),
            slotCount);
    }

    private void ApplyDeepCapsuleSlotHostWidth(double width, bool updateFixedLayout = true)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleSlotHost.Width = Math.Max(1, width);
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        if (updateFixedLayout)
        {
            ApplyDeepCapsuleSlotFixedLayout();
        }
    }

    private void SetDeepCapsuleSlotHostHorizontalBounds(double left, double width, bool updateFixedLayout = true)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (MyDeepCapsuleIsLeftEdge)
        {
            _deepCapsuleSlotHost.Left = left;
            ApplyDeepCapsuleSlotHostWidth(width, updateFixedLayout);
        }
        else
        {
            // Top-level Window Left/Width changes can be presented as separate native moves.
            // On the right edge, keep the wall side covered between those native updates:
            // grow first when sliding out, but move first when shrinking back.
            var targetWidth = Math.Max(1, width);
            var currentWidth = double.IsNaN(_deepCapsuleSlotHost.Width) ||
                double.IsInfinity(_deepCapsuleSlotHost.Width) ||
                _deepCapsuleSlotHost.Width <= 0
                    ? targetWidth
                    : _deepCapsuleSlotHost.Width;
            var shrinking = targetWidth < currentWidth - 0.01;
            if (shrinking)
            {
                _deepCapsuleSlotHost.Left = left;
                ApplyDeepCapsuleSlotHostWidth(width, updateFixedLayout);
            }
            else
            {
                ApplyDeepCapsuleSlotHostWidth(width, updateFixedLayout);
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

        ApplyDeepCapsuleSlotCloseWidth(DeepCapsuleTargetCloseWidth());
        ApplyDeepCapsuleSlotFixedLayout();
        AnimateDeepCapsuleCrossQueueDragVisual(active, animate && _controller.State.EnableAnimations);
    }

    private double DeepCapsuleCrossQueueDragWidth()
    {
        // A free drag pill restores the second shadow margin; its actual content width is the
        // same as the expanded edge tag rather than a separate hidden/full-width measurement.
        return Math.Max(
            PaperLayoutDefaults.CapsuleWidth,
            ExpandedDeepCapsuleVisibleWidth() + WindowChromeMargin);
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
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var leftEdge = MyDeepCapsuleIsLeftEdge;
        var bodyCorner = DeepCapsuleEdgeCornerRadius(CapsuleChromeCornerRadius);
        var outlineCorner = DeepCapsuleEdgeCornerRadius(
            CapsuleChromeCornerRadius + DeepCapsuleSlotOutlineThickness - DeepCapsuleSlotOutlineOverlap);

        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Margin = leftEdge
                ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
                : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotChrome.HorizontalAlignment = HorizontalAlignment.Stretch;
            _deepCapsuleSlotChrome.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotChrome.Width = double.NaN;
            _deepCapsuleSlotChrome.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset);
            _deepCapsuleSlotChrome.CornerRadius = bodyCorner;
            _deepCapsuleSlotChrome.BorderThickness = leftEdge
                ? new Thickness(0, 1, 1, 1)
                : new Thickness(1, 1, 0, 1);
        }
        if (_deepCapsuleSlotShell != null)
        {
            _deepCapsuleSlotShell.Margin = leftEdge
                ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
                : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotShell.HorizontalAlignment = HorizontalAlignment.Stretch;
            _deepCapsuleSlotShell.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotShell.Width = double.NaN;
            _deepCapsuleSlotShell.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset);
        }
        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.Margin = leftEdge
                ? new Thickness(0, outlineMargin, outlineMargin, outlineMargin)
                : new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
            _deepCapsuleSlotOutline.HorizontalAlignment = HorizontalAlignment.Stretch;
            _deepCapsuleSlotOutline.VerticalAlignment = VerticalAlignment.Top;
            _deepCapsuleSlotOutline.Width = double.NaN;
            _deepCapsuleSlotOutline.Height = Math.Max(0, PaperLayoutDefaults.CapsuleHeight - outlineMargin * 2);
            _deepCapsuleSlotOutline.CornerRadius = outlineCorner;
            _deepCapsuleSlotOutline.BorderThickness = leftEdge
                ? new Thickness(0, DeepCapsuleSlotOutlineThickness, DeepCapsuleSlotOutlineThickness, DeepCapsuleSlotOutlineThickness)
                : new Thickness(DeepCapsuleSlotOutlineThickness, DeepCapsuleSlotOutlineThickness, 0, DeepCapsuleSlotOutlineThickness);
        }

        UpdateDeepCapsuleSlotSegmentCorners();
    }

    private CornerRadius DeepCapsuleEdgeCornerRadius(double radius)
    {
        return MyDeepCapsuleIsLeftEdge
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);
    }

    // Mirror the real-width edge tag. Content (icon + title) faces the interior and the close
    // segment sits on the wall side, exactly like the pre-refactor tag — but the close pixels are
    // real window width (0 at rest, CapsuleCloseWidth revealed), never off-screen overflow.
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

        _deepCapsuleSlotLeftArea.Cursor = Cursors.Hand;

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

        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);
    }

    private double DeepCapsuleTargetCloseWidth()
    {
        return _deepCapsuleCrossQueueDragVisualActive ||
            _deepCapsuleVisualState is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active
                ? CapsuleCloseWidth
                : 0;
    }

    private void ApplyDeepCapsuleSlotCloseWidth(double width)
    {
        if (_deepCapsuleSlotCloseArea == null)
        {
            return;
        }

        width = Math.Clamp(width, 0, CapsuleCloseWidth);
        _deepCapsuleSlotCloseArea.BeginAnimation(FrameworkElement.WidthProperty, null);
        _deepCapsuleSlotCloseArea.BeginAnimation(UIElement.OpacityProperty, null);
        _deepCapsuleSlotCloseArea.Width = width;
        _deepCapsuleSlotCloseArea.Opacity = width / CapsuleCloseWidth;
        _deepCapsuleSlotCloseArea.IsHitTestVisible =
            width >= CapsuleCloseWidth - 0.5 &&
            !_deepCapsuleCrossQueueDragVisualActive &&
            !IsDeepCapsuleReordering;
        UpdateDeepCapsuleSlotSegmentCorners();
    }

    private void UpdateDeepCapsuleSlotSegmentCorners()
    {
        if (_deepCapsuleSlotLeftArea == null || _deepCapsuleSlotCloseArea == null)
        {
            return;
        }

        // Content keeps the rounded interior cap in every docked state; the close segment hugs
        // the square wall side, so it never needs rounding of its own.
        if (MyDeepCapsuleIsLeftEdge)
        {
            _deepCapsuleSlotLeftArea.CornerRadius =
                new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0);
        }
        else
        {
            _deepCapsuleSlotLeftArea.CornerRadius =
                new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius);
        }

        _deepCapsuleSlotCloseArea.CornerRadius = new CornerRadius(0);
    }

    private void ApplyDeepCapsuleSlotHorizontalProgress(double progress)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        progress = Math.Clamp(progress, 0.0, 1.0);
        var width = Lerp(_deepCapsuleSlotStartWidth, _deepCapsuleSlotTargetWidth, progress);
        // Right edge: pin the right side (target + width) and grow leftward.
        // Left edge:  the docked left is fixed at the screen edge; grow rightward.
        var left = MyDeepCapsuleIsLeftEdge
            ? _deepCapsuleSlotRequestedBounds.Left
            : _deepCapsuleSlotRequestedBounds.Right - width;
        var right = left + width;

        // Round the two screen edges, then derive width from their difference — NOT Round(left)
        // plus an independently Round(width), which lets host.Right drift ~1px off the wall as the
        // animated width changes (non-linear rounding: Round(a)+Round(b) != Round(a+b)). Only the
        // right dock showed it; the left dock's wall is host.Left = Round(0), exact by construction.
        // Same wall-exact rule as the settle paths in MoveExpandedDeepCapsuleSlotHost — see the
        // detailed comment there.
        var roundedLeft = RoundDeepCapsuleSlotX(left);
        var roundedRight = RoundDeepCapsuleSlotX(right);
        ApplyDeepCapsuleSlotCloseWidth(Lerp(
            _deepCapsuleSlotStartCloseWidth,
            _deepCapsuleSlotTargetCloseWidth,
            progress));
        SetDeepCapsuleSlotHostHorizontalBounds(roundedLeft, roundedRight - roundedLeft, updateFixedLayout: false);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
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

        var shouldUseExpandedWidth = !keepHiding &&
            !forceRestingOffset &&
            (_deepCapsuleVisualState is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active);
        var targetTop = targetTopOverride ?? DeepCapsuleTopForIndex(_deepCapsuleIndex + _deepCapsuleVisualOffset);

        MoveExpandedDeepCapsuleSlotHost(
            targetTop,
            shouldUseExpandedWidth ? CapsuleCloseWidth : 0,
            animate,
            durationMs,
            keepHiding);
    }

    private double DeepCapsuleVisibleWidth()
    {
        // A resting edge tag owns exactly the pixels it renders: one interior shadow margin plus
        // icon/title content and its padding. There is no hidden full-width pill behind it.
        var dpi = DeepCapsuleSlotDpi();
        var bodyWidth = Math.Ceiling(
            CapsuleLeftPadding +
            MeasureCapsuleIconWidth(dpi.PixelsPerDip) +
            CapsuleIconGap +
            MeasureCapsuleTitleWidth(
                limitForDeepCapsule: true,
                pixelsPerDip: dpi.PixelsPerDip) +
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
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);
        RefreshEffectiveTopmost();

        MoveDeepCapsuleToCurrentTarget(
            animate,
            DeepCapsuleLayout.SlotRetractMoveMilliseconds,
            keepHiding: true,
            forceRestingOffset: true,
            targetTopOverride: anchorTop,
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

        if (!hovering && _deepCapsuleSlotContextMenuOpen)
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

    private void MoveDeepCapsuleCrossQueueDropToCurrentTarget(int durationMs)
    {
        if (!HasDeepCapsuleSlotPlacement || _deepCapsuleSlotHost == null)
        {
            return;
        }

        var host = _deepCapsuleSlotHost;
        var fromLeft = double.IsNaN(host.Left) || double.IsInfinity(host.Left)
            ? _deepCapsuleSlotLeft
            : host.Left;
        var fromTop = double.IsNaN(host.Top) || double.IsInfinity(host.Top)
            ? _deepCapsuleSlotTop
            : host.Top;
        var ghost = CreateDeepCapsuleDropGhost(fromLeft, fromTop);

        SetDeepCapsuleCrossQueueDragVisual(false, animate: false);
        MoveDeepCapsuleToCurrentTarget(animate: false);
        UpdateDeepCapsuleSlotClosePlacement();

        var root = _deepCapsuleSlotHostRoot;
        if (root == null || ghost == null)
        {
            if (root != null)
            {
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.Opacity = 1.0;
                root.IsHitTestVisible = true;
            }
            return;
        }

        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 0.0;
        root.IsHitTestVisible = false;

        var area = DeepCapsuleWorkArea();
        var ghostWidth = double.IsNaN(ghost.Width) || double.IsInfinity(ghost.Width) || ghost.Width <= 1
            ? Math.Max(PaperLayoutDefaults.CapsuleWidth, _deepCapsuleCrossQueueDragWidth)
            : ghost.Width;
        var targetLeft = RoundDeepCapsuleSlotX(MyDeepCapsuleIsLeftEdge
            ? area.Left
            : area.Right - ghostWidth);
        var targetTop = RoundDeepCapsuleSlotY(DeepCapsuleTopForIndex(_deepCapsuleIndex + _deepCapsuleVisualOffset));
        AnimateDeepCapsuleDropGhost(
            ghost,
            RoundDeepCapsuleSlotX(fromLeft),
            RoundDeepCapsuleSlotY(fromTop),
            targetLeft,
            targetTop,
            durationMs,
            progressChanged: progress =>
            {
                // The free drag pill can be wider than the docked tag. Cross-fade into the real
                // target near the end instead of recreating the old off-screen crop on the ghost.
                var blend = Math.Clamp((progress - 0.55) / 0.45, 0, 1);
                ghost.Opacity = 1 - blend;
                root.Opacity = blend;
            },
            completed: () =>
            {
                MoveDeepCapsuleToCurrentTarget(animate: false);
                _controller.ArrangeDeepCapsules(animate: false);
                _deepCapsuleSlotLayoutSettlePending = true;
                ScheduleDeepCapsuleSlotHostLayoutSettle();
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        root.BeginAnimation(UIElement.OpacityProperty, null);
                        root.Opacity = 1.0;
                        root.IsHitTestVisible = true;
                        Dispatcher.BeginInvoke(
                            new Action(ghost.Close),
                            System.Windows.Threading.DispatcherPriority.Background);
                    }),
                    System.Windows.Threading.DispatcherPriority.Render);
            });
    }

    private Window? CreateDeepCapsuleDropGhost(double left, double top)
    {
        if (_deepCapsuleSlotHost == null || _deepCapsuleSlotHostRoot == null)
        {
            return null;
        }

        var width = _deepCapsuleSlotHost.ActualWidth > 1
            ? _deepCapsuleSlotHost.ActualWidth
            : _deepCapsuleSlotHost.Width;
        var height = _deepCapsuleSlotHost.ActualHeight > 1
            ? _deepCapsuleSlotHost.ActualHeight
            : _deepCapsuleSlotHost.Height;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 1)
        {
            width = Math.Max(PaperLayoutDefaults.CapsuleWidth, _deepCapsuleCrossQueueDragWidth);
        }
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 1)
        {
            height = PaperLayoutDefaults.CapsuleHeight;
        }

        var dpi = VisualTreeHelper.GetDpi(_deepCapsuleSlotHostRoot);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(_deepCapsuleSlotHostRoot);

        var image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };

        var ghost = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Topmost = _deepCapsuleSlotHost.Topmost,
            Left = RoundDeepCapsuleSlotX(left),
            Top = RoundDeepCapsuleSlotY(top),
            Width = width,
            Height = height,
            Content = image,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        ghost.SourceInitialized += (_, _) => WindowNative.ApplyNoActivateStyle(ghost);
        ghost.Show();
        return ghost;
    }

    private void AnimateDeepCapsuleDropGhost(
        Window ghost,
        double fromLeft,
        double fromTop,
        double toLeft,
        double toTop,
        int durationMs,
        Action<double>? progressChanged,
        Action completed)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var duration = Math.Max(1, durationMs);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            var progress = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds / duration;
            if (progress >= 1.0)
            {
                ghost.Left = toLeft;
                ghost.Top = toTop;
                progressChanged?.Invoke(1);
                timer.Stop();
                completed();
                return;
            }

            progress = Math.Clamp(progress, 0.0, 1.0);
            var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            ghost.Left = RoundDeepCapsuleSlotX(Lerp(fromLeft, toLeft, eased));
            ghost.Top = RoundDeepCapsuleSlotY(Lerp(fromTop, toTop, eased));
            progressChanged?.Invoke(progress);
        };

        timer.Start();
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
        if (_deepCapsuleCrossQueueDragVisualActive)
        {
            if (animate)
            {
                MoveDeepCapsuleCrossQueueDropToCurrentTarget(DeepCapsuleLayout.SlotMoveMilliseconds);
            }
            else
            {
                SetDeepCapsuleCrossQueueDragVisual(false, animate: false);
                MoveDeepCapsuleToCurrentTarget(animate: false);
            }
        }
        else
        {
            MoveDeepCapsuleToCurrentTarget(
                animate,
                keepActiveUntilRetracted ? DeepCapsuleLayout.SlotRetractMoveMilliseconds : DeepCapsuleLayout.SlotMoveMilliseconds,
                forceRestingOffset: keepActiveUntilRetracted);
        }
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

    public void PreviewDeepCapsulePlacement(int index, int visualOffset = 0, int slotCount = 1)
    {
        if (!HasDeepCapsuleSlotPlacement ||
            _deepCapsuleSlotHost?.IsVisible != true ||
            IsDeepCapsuleReordering ||
            _isCollapseAllRetracted)
        {
            return;
        }

        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        _deepCapsuleSlotCount = Math.Max(1, slotCount);
        MoveDeepCapsuleToCurrentTarget(
            animate: true,
            durationMs: DeepCapsuleLayout.SlotMoveMilliseconds);
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

        var targetTop = DeepCapsuleTopForIndex(index + visualOffset);
        RefreshDeepCapsuleSlotLabel();

        var firstShow = _deepCapsuleSlotHost?.IsVisible != true;
        if (_deepCapsuleCrossQueueDragVisualActive && !firstShow && animate && _controller.State.EnableAnimations)
        {
            // Same drop hand-off as ApplyDeepCapsulePlacement: an expanded-reserved tag can also be
            // the window that just finished a cross-queue drag, and docking it without deactivating
            // the free-pill visual leaves the centered drag layout clipped inside the docked host.
            MoveDeepCapsuleCrossQueueDropToCurrentTarget(DeepCapsuleLayout.SlotMoveMilliseconds);
        }
        else if (firstShow)
        {
            MoveExpandedDeepCapsuleSlotHost(targetTop, CapsuleCloseWidth, animate: false);
        }
        else
        {
            MoveExpandedDeepCapsuleSlotHost(targetTop, CapsuleCloseWidth, animate);
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
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: !_paper.IsCollapsed || !HasDeepCapsuleSlotPlacement);

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
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);

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
        CancelDeepCapsuleReorderDrag();
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
        _deepCapsuleReorderPreviewIndex = -1;
        _deepCapsuleCrossQueueDragUnlocked = false;
        _deepCapsuleDragMouseOffsetY = currentDip.Y - _deepCapsuleSlotHost.Top;
        _deepCapsuleDragMouseOffsetX = currentDip.X - _deepCapsuleSlotHost.Left;

        _deepCapsuleSlotHost.BeginAnimation(Window.LeftProperty, null);
        _deepCapsuleSlotHost.BeginAnimation(Window.TopProperty, null);
        ClearDeepCapsuleSlotHorizontalAnimation();
        SetDeepCapsuleCrossQueueDragVisual(false, animate: false);

        var dragTop = double.IsNaN(_deepCapsuleSlotHost.Top) || double.IsInfinity(_deepCapsuleSlotHost.Top)
            ? _deepCapsuleSlotTop
            : _deepCapsuleSlotHost.Top;
        var dragBounds = UpdateDeepCapsuleSlotRequestedGeometry(dragTop, CapsuleCloseWidth);
        _deepCapsuleDragLeft = dragBounds.Left;

        ApplyDeepCapsuleSlotCloseWidth(CapsuleCloseWidth);
        SetDeepCapsuleSlotHostHorizontalBounds(_deepCapsuleDragLeft, dragBounds.Width);
        WindowNative.BringToFrontNoActivate(_deepCapsuleSlotHost);

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
                BeginDeepCapsuleNativeCrossQueueDrag(currentScreenPos);
                return;
            }

            var targetTop = RoundDeepCapsuleSlotY(cursorDip.Y - _deepCapsuleDragMouseOffsetY);
            if (_deepCapsuleCrossQueueDragUnlocked)
            {
                ApplyDeepCapsuleCrossQueueDragHostBounds(
                    RoundDeepCapsuleSlotX(cursorDip.X - _deepCapsuleDragMouseOffsetX),
                    targetTop);
            }
            else
            {
                _deepCapsuleSlotHost.Left = _deepCapsuleDragLeft;
                _deepCapsuleSlotHost.Top = targetTop;
                _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
                _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
                PreviewDeepCapsuleReorderForCurrentPosition();
            }
        }
    }

    private void PreviewDeepCapsuleReorderForCurrentPosition()
    {
        var dropIndex = DeepCapsuleDropIndexForCurrentPosition();
        if (dropIndex == _deepCapsuleReorderPreviewIndex)
        {
            return;
        }

        _deepCapsuleReorderPreviewIndex = dropIndex;
        _controller.PreviewDeepCapsuleReorder(_paper, dropIndex);
    }

    private void BeginDeepCapsuleNativeCrossQueueDrag(Point currentScreenPos)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleCrossQueueDragUnlocked = true;
        _deepCapsuleCrossQueueDragWidth = DeepCapsuleCrossQueueDragWidth();
        _deepCapsuleDragMouseOffsetX = _deepCapsuleCrossQueueDragWidth / 2.0;
        _deepCapsuleDragMouseOffsetY = PaperLayoutDefaults.CapsuleHeight / 2.0;

        var cursorDip = DeepCapsuleScreenPointToDip(currentScreenPos);
        _deepCapsuleDragLastDip = cursorDip;
        _deepCapsuleDragLastScreenPos = currentScreenPos;
        ApplyDeepCapsuleCrossQueueDragHostBounds(
            RoundDeepCapsuleSlotX(cursorDip.X - _deepCapsuleDragMouseOffsetX),
            RoundDeepCapsuleSlotY(cursorDip.Y - _deepCapsuleDragMouseOffsetY));
        SetDeepCapsuleCrossQueueDragVisual(true, animate: true);
        WindowNative.BringToFrontNoActivate(_deepCapsuleSlotHost);
        Mouse.OverrideCursor = Cursors.SizeAll;

        if (!WindowNative.TryBeginWindowCaptionDrag(_deepCapsuleSlotHost))
        {
            return;
        }

        RefreshDeepCapsuleNativeDragDropPosition();
        EndDeepCapsuleReorderDrag(commit: true);
        ClearCapsuleInteractionKeyboardFocus();
    }

    private void RefreshDeepCapsuleNativeDragDropPosition()
    {
        if (_deepCapsuleSlotHost != null &&
            WindowNative.TryGetWindowScreenBounds(_deepCapsuleSlotHost, out var hostBounds))
        {
            _deepCapsuleSlotHost.Left = RoundDeepCapsuleSlotX(hostBounds.Left);
            _deepCapsuleSlotHost.Top = RoundDeepCapsuleSlotY(hostBounds.Top);
            _deepCapsuleSlotHost.Width = Math.Max(1, RoundDeepCapsuleSlotX(hostBounds.Width));
            _deepCapsuleSlotHost.Height = Math.Max(1, RoundDeepCapsuleSlotY(hostBounds.Height));
            _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
            _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
        }

        Point screenPos;
        if (!WindowNative.TryGetCursorScreenPosition(out screenPos))
        {
            screenPos = _deepCapsuleDragLastScreenPos;
            if (_deepCapsuleSlotHost != null &&
                PresentationSource.FromVisual(_deepCapsuleSlotHost) != null)
            {
                var width = _deepCapsuleSlotHost.ActualWidth > 1 ? _deepCapsuleSlotHost.ActualWidth : _deepCapsuleSlotHost.Width;
                var height = _deepCapsuleSlotHost.ActualHeight > 1 ? _deepCapsuleSlotHost.ActualHeight : _deepCapsuleSlotHost.Height;
                if (double.IsNaN(width) || double.IsInfinity(width) || width <= 1)
                {
                    width = _deepCapsuleCrossQueueDragWidth;
                }
                if (double.IsNaN(height) || double.IsInfinity(height) || height <= 1)
                {
                    height = PaperLayoutDefaults.CapsuleHeight;
                }

                screenPos = _deepCapsuleSlotHost.PointToScreen(new Point(width / 2.0, height / 2.0));
            }
        }

        _deepCapsuleDragLastScreenPos = screenPos;
        _deepCapsuleDragLastDip = DeepCapsuleScreenPointToDip(screenPos);
        if (_deepCapsuleSlotHost != null)
        {
            _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
            _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
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
            _deepCapsuleSlotChrome.BorderThickness = new Thickness(1);
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
                _deepCapsuleSlotShell.ColumnDefinitions[1].Width = GridLength.Auto;
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
            _deepCapsuleSlotOutline.BorderThickness = new Thickness(DeepCapsuleSlotOutlineThickness);
        }

        if (_deepCapsuleSlotLeftArea != null)
        {
            Grid.SetColumn(_deepCapsuleSlotLeftArea, 0);
            _deepCapsuleSlotLeftArea.CornerRadius = new CornerRadius(
                bodyHeight / 2.0,
                0,
                0,
                bodyHeight / 2.0);
            _deepCapsuleSlotLeftArea.Cursor = Cursors.SizeAll;
        }

        if (_deepCapsuleSlotCloseArea != null)
        {
            Grid.SetColumn(_deepCapsuleSlotCloseArea, 1);
            _deepCapsuleSlotCloseArea.Width = CapsuleCloseWidth;
            _deepCapsuleSlotCloseArea.Opacity = 1;
            _deepCapsuleSlotCloseArea.Margin = new Thickness(0);
            _deepCapsuleSlotCloseArea.CornerRadius = new CornerRadius(
                0,
                bodyHeight / 2.0,
                bodyHeight / 2.0,
                0);
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

        // This centered free-pill layout intentionally uses the non-edge column order and both-side
        // rounding. It must NOT leave _appliedSlotEdge pointing at the docked edge: the edge-layout
        // pass short-circuits on `_appliedSlotEdge == edge`, so a stale marker would block the swap
        // back to the docked column order and leave the close segment on the wrong (wall) side.
        _appliedSlotEdge = null;
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

        try
        {
            var crossQueueDragUnlocked = _deepCapsuleCrossQueueDragUnlocked;
            _deepCapsuleCrossQueueDragUnlocked = false;
            _deepCapsuleDragStartMonitorDeviceName = "";
            _deepCapsuleReorderPreviewIndex = -1;
            if (!crossQueueDragUnlocked)
            {
                SetDeepCapsuleCrossQueueDragVisual(false, animate: false);
            }

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

            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            _controller.CompleteDeepCapsuleReorderDrag();
            _controller.RefreshFloatingSurfaceZOrder();
            if (_deepCapsuleSlotLayoutSettlePending)
            {
                ScheduleDeepCapsuleSlotHostLayoutSettle();
            }
        }
    }

    private void CancelDeepCapsuleReorderDrag()
    {
        var wasReordering = IsDeepCapsuleReordering;
        if (!wasReordering && !IsDeepCapsuleSlotPendingClick)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
        _deepCapsuleCrossQueueDragUnlocked = false;
        _deepCapsuleDragStartMonitorDeviceName = "";
        _deepCapsuleReorderPreviewIndex = -1;
        SetDeepCapsuleCrossQueueDragVisual(false, animate: false);
        Mouse.OverrideCursor = null;

        if (_deepCapsuleSlotLeftArea?.IsMouseCaptured == true)
        {
            _deepCapsuleSlotLeftArea.ReleaseMouseCapture();
        }

        if (!wasReordering)
        {
            return;
        }

        _controller.CompleteDeepCapsuleReorderDrag();
        _controller.RefreshFloatingSurfaceZOrder();
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
