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

        _deepCapsuleSlotHostRoot = new Grid
        {
            Background = null,
            // The docked host and all of its children have the same real bounds. Its shape comes
            // from explicit one-sided corners and borders, never from a clipping viewport.
            ClipToBounds = false,
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
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = !_controller.SuppressTopmostForFullscreenForeground,
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
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
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
            Cursor = Cursors.Hand
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
            BeginDeepCapsulePointerInteraction(DeepCapsuleSlotPointerScreenPosition(e));
            leftArea.CaptureMouse();
            e.Handled = true;
        };
        leftArea.PreviewMouseMove += (_, e) =>
        {
            if ((IsDeepCapsuleReordering || IsDeepCapsuleSlotPendingClick) &&
                e.LeftButton != MouseButtonState.Pressed)
            {
                if (IsDeepCapsuleReordering)
                {
                    EndDeepCapsuleReorderDrag(commit: false);
                    ClearCapsuleInteractionKeyboardFocus();
                }
                else
                {
                    FinishDeepCapsulePointerInteraction();
                }

                if (leftArea.IsMouseCaptured)
                {
                    leftArea.ReleaseMouseCapture();
                }
                e.Handled = true;
                return;
            }

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
            var session = RequireDeepCapsuleDragSession();
            var deltaX = Math.Abs(currentScreenPos.X - session.PointerDownScreenPosition.X);
            var deltaY = Math.Abs(currentScreenPos.Y - session.PointerDownScreenPosition.Y);
            if (CanReorderDeepCapsuleSlot())
            {
                // Start tracking on either axis, but keep the capsule magneted to its edge first.
                // A larger outward pull below unlocks the cross-edge / cross-monitor drag.
                if (deltaY >= SystemParameters.MinimumVerticalDragDistance + DeepCapsuleReorderDragExtraThreshold ||
                    deltaX >= SystemParameters.MinimumHorizontalDragDistance + DeepCapsuleReorderDragExtraThreshold)
                {
                    StartDeepCapsuleReorderDrag(currentScreenPos);
                    e.Handled = true;
                }

                return;
            }

            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
                deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                FinishDeepCapsulePointerInteraction();
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
                FinishDeepCapsulePointerInteraction();
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
            if (_deepCapsuleIgnoreCaptureLoss)
            {
                return;
            }

            if (IsDeepCapsuleSlotPendingClick)
            {
                FinishDeepCapsulePointerInteraction();
            }

            if (!IsDeepCapsuleReordering)
            {
                return;
            }

            // Opening the floating drag HWND often steals capture while the button is still down.
            // Re-capture and keep dragging so move/up still reach this slot instead of cancelling.
            if (Mouse.LeftButton == MouseButtonState.Pressed &&
                leftArea.IsVisible &&
                leftArea.IsEnabled)
            {
                leftArea.CaptureMouse();
                return;
            }

            EndDeepCapsuleReorderDrag(commit: false);
            ClearCapsuleInteractionKeyboardFocus();
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
            CornerRadius = new CornerRadius(0),
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
            _controller.HidePaper(_paper);
            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };

        Grid.SetColumn(_deepCapsuleSlotCloseArea, 1);
        shell.Children.Add(_deepCapsuleSlotCloseArea);

        RefreshDeepCapsuleSlotLabel();
        return shell;
    }

    private DeviceScreenPoint DeepCapsuleSlotPointerScreenPosition(MouseEventArgs e)
    {
        if (_deepCapsuleSlotShell != null && PresentationSource.FromVisual(_deepCapsuleSlotShell) != null)
        {
            return DeviceScreenPoint.FromPoint(
                _deepCapsuleSlotShell.PointToScreen(e.GetPosition(_deepCapsuleSlotShell)));
        }

        return DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this)));
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
                // PointToScreen is physical pixels; convert through the app's global screen-DIP
                // coordinate space. Rounding with this hidden paper HWND's old monitor DPI is the
                // exact mixed-DPI bug this hand-off must avoid.
                var slotOrigin = WindowWorkAreaHelper.DeviceScreenPointToDip(
                    DeviceScreenPoint.FromPoint(_deepCapsuleSlotHost.PointToScreen(new Point(0, 0))));
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
        animate = animate && _controller.State.EnableAnimations;
        var host = EnsureDeepCapsuleSlotHost();
        var requestedBounds = UpdateDeepCapsuleSlotRequestedGeometry(
            targetTop,
            targetCloseWidth,
            out var requestedGeometry);
        targetCloseWidth = _deepCapsuleSlotRequestedCloseWidth;

        if (!keepHiding && IsDeepCapsuleSlotRetracting)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed
                ? DeepCapsuleSlotState.CollapsedDocked
                : DeepCapsuleSlotState.None);
        }

        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            var floatingDragOwnsVisual = _deepCapsuleFloatingDragHost != null;
            _deepCapsuleSlotHostRoot.Opacity = floatingDragOwnsVisual ? 0 : 1;
            _deepCapsuleSlotHostRoot.IsHitTestVisible = !keepHiding && !floatingDragOwnsVisual;
        }

        if (!host.IsVisible)
        {
            _deepCapsuleGeometryGeneration++;
            host.BeginAnimation(Window.OpacityProperty, null);
            host.Opacity = 0;
            ClearDeepCapsuleSlotHorizontalAnimation();
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            ApplyDeepCapsuleSlotHostBounds(requestedBounds);
            _deepCapsuleSlotTop = targetTop;
            host.Show();
            ApplyDeepCapsuleSlotHostBounds(requestedBounds);
            host.Opacity = IsDeepCapsuleRetractedIntoMaster ? 0 : 1;
            _deepCapsuleSlotLayoutSettlePending = true;
            ScheduleDeepCapsuleSlotHostLayoutSettle();
            QueuePendingDeepCapsuleSlotMeasureRefresh();
            RefreshEffectiveTopmost();
            return;
        }

        host.BeginAnimation(Window.OpacityProperty, null);
        if (!IsDeepCapsuleRetractedIntoMaster)
        {
            host.Opacity = 1;
        }

        var generation = ++_deepCapsuleGeometryGeneration;
        if (!animate)
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            ApplyDeepCapsuleSlotHostBounds(requestedBounds);
            _deepCapsuleSlotTop = targetTop;
            SchedulePendingDeepCapsuleHoverReconcile();
            QueuePendingDeepCapsuleSlotMeasureRefresh();
            return;
        }

        ApplyDeepCapsuleSlotFixedLayout();
        var currentBounds = _deepCapsuleSlotDeviceBounds.IsEmpty
            ? requestedBounds
            : _deepCapsuleSlotDeviceBounds;
        var currentTop = double.IsNaN(_deepCapsuleSlotTop)
            ? targetTop
            : _deepCapsuleSlotTop;
        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        var needsHorizontalAnimation =
            currentBounds.Left != requestedBounds.Left ||
            currentBounds.Right != requestedBounds.Right;
        if (needsHorizontalAnimation)
        {
            _deepCapsuleSlotStartWidth = currentBounds.Width;
            _deepCapsuleSlotTargetWidth = requestedBounds.Width;
            _deepCapsuleSlotWallDeviceX = MyDeepCapsuleIsLeftEdge
                ? requestedBounds.Left
                : requestedBounds.Right;
            _deepCapsuleSlotRestingWidth = Math.Max(1, (int)Math.Round(
                DeepCapsuleVisibleWidth(requestedGeometry.DpiScaleY) * requestedGeometry.DpiScaleX,
                MidpointRounding.AwayFromZero));
            _deepCapsuleSlotHorizontalDpiScaleX = Math.Max(1, requestedGeometry.DpiScaleX);
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
                if (generation != _deepCapsuleGeometryGeneration)
                {
                    return;
                }

                ClearDeepCapsuleSlotHorizontalAnimation();
                ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
                ApplyDeepCapsuleRequestedHorizontalBounds();
                SchedulePendingDeepCapsuleHoverReconcile();
                QueuePendingDeepCapsuleSlotMeasureRefresh();
            };
            BeginAnimation(
                DeepCapsuleSlotHorizontalProgressProperty,
                horizontalAnim,
                System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            ApplyDeepCapsuleSlotCloseWidth(targetCloseWidth);
            ApplyDeepCapsuleRequestedHorizontalBounds();
            SchedulePendingDeepCapsuleHoverReconcile();
            QueuePendingDeepCapsuleSlotMeasureRefresh();
        }

        if (Math.Abs(currentTop - targetTop) >= 0.5)
        {
            AnimateDeepCapsuleSlotHostTop(host, currentTop, targetTop, durationMs, generation);
        }
        else
        {
            ApplyDeepCapsuleSlotTop(targetTop);
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
                var requestedTop = _deepCapsuleSlotRequestedTop;
                var requestedCloseWidth = _deepCapsuleSlotRequestedCloseWidth;

                // A late first-show / DPI settle must not turn a legitimate hover transition
                // into a width snap. Rebase the same real-width animation on fresh monitor
                // geometry; MoveExpandedDeepCapsuleSlotHost owns generation/cancellation.
                if (IsDeepCapsuleSlotHorizontalAnimating)
                {
                    MoveExpandedDeepCapsuleSlotHost(
                        requestedTop,
                        requestedCloseWidth,
                        animate: true,
                        durationMs: DeepCapsuleLayout.SlotMoveMilliseconds);
                    RefreshDeepCapsuleSlotHostLayout(host, root);
                    return;
                }

                var bounds = UpdateDeepCapsuleSlotRequestedGeometry(requestedTop, requestedCloseWidth);
                ClearDeepCapsuleSlotHorizontalAnimation();
                ApplyDeepCapsuleSlotCloseWidth(requestedCloseWidth);
                ApplyDeepCapsuleSlotHostBounds(bounds);
                _deepCapsuleSlotTop = requestedTop;
                SchedulePendingDeepCapsuleHoverReconcile();
                QueuePendingDeepCapsuleSlotMeasureRefresh();

                RefreshDeepCapsuleSlotHostLayout(host, root);
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void RefreshDeepCapsuleSlotHostLayout(Window host, Grid root)
    {
        host.InvalidateMeasure();
        host.InvalidateArrange();
        root.InvalidateMeasure();
        root.InvalidateArrange();
        host.UpdateLayout();
        root.InvalidateVisual();
    }

    private void AnimateDeepCapsuleSlotHostTop(Window host, double from, double to, int durationMs, int generation)
    {
        var geometry = DeepCapsuleMonitorGeometry();
        ApplyDeepCapsuleSlotTop(from, geometry);

        var startedAt = DateTimeOffset.UtcNow;
        var duration = Math.Max(1, durationMs);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            if (generation != _deepCapsuleGeometryGeneration || !ReferenceEquals(host, _deepCapsuleSlotHost))
            {
                timer.Stop();
                return;
            }

            var progress = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds / duration;
            if (progress >= 1.0)
            {
                ApplyDeepCapsuleSlotTop(to, geometry);
                timer.Stop();
                return;
            }

            progress = Math.Clamp(progress, 0.0, 1.0);
            var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            ApplyDeepCapsuleSlotTop(Lerp(from, to, eased), geometry);
        };
        timer.Start();
    }

    private void AnimateSlotHostOpacity(double to, bool animate)
    {
        var host = _deepCapsuleSlotHost;
        if (host == null)
        {
            return;
        }

        var generation = ++_deepCapsuleVisibilityGeneration;
        animate = animate && _controller.State.EnableAnimations;
        if (!animate || Math.Abs(host.Opacity - to) < 0.001)
        {
            host.BeginAnimation(Window.OpacityProperty, null);
            host.Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = host.Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotOpacityFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        anim.Completed += (_, _) =>
        {
            if (generation != _deepCapsuleVisibilityGeneration ||
                !ReferenceEquals(host, _deepCapsuleSlotHost))
            {
                return;
            }

            host.BeginAnimation(Window.OpacityProperty, null);
            host.Opacity = to;
        };
        host.BeginAnimation(Window.OpacityProperty, anim);
    }

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        _deepCapsuleGeometryGeneration++;
        _deepCapsuleVisibilityGeneration++;
        CancelDeepCapsuleReorderDrag();
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: false);
        CloseDeepCapsuleSlotContextMenu();
        if (!_paper.IsCollapsed && DeepCapsuleSlot == DeepCapsuleSlotState.ExpandedReserved)
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
        _deepCapsuleSlotRequestedBounds = default;
        _deepCapsuleSlotDeviceBounds = default;
        _deepCapsuleSlotRequestedTop = 0;
        _deepCapsuleSlotTop = double.NaN;
        _deepCapsuleSlotRequestedCloseWidth = 0;
        _deepCapsuleSlotMeasureRefreshPending = false;
        _deepCapsuleSlotMeasureRefreshScheduled = false;
        _deepCapsuleHoverReconcilePending = false;
        _deepCapsuleHoverReconcileScheduled = false;
        _deepCapsuleSlotHost.Content = null;
        _deepCapsuleSlotHost.Close();
        _deepCapsuleSlotHost = null;
        _deepCapsuleSlotHostRoot = null;
        _deepCapsuleSlotChrome = null;
        _deepCapsuleSlotOutline = null;
        _deepCapsuleSlotShell = null;
        _deepCapsuleSlotLeftArea = null;
        _deepCapsuleSlotLeftStack = null;
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
        _controller.SetDeepCapsuleContextMenuOpen(_paper.Id, open);

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

    private MonitorGeometry DeepCapsuleMonitorGeometry()
    {
        if (WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _paper.CapsuleMonitorDeviceName,
                _deepCapsuleSlotHost,
                out var geometry))
        {
            return geometry;
        }

        var dpi = _deepCapsuleSlotHost != null
            ? VisualTreeHelper.GetDpi(_deepCapsuleSlotHost)
            : VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        return new MonitorGeometry(
            "",
            new DeviceScreenRect(
                (int)Math.Round(area.Left * dpi.DpiScaleX),
                (int)Math.Round(area.Top * dpi.DpiScaleY),
                (int)Math.Round(area.Right * dpi.DpiScaleX),
                (int)Math.Round(area.Bottom * dpi.DpiScaleY)),
            Math.Max(1, dpi.DpiScaleX),
            Math.Max(1, dpi.DpiScaleY));
    }

    private DpiScale DeepCapsuleSlotDpi()
    {
        var geometry = DeepCapsuleMonitorGeometry();
        return new DpiScale(geometry.DpiScaleX, geometry.DpiScaleY);
    }

    private DeviceScreenRect UpdateDeepCapsuleSlotRequestedGeometry(
        double targetTop,
        double targetCloseWidth)
    {
        return UpdateDeepCapsuleSlotRequestedGeometry(
            targetTop,
            targetCloseWidth,
            out _);
    }

    private DeviceScreenRect UpdateDeepCapsuleSlotRequestedGeometry(
        double targetTop,
        double targetCloseWidth,
        out MonitorGeometry geometry)
    {
        targetCloseWidth = Math.Clamp(targetCloseWidth, 0, CapsuleCloseWidth);
        geometry = DeepCapsuleMonitorGeometry();
        var targetWidthDip = DeepCapsuleVisibleWidth(geometry.DpiScaleY) + targetCloseWidth;
        var width = Math.Max(1, (int)Math.Round(
            targetWidthDip * geometry.DpiScaleX,
            MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(
            PaperLayoutDefaults.CapsuleHeight * geometry.DpiScaleY,
            MidpointRounding.AwayFromZero));
        var top = geometry.WorkArea.Top +
            (int)Math.Round(targetTop * geometry.DpiScaleY, MidpointRounding.AwayFromZero);
        var left = MyDeepCapsuleIsLeftEdge
            ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - width;

        _deepCapsuleSlotRequestedTop = targetTop;
        _deepCapsuleSlotRequestedCloseWidth = targetCloseWidth;
        _deepCapsuleSlotRequestedBounds = new DeviceScreenRect(
            left,
            top,
            left + width,
            top + height);
        return _deepCapsuleSlotRequestedBounds;
    }
    private void ScheduleDeepCapsuleSlotMeasureRefresh()
    {
        var host = _deepCapsuleSlotHost;
        if (host?.IsVisible != true || !HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        _deepCapsuleSlotMeasureRefreshPending = true;
        QueuePendingDeepCapsuleSlotMeasureRefresh();
    }

    private void QueuePendingDeepCapsuleSlotMeasureRefresh()
    {
        var host = _deepCapsuleSlotHost;
        if (!_deepCapsuleSlotMeasureRefreshPending ||
            host?.IsVisible != true ||
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
                if (!_deepCapsuleSlotMeasureRefreshPending)
                {
                    return;
                }

                if (!host.IsVisible ||
                    !HasDeepCapsuleSlotPlacement)
                {
                    _deepCapsuleSlotMeasureRefreshPending = false;
                    return;
                }

                // Measurement is not a semantic placement transition. Let the active hover/drag
                // finish, then resize the real HWND using its already-requested close segment.
                if (_deepCapsuleFloatingDragHost != null ||
                    IsDeepCapsuleReordering ||
                    IsDeepCapsuleSlotHorizontalAnimating)
                {
                    return;
                }

                _deepCapsuleSlotMeasureRefreshPending = false;
                var requestedTop = _deepCapsuleSlotRequestedTop;
                var requestedCloseWidth = _deepCapsuleSlotRequestedCloseWidth;
                var bounds = UpdateDeepCapsuleSlotRequestedGeometry(
                    requestedTop,
                    requestedCloseWidth);
                ApplyDeepCapsuleSlotCloseWidth(requestedCloseWidth);
                ApplyDeepCapsuleSlotHostBounds(bounds);
                _deepCapsuleSlotTop = requestedTop;
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private double MyTopForIndex(int index, int slotCount)
    {
        return DeepCapsuleLayout.TopForIndex(
            index,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            DeepCapsuleLayout.LocalWorkAreaForQueue(_paper.CapsuleMonitorDeviceName),
            slotCount);
    }

    private void ApplyDeepCapsuleSlotHostBounds(
        DeviceScreenRect bounds,
        bool updateFixedLayout = true)
    {
        if (_deepCapsuleSlotHost == null || bounds.IsEmpty)
        {
            return;
        }

        if (updateFixedLayout)
        {
            ApplyDeepCapsuleSlotFixedLayout();
        }

        if (WindowNative.TrySetWindowDeviceBounds(_deepCapsuleSlotHost, bounds))
        {
            _deepCapsuleSlotDeviceBounds = bounds;
        }
    }

    private void ApplyDeepCapsuleSlotTop(double topDip)
    {
        ApplyDeepCapsuleSlotTop(topDip, DeepCapsuleMonitorGeometry());
    }

    private void ApplyDeepCapsuleSlotTop(double topDip, MonitorGeometry geometry)
    {
        var top = geometry.WorkArea.Top +
            (int)Math.Round(topDip * geometry.DpiScaleY, MidpointRounding.AwayFromZero);
        var height = Math.Max(1, (int)Math.Round(
            PaperLayoutDefaults.CapsuleHeight * geometry.DpiScaleY,
            MidpointRounding.AwayFromZero));
        var horizontal = !_deepCapsuleSlotDeviceBounds.IsEmpty
            ? _deepCapsuleSlotDeviceBounds
            : _deepCapsuleSlotRequestedBounds;
        if (horizontal.IsEmpty)
        {
            return;
        }

        ApplyDeepCapsuleSlotHostBounds(horizontal.WithVerticalEdges(top, top + height), updateFixedLayout: false);
        _deepCapsuleSlotTop = topDip;
    }

    private void ApplyDeepCapsuleRequestedHorizontalBounds()
    {
        if (_deepCapsuleSlotRequestedBounds.IsEmpty)
        {
            return;
        }

        var vertical = _deepCapsuleSlotDeviceBounds.IsEmpty
            ? _deepCapsuleSlotRequestedBounds
            : _deepCapsuleSlotDeviceBounds;
        ApplyDeepCapsuleSlotHostBounds(
            new DeviceScreenRect(
                _deepCapsuleSlotRequestedBounds.Left,
                vertical.Top,
                _deepCapsuleSlotRequestedBounds.Right,
                vertical.Bottom),
            updateFixedLayout: false);
    }

    private double DeepCapsuleFloatingDragWidth()
    {
        // The detached drag surface is a complete pill with a transparent margin on both sides.
        // It is never reused as the docked edge tag.
        return Math.Max(
            PaperLayoutDefaults.CapsuleWidth,
            ExpandedDeepCapsuleVisibleWidth() + WindowChromeMargin);
    }

    private DeepCapsuleDragWindow CreateDeepCapsuleFloatingDragHost(DeviceScreenPoint pointer)
    {
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: false);

        var width = DeepCapsuleFloatingDragWidth();
        var height = PaperLayoutDefaults.CapsuleHeight;
        var bodyHeight = Math.Max(1, height - WindowChromeInset);
        var bodyRadius = bodyHeight / 2.0;
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var host = new DeepCapsuleDragWindow(new DeepCapsuleDragWindowOptions
        {
            Width = width,
            Height = height,
            BodyHeight = bodyHeight,
            BodyRadius = bodyRadius,
            WindowChromeMargin = WindowChromeMargin,
            OutlineMargin = outlineMargin,
            OutlineThickness = DeepCapsuleSlotOutlineThickness,
            OutlineOverlap = DeepCapsuleSlotOutlineOverlap,
            CloseWidth = CapsuleCloseWidth,
            LeftPadding = CapsuleLeftPadding,
            IconGap = CapsuleIconGap,
            RightPadding = CapsuleRightPadding,
            Icon = CapsuleIconText(),
            Label = _controller.PaperCapsuleTitle(_paper),
            IconFontSize = CapsuleIconFontSizeForCurrentPaper(),
            LabelFontSize = CapsuleLabelFontSize,
            UiFontFamily = AppTypography.UiFontFamily,
            SymbolFontFamily = AppTypography.SymbolFontFamily,
            Language = AppTypography.Language,
            PaperBrush = PaperBrush,
            PaperBorderBrush = PaperBorderBrush,
            IconBrush = BrightWeakTextBrush,
            LabelBrush = WeakTextBrush,
            OutlineBrush = Theme.CapsuleFocusBorderBrush,
            ShowOutline = IsDeepCapsuleSlotActive,
            Topmost = _deepCapsuleSlotHost?.Topmost == true
        });
        host.UnexpectedlyClosed += OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;

        _deepCapsuleFloatingDragHost = host;
        try
        {
            host.ShowWithEntrance(
                pointer,
                _controller.State.EnableAnimations,
                DeepCapsuleCrossQueueDragScaleFrom,
                DeepCapsuleCrossQueueDragMorphMilliseconds);
            return host;
        }
        catch
        {
            _deepCapsuleFloatingDragHost = null;
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            try
            {
                host.CloseFromOwner();
            }
            catch
            {
                // Preserve the original Show failure; the interaction caller owns rollback.
            }
            throw;
        }
    }

    private void MoveDeepCapsuleFloatingDragHost(DeviceScreenPoint pointer)
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        _deepCapsuleFloatingDragHost.MoveCenteredAt(pointer);
    }

    private void SetDeepCapsuleDockedRootSuppressedForFloatingDrag(bool suppressed)
    {
        if (_deepCapsuleSlotHostRoot == null)
        {
            return;
        }

        _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
        _deepCapsuleSlotHostRoot.Opacity = suppressed ? 0 : 1;
        _deepCapsuleSlotHostRoot.IsHitTestVisible = !suppressed &&
            !IsDeepCapsuleSlotRetracting &&
            !IsDeepCapsuleRetractedIntoMaster &&
            HasDeepCapsuleSlotPlacement;
    }

    private void ReconcileDeepCapsuleHostPresentation()
    {
        var host = _deepCapsuleSlotHost;
        var root = _deepCapsuleSlotHostRoot;
        if (host == null || root == null)
        {
            return;
        }

        host.BeginAnimation(Window.OpacityProperty, null);
        root.BeginAnimation(UIElement.OpacityProperty, null);

        if (!HasDeepCapsuleSlotPlacement || IsDeepCapsuleSlotRetracting)
        {
            root.Opacity = 1.0;
            root.IsHitTestVisible = false;
            host.Opacity = 1.0;
            if (host.IsVisible)
            {
                host.Hide();
            }
            return;
        }

        var floatingOwnsVisual = _deepCapsuleFloatingDragHost != null || IsDeepCapsuleFloatingReordering;
        root.Opacity = floatingOwnsVisual ? 0.0 : 1.0;
        root.IsHitTestVisible = !floatingOwnsVisual && !IsDeepCapsuleRetractedIntoMaster;
        host.Opacity = IsDeepCapsuleRetractedIntoMaster ? 0.0 : 1.0;
    }

    private void CloseDeepCapsuleFloatingDragHost(bool restoreDockedRoot)
    {
        var host = _deepCapsuleFloatingDragHost;
        _deepCapsuleFloatingDragHost = null;
        if (host != null)
        {
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            host.CloseFromOwner();
        }

        if (restoreDockedRoot)
        {
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
        }
    }

    private void OnDeepCapsuleFloatingDragHostUnexpectedlyClosed(object? sender, EventArgs e)
    {
        if (sender is not DeepCapsuleDragWindow host ||
            !ReferenceEquals(host, _deepCapsuleFloatingDragHost))
        {
            return;
        }

        host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
        _deepCapsuleFloatingDragHost = null;
        SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
        if (!IsDeepCapsuleFloatingReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private void ApplyDeepCapsuleSlotFixedLayout()
    {
        // This visual tree has exactly one responsibility: render the current queue edge. Cross-
        // queue dragging is hosted elsewhere and must never add a second layout branch here.
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

    // Mirror the real-width edge tag. The close segment owns the wall side; icon/title content
    // moves inward as that segment grows and never reverses after a floating drag.
    private void ApplyDeepCapsuleSlotEdgeLayout()
    {
        // This window's OWN queue edge — NOT the global anchor. With per-edge queues a capsule on
        // the left edge must flip its column order / close area / corner radii independently of
        // whatever edge other queues use.
        var edge = MyDeepCapsuleEdge;
        if (_deepCapsuleSlotShell == null ||
            _deepCapsuleSlotLeftArea == null ||
            _deepCapsuleSlotCloseArea == null ||
            _deepCapsuleSlotLeftStack == null)
        {
            return;
        }

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
            ? new Thickness(CapsuleLeftPadding, 0, 0, 0)
            : new Thickness(0, 0, CapsuleLeftPadding, 0);

        if (_deepCapsuleSlotLeftStack.ColumnDefinitions.Count >= 2)
        {
            _deepCapsuleSlotLeftStack.ColumnDefinitions[0].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            _deepCapsuleSlotLeftStack.ColumnDefinitions[1].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
        }

        if (_deepCapsuleSlotIconText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotIconText, leftEdge ? 0 : 1);
            _deepCapsuleSlotIconText.HorizontalAlignment = leftEdge
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            _deepCapsuleSlotIconText.TextAlignment = leftEdge
                ? TextAlignment.Left
                : TextAlignment.Right;
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            Grid.SetColumn(_deepCapsuleSlotLabelText, leftEdge ? 1 : 0);
            _deepCapsuleSlotLabelText.Margin = leftEdge
                ? new Thickness(CapsuleIconGap, 0, 0, 0)
                : new Thickness(0, 0, CapsuleIconGap, 0);
            _deepCapsuleSlotLabelText.TextAlignment = leftEdge
                ? TextAlignment.Left
                : TextAlignment.Right;
        }

        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);
    }

    private double DeepCapsuleTargetCloseWidth()
    {
        return DeepCapsuleVisual is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active
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
            _deepCapsuleFloatingDragHost == null &&
            !IsDeepCapsuleReordering;
        UpdateDeepCapsuleSlotSegmentCorners();
    }

    private void UpdateDeepCapsuleSlotSegmentCorners()
    {
        if (_deepCapsuleSlotLeftArea == null || _deepCapsuleSlotCloseArea == null)
        {
            return;
        }

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

        // The wall side belongs to the close segment and is always square. The outer chrome owns
        // the same square wall edge, so no hidden/off-screen radius is needed at any width.
        _deepCapsuleSlotCloseArea.CornerRadius = new CornerRadius(0);
    }

    private void ApplyDeepCapsuleSlotHorizontalProgress(double progress)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        progress = Math.Clamp(progress, 0.0, 1.0);
        var width = Math.Max(1, (int)Math.Round(
            Lerp(_deepCapsuleSlotStartWidth, _deepCapsuleSlotTargetWidth, progress),
            MidpointRounding.AwayFromZero));
        var left = MyDeepCapsuleIsLeftEdge
            ? _deepCapsuleSlotWallDeviceX
            : _deepCapsuleSlotWallDeviceX - width;
        var right = MyDeepCapsuleIsLeftEdge
            ? _deepCapsuleSlotWallDeviceX + width
            : _deepCapsuleSlotWallDeviceX;
        var vertical = _deepCapsuleSlotDeviceBounds.IsEmpty
            ? _deepCapsuleSlotRequestedBounds
            : _deepCapsuleSlotDeviceBounds;

        // The applied, integer HWND width is the only horizontal animation channel. Derive the
        // close segment from that exact width so WPF layout cannot get ahead of (or lag behind)
        // the native right-edge rectangle because of a second floating-point interpolation.
        var closeWidth = (width - _deepCapsuleSlotRestingWidth) /
            _deepCapsuleSlotHorizontalDpiScaleX;
        ApplyDeepCapsuleSlotCloseWidth(closeWidth);
        var bounds = new DeviceScreenRect(left, vertical.Top, right, vertical.Bottom);
        if (bounds != _deepCapsuleSlotDeviceBounds)
        {
            ApplyDeepCapsuleSlotHostBounds(bounds, updateFixedLayout: false);
        }
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
        if (!HasDeepCapsuleSlotPlacement || (IsDeepCapsuleRetractedIntoMaster && !allowCollapseAllRetracted))
        {
            return;
        }

        var shouldUseExpandedWidth = !keepHiding &&
            !forceRestingOffset &&
            (DeepCapsuleVisual is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active);
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
    public void RetractIntoMaster(bool animate)
    {
        if (!_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible ||
            !_controller.CanPaperDisplayAsCapsule(_paper))
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        SetDeepCapsuleSlotState(_paper.IsCollapsed
            ? DeepCapsuleSlotState.RetractedCollapsed
            : DeepCapsuleSlotState.RetractedExpanded);
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);
        RefreshEffectiveTopmost();

        MoveDeepCapsuleToCurrentTarget(
            animate,
            DeepCapsuleLayout.SlotRetractMoveMilliseconds,
            keepHiding: true,
            forceRestingOffset: true,
            targetTopOverride: DeepCapsuleTopForIndex(0),
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

        if (IsDeepCapsuleSlotHorizontalAnimating)
        {
            // Moving the right-edge HWND changes its left boundary every frame. Windows may emit
            // transient enter/leave pairs during that native resize; reconcile once the bounds are
            // stable instead of reversing the animation on every pair.
            _deepCapsuleHoverReconcilePending = true;
            return;
        }

        var targetState = hovering
            ? DeepCapsuleVisualState.Hovered
            : DeepCapsuleVisualState.Resting;
        if (DeepCapsuleVisual == targetState)
        {
            return;
        }

        SetDeepCapsuleVisualState(targetState);
        MoveDeepCapsuleToCurrentTarget(animate: _controller.State.EnableAnimations);
    }

    private void SchedulePendingDeepCapsuleHoverReconcile()
    {
        var host = _deepCapsuleSlotHost;
        if (!_deepCapsuleHoverReconcilePending ||
            _deepCapsuleHoverReconcileScheduled ||
            host == null)
        {
            return;
        }

        _deepCapsuleHoverReconcileScheduled = true;
        host.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(host, _deepCapsuleSlotHost))
                {
                    return;
                }

                _deepCapsuleHoverReconcileScheduled = false;
                if (!_deepCapsuleHoverReconcilePending ||
                    IsDeepCapsuleSlotHorizontalAnimating)
                {
                    return;
                }

                _deepCapsuleHoverReconcilePending = false;
                if (!host.IsVisible ||
                    !HasDeepCapsuleSlotPlacement ||
                    !_paper.IsCollapsed ||
                    IsDeepCapsuleReordering)
                {
                    return;
                }

                SetDeepCapsuleHover(
                    _deepCapsuleSlotContextMenuOpen ||
                    _deepCapsuleSlotShell?.IsMouseOver == true);
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CompleteDeepCapsuleFloatingDragDrop()
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
            return;
        }

        if (!HasDeepCapsuleSlotPlacement || _deepCapsuleSlotHost == null || _deepCapsuleSlotHostRoot == null)
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            return;
        }

        // The controller has already resolved the destination queue. Snap the permanently docked
        // host while hidden, then destroy the floating HWND before revealing the docked tree.
        // Keeping this hand-off synchronous avoids a third, mixed-DPI transition state.
        MoveDeepCapsuleToCurrentTarget(animate: false);
        UpdateDeepCapsuleSlotClosePlacement();
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
        _deepCapsuleSlotLayoutSettlePending = true;
        ScheduleDeepCapsuleSlotHostLayoutSettle();
    }

    public void ApplyDeepCapsulePlacement(int index, bool animate = false, int visualOffset = 0, int slotCount = 1)
    {
        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
        // Semantic state changes immediately. The geometry animator already snapshots the current
        // width, so it can retract from Active without keeping a false Active target alive.
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        _deepCapsuleSlotCount = Math.Max(1, slotCount);
        RefreshCapsuleLabel();
        MoveDeepCapsuleToCurrentTarget(
            animate,
            DeepCapsuleLayout.SlotMoveMilliseconds);
        AnimateSlotHostOpacity(1.0, animate);
        if (!IsPaperFormTransitioning)
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
            IsDeepCapsuleRetractedIntoMaster)
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
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        _deepCapsuleSlotCount = Math.Max(1, slotCount);
        RefreshCapsuleLabel();
        UpdateDeepCapsuleSlotHostTheme();

        var targetTop = DeepCapsuleTopForIndex(index + visualOffset);
        RefreshDeepCapsuleSlotLabel();

        var firstShow = _deepCapsuleSlotHost?.IsVisible != true;
        if (firstShow)
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
        if (!IsPaperFormTransitioning && shouldSaveExpandedGeometry)
        {
            _controller.UpdateGeometry(_paper, this);
        }
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        _deepCapsuleGeometryGeneration++;
        _deepCapsuleVisibilityGeneration++;
        if (DeepCapsuleSlot == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        if (IsDeepCapsuleSlotRetracting)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: !_paper.IsCollapsed || !HasDeepCapsuleSlotPlacement);

        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            MoveDeepCapsuleToCurrentTarget(
                animate,
                DeepCapsuleLayout.SlotMoveMilliseconds);
        }
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        _deepCapsuleVisibilityGeneration++;
        _deepCapsuleSlotMeasureRefreshPending = false;
        _deepCapsuleHoverReconcilePending = false;
        if (_deepCapsuleSlotHost == null)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            return;
        }

        if (!animate || !_deepCapsuleSlotHost.IsVisible || _deepCapsuleSlotHostRoot == null)
        {
            if (HasDeepCapsuleSlotPlacement)
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

        if (HasDeepCapsuleSlotPlacement)
        {
            BeginDeepCapsuleSlotRetraction();
        }
        _deepCapsuleSlotHostRoot.IsHitTestVisible = false;
        var hideGeneration = _deepCapsuleVisibilityGeneration;
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = _deepCapsuleSlotHostRoot.Opacity,
            To = 0,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotRetractFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (hideGeneration != _deepCapsuleVisibilityGeneration || _deepCapsuleSlotHost == null)
            {
                return;
            }

            if (HasDeepCapsuleSlotPlacement)
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
        _deepCapsuleSlotMeasureRefreshPending = false;
        _deepCapsuleHoverReconcilePending = false;
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

        BeginDeepCapsuleSlotRetraction();
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostWidth: false);

        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1.0;
        root.IsHitTestVisible = false;

        MoveDeepCapsuleToCurrentTarget(animate: true, durationMs: DeepCapsuleLayout.SlotRetractMoveMilliseconds, keepHiding: true);
        var generation = ++_deepCapsuleVisibilityGeneration;

        var finishTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotReleaseSettleMilliseconds)
        };
        finishTimer.Tick += (_, _) =>
        {
            finishTimer.Stop();
            if (generation == _deepCapsuleVisibilityGeneration &&
                ReferenceEquals(root, _deepCapsuleSlotHostRoot) &&
                IsDeepCapsuleSlotRetracting)
            {
                BeginDeepCapsuleSlotHideFade(generation, root);
            }
        };
        finishTimer.Start();
    }

    private void BeginDeepCapsuleSlotHideFade(int generation, Grid root)
    {
        if (generation != _deepCapsuleVisibilityGeneration ||
            !ReferenceEquals(root, _deepCapsuleSlotHostRoot) ||
            !IsDeepCapsuleSlotRetracting)
        {
            return;
        }

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotReleaseFadeMilliseconds),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (generation != _deepCapsuleVisibilityGeneration ||
                _deepCapsuleSlotHost == null ||
                !ReferenceEquals(root, _deepCapsuleSlotHostRoot) ||
                !IsDeepCapsuleSlotRetracting)
            {
                return;
            }

            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
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
        root.BeginAnimation(UIElement.OpacityProperty, fadeOut, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    public void ClearDeepCapsulePlacement(bool restoreCollapsedPosition = true, bool animate = false)
    {
        CancelDeepCapsuleReorderDrag();
        animate = animate && _controller.State.EnableAnimations;
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);

        var shouldRetractBeforeHide = animate &&
            _deepCapsuleSlotHost?.IsVisible == true &&
            _deepCapsuleSlotHostRoot != null &&
            HasDeepCapsuleSlotPlacement &&
            !IsDeepCapsuleRetractedIntoMaster;

        if (shouldRetractBeforeHide)
        {
            RetractAndHideDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
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
        if (DeepCapsuleSlot == DeepCapsuleSlotState.ExpandedReserved)
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
            if (DeepCapsuleSlot == DeepCapsuleSlotState.ExpandedReserved)
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
            if (DeepCapsuleSlot == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
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

    private void StartDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() ||
            _deepCapsuleSlotHost == null ||
            _deepCapsuleSlotDeviceBounds.IsEmpty)
        {
            return;
        }

        var session = RequireDeepCapsuleDragSession();
        _deepCapsuleHoverReconcilePending = false;
        SetDeepCapsuleGestureState(DeepCapsuleGestureState.DockedReordering);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Hovered);
        session.LastScreenPosition = currentScreenPos;
        session.StartMonitorDeviceName = WindowWorkAreaHelper
            .MonitorAtDeviceScreenPoint(session.PointerDownScreenPosition)?.DeviceName ?? "";
        session.PreviewIndex = -1;
        session.DockedPointerOffsetY = currentScreenPos.Y - _deepCapsuleSlotDeviceBounds.Top;

        ClearDeepCapsuleSlotHorizontalAnimation();
        CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);

        var dragBounds = UpdateDeepCapsuleSlotRequestedGeometry(
            _deepCapsuleSlotTop,
            CapsuleCloseWidth);
        var current = _deepCapsuleSlotDeviceBounds;
        ApplyDeepCapsuleSlotCloseWidth(CapsuleCloseWidth);
        ApplyDeepCapsuleSlotHostBounds(
            new DeviceScreenRect(
                dragBounds.Left,
                current.Top,
                dragBounds.Right,
                current.Bottom));
        WindowNative.BringToFrontNoActivate(_deepCapsuleSlotHost);

        Mouse.OverrideCursor = Cursors.SizeAll;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        var session = RequireDeepCapsuleDragSession();
        session.LastScreenPosition = currentScreenPos;

        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (IsDeepCapsuleDockedReordering &&
            ShouldUnlockDeepCapsuleCrossQueueDrag(currentScreenPos))
        {
            BeginDeepCapsuleFloatingReorder(currentScreenPos);
            return;
        }

        if (IsDeepCapsuleFloatingReordering)
        {
            MoveDeepCapsuleFloatingDragHost(currentScreenPos);
            return;
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var targetDeviceTop = currentScreenPos.Y - session.DockedPointerOffsetY;
        ApplyDeepCapsuleSlotTop(geometry.DeviceYToLocalDip(targetDeviceTop));
        PreviewDeepCapsuleReorderForCurrentPosition();
    }

    private void PreviewDeepCapsuleReorderForCurrentPosition()
    {
        var session = RequireDeepCapsuleDragSession();
        var dropIndex = DeepCapsuleDropIndexForCurrentPosition();
        if (dropIndex == session.PreviewIndex)
        {
            return;
        }

        session.PreviewIndex = dropIndex;
        _controller.PreviewDeepCapsuleReorder(_paper, dropIndex);
    }

    private void BeginDeepCapsuleFloatingReorder(DeviceScreenPoint currentScreenPos)
    {
        if (_deepCapsuleSlotHost == null || !IsDeepCapsuleDockedReordering)
        {
            return;
        }

        var session = RequireDeepCapsuleDragSession();
        session.LastScreenPosition = currentScreenPos;
        var leftArea = _deepCapsuleSlotLeftArea;
        _deepCapsuleIgnoreCaptureLoss = true;
        try
        {
            var floatingHost = CreateDeepCapsuleFloatingDragHost(currentScreenPos);
            SetDeepCapsuleDockedRootSuppressedForFloatingDrag(true);
            SetDeepCapsuleGestureState(DeepCapsuleGestureState.FloatingReordering);
            WindowNative.BringToFrontNoActivate(floatingHost);
            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;
            // Show() of the floating top-level window steals capture from the docked content.
            // Re-capture so subsequent move/up keep driving the floating host.
            if (leftArea != null &&
                Mouse.LeftButton == MouseButtonState.Pressed &&
                !leftArea.IsMouseCaptured)
            {
                leftArea.CaptureMouse();
            }
        }
        catch
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            _deepCapsuleIgnoreCaptureLoss = false;
            if (IsDeepCapsuleFloatingReordering &&
                leftArea != null &&
                Mouse.LeftButton == MouseButtonState.Pressed &&
                !leftArea.IsMouseCaptured)
            {
                leftArea.CaptureMouse();
            }
        }
    }

    private bool ShouldUnlockDeepCapsuleCrossQueueDrag(DeviceScreenPoint currentScreenPos)
    {
        var session = RequireDeepCapsuleDragSession();
        var scaleX = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            session.PointerDownScreenPosition,
            out var startGeometry)
                ? startGeometry.DpiScaleX
                : 1.0;
        var horizontalDelta = Math.Abs(
            currentScreenPos.X - session.PointerDownScreenPosition.X);
        if (horizontalDelta >= DeepCapsuleCrossQueueDragUnlockDistance * scaleX)
        {
            return true;
        }

        return HasDeepCapsuleDragEnteredAnotherMonitor(currentScreenPos);
    }
    private bool HasDeepCapsuleDragEnteredAnotherMonitor(DeviceScreenPoint currentScreenPos)
    {
        var session = RequireDeepCapsuleDragSession();
        if (string.IsNullOrEmpty(session.StartMonitorDeviceName))
        {
            return false;
        }

        var currentMonitor = WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(currentScreenPos);
        return currentMonitor.HasValue &&
            !string.IsNullOrEmpty(currentMonitor.Value.DeviceName) &&
            !string.Equals(
                currentMonitor.Value.DeviceName,
                session.StartMonitorDeviceName,
                StringComparison.Ordinal);
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        var session = RequireDeepCapsuleDragSession();
        var wasFloatingDrag = IsDeepCapsuleFloatingReordering;
        FinishDeepCapsulePointerInteraction();
        try
        {
            Mouse.OverrideCursor = null;
            SetDeepCapsuleVisualState(
                _deepCapsuleSlotShell?.IsMouseOver == true
                    ? DeepCapsuleVisualState.Hovered
                    : DeepCapsuleVisualState.Resting);

            if (commit)
            {
                if (!wasFloatingDrag)
                {
                    _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                    return;
                }

                // Resolve the (monitor, edge) queue under the drop point. If it differs from this
                // paper's current queue, reassign it (cross-edge / cross-monitor move). Otherwise it's
                // a plain vertical reorder within the same queue.
                var targetGeometry = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                    session.LastScreenPosition,
                    out var resolvedGeometry)
                        ? resolvedGeometry
                        : DeepCapsuleMonitorGeometry();
                var targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                    targetGeometry.DeviceName);

                // Choose the nearer physical wall of the target monitor.
                var targetSide = session.LastScreenPosition.X <
                    targetGeometry.WorkArea.Left + targetGeometry.WorkArea.Width / 2.0
                    ? DeepCapsuleSides.Left
                    : DeepCapsuleSides.Right;

                var queueChanged = targetSide != _paper.CapsuleSide ||
                    !string.Equals(targetMonitor, _paper.CapsuleMonitorDeviceName, StringComparison.Ordinal);

                if (queueChanged)
                {
                    _controller.MoveCapsuleToQueue(
                        _paper,
                        targetMonitor,
                        targetSide,
                        session.LastScreenPosition);
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
            if (wasFloatingDrag || _deepCapsuleFloatingDragHost != null)
            {
                CompleteDeepCapsuleFloatingDragDrop();
            }
            else
            {
                SetDeepCapsuleDockedRootSuppressedForFloatingDrag(false);
            }
            _controller.CompleteDeepCapsuleReorderDrag();
            _controller.RefreshFloatingSurfaceZOrder();
            QueuePendingDeepCapsuleSlotMeasureRefresh();
            if (_deepCapsuleSlotLayoutSettlePending)
            {
                ScheduleDeepCapsuleSlotHostLayoutSettle();
            }
        }
    }

    private void CancelDeepCapsuleReorderDrag(bool restoreLayout = false)
    {
        var wasReordering = IsDeepCapsuleReordering;
        if (!wasReordering &&
            !IsDeepCapsuleSlotPendingClick &&
            _deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        FinishDeepCapsulePointerInteraction();
        try
        {
            CloseDeepCapsuleFloatingDragHost(restoreDockedRoot: true);
            Mouse.OverrideCursor = null;

            if (_deepCapsuleSlotLeftArea?.IsMouseCaptured == true)
            {
                _deepCapsuleSlotLeftArea.ReleaseMouseCapture();
            }
        }
        finally
        {
            if (wasReordering)
            {
                if (restoreLayout && _windowLifecycle == PaperWindowLifecycleState.Alive)
                {
                    _controller.ArrangeDeepCapsules(animate: true);
                }
                _controller.CompleteDeepCapsuleReorderDrag();
                _controller.RefreshFloatingSurfaceZOrder();
            }

            QueuePendingDeepCapsuleSlotMeasureRefresh();
        }
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

        var dragBounds = _deepCapsuleFloatingDragHost != null &&
            WindowNative.TryGetWindowDeviceBounds(_deepCapsuleFloatingDragHost, out var floatingBounds)
                ? floatingBounds
                : _deepCapsuleSlotDeviceBounds;
        if (dragBounds.IsEmpty)
        {
            return Math.Clamp(_deepCapsuleIndex, 0, count - 1);
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var centerY = geometry.DeviceYToLocalDip(dragBounds.Top + dragBounds.Height / 2.0);
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
