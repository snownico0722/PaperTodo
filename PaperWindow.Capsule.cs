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
    private bool CanDisplayAsCapsule()
    {
        return _controller.CanPaperDisplayAsCapsule(_paper);
    }

    private void RefreshCloseButton()
    {
        if (_closeButton == null)
        {
            return;
        }

        if (CanDisplayAsCapsule())
        {
            _closeButton.Content = "─";
            _closeButton.ToolTip = Strings.Get("ToolTipCollapseToCapsule");
            _closeButton.Cursor = Cursors.Hand;
        }
        else
        {
            _closeButton.Content = "×";
            _closeButton.ToolTip = Strings.Get("ToolTipHideThisPaper");
            _closeButton.Cursor = Cursors.Hand;
        }
    }

    public void UpdateCapsuleMode()
    {
        RefreshCloseButton();
        if (!CanDisplayAsCapsule() && _paper.IsCollapsed)
        {
            RestoreFromCapsuleAfterEligibilityLoss();
        }
        else
        {
            RefreshEffectiveTopmost();
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        UpdateTextZoom();
    }

    public void RefreshCapsuleEligibility()
    {
        RefreshCloseButton();
        if (!CanDisplayAsCapsule() && _paper.IsCollapsed)
        {
            RestoreFromCapsuleAfterEligibilityLoss();
            return;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
    }

    private void RestoreFromCapsuleAfterEligibilityLoss()
    {
        var wasDeepCapsulePlaced = HasDeepCapsuleSlotPlacement;
        if (wasDeepCapsulePlaced)
        {
            ShowMainWindowForDeepCapsuleActivation();
        }

        SetCollapsedState(false, animate: true, alignExpandedToDockedEdge: wasDeepCapsulePlaced);
    }

    private void RefreshCapsuleLabel()
    {
        if (_capsuleLabelText == null)
        {
            return;
        }

        _capsuleLabelText.Text = _controller.PaperCapsuleTitle(_paper);
        _capsuleLabelText.ToolTip = _controller.PaperTitleText(_paper);
        if (_capsuleIconText != null)
        {
            _capsuleIconText.Text = CapsuleIconText();
            _capsuleIconText.FontSize = CapsuleIconFontSizeForCurrentPaper();
            _capsuleIconText.Foreground = BrightWeakTextBrush;
        }
        if (_capsuleShell != null)
        {
            _capsuleShell.Width = CapsuleShellLayoutWidth();
        }
        UpdateCapsuleClosePlacement();
        RefreshDeepCapsuleSlotLabel();
        UpdateDeepCapsuleSlotClosePlacement();
    }

    private void RefreshDeepCapsuleSlotLabel()
    {
        if (_deepCapsuleSlotLabelText == null)
        {
            return;
        }

        _deepCapsuleSlotLabelText.Text = _controller.PaperCapsuleTitle(_paper);
        _deepCapsuleSlotLabelText.ToolTip = _controller.PaperTitleText(_paper);
        ScheduleDeepCapsuleSlotMeasureRefresh();
    }

    private void UpdateCapsuleClosePlacement()
    {
        var usesDeepCapsulePresentation = UsesDeepCapsulePresentation;
        if (_capsuleCloseArea != null)
        {
            _capsuleCloseArea.Width = CapsuleCloseWidthForCurrentPlacement();
            _capsuleCloseArea.Margin = usesDeepCapsulePresentation
                ? new Thickness(0, 0, 2, 0)
                : new Thickness(0);
        }

        if (_capsuleCloseGlyphOffset != null)
        {
            _capsuleCloseGlyphOffset.X = usesDeepCapsulePresentation
                ? CapsuleCloseGlyphDeepOffset
                : CapsuleCloseGlyphNormalOffset;
        }

        if (_capsuleShell != null)
        {
            _capsuleShell.Width = CapsuleShellLayoutWidth();
        }

        UpdateDeepCapsuleSlotClosePlacement();
    }

    private void UpdateDeepCapsuleSlotClosePlacement(bool updateHostWidth = true)
    {
        // A visible slot's close segment is owned by the same geometry transition as its host.
        // Theme/label refreshes must not jump it to the next visual state before the host grows.
        if (!IsDeepCapsuleSlotHorizontalAnimating &&
            (_deepCapsuleSlotHost?.IsVisible != true || !HasDeepCapsuleSlotPlacement))
        {
            ApplyDeepCapsuleSlotCloseWidth(DeepCapsuleTargetCloseWidth());
        }

        if (_deepCapsuleSlotCloseArea != null)
        {
            _deepCapsuleSlotCloseArea.Margin = new Thickness(0);
        }

        if (_deepCapsuleSlotCloseGlyphOffset != null)
        {
            _deepCapsuleSlotCloseGlyphOffset.X = 0;
        }

        if (updateHostWidth && _deepCapsuleSlotHost != null && HasDeepCapsuleSlotPlacement)
        {
            ApplyDeepCapsuleSlotHostWidth(_deepCapsuleSlotHost.Width);
        }
    }

    private Point CapsulePointerScreenPosition(MouseEventArgs e)
    {
        if (_capsuleShell != null && PresentationSource.FromVisual(_capsuleShell) != null)
        {
            return _capsuleShell.PointToScreen(e.GetPosition(_capsuleShell));
        }

        return PointToScreen(e.GetPosition(this));
    }

    private void BuildCapsuleShell()
    {
        _capsuleShell = new Grid
        {
            Width = CapsuleShellLayoutWidth(),
            Height = 30,
            Background = Brushes.Transparent
        };
        _capsuleShell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _capsuleShell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftArea = new Border
        {
            Background = Brushes.Transparent,
            // Concentric with the capsule pill's left end.
            CornerRadius = new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius),
            Cursor = Cursors.Hand
        };

        var leftStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // Hug the left edge (with a small inset) instead of centering, so the icon
            // doesn't float in the middle of the left area with dead space beside it.
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(CapsuleLeftPadding, 0, 0, 0)
        };

        var iconText = new TextBlock
        {
            Text = CapsuleIconText(),
            Foreground = BrightWeakTextBrush,
            // Explicit font so the rendered glyph matches what MeasureCapsuleTextWidth measures.
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = CapsuleIconFontSizeForCurrentPaper(),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleIconText = iconText;
        leftStack.Children.Add(iconText);

        _capsuleLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            // Explicit font so the rendered title matches the measured width.
            FontFamily = AppTypography.UiFontFamily,
            FontSize = CapsuleLabelFontSize,
            Margin = new Thickness(CapsuleIconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RefreshCapsuleLabel();
        leftStack.Children.Add(_capsuleLabelText);

        leftArea.Child = leftStack;

        leftArea.MouseEnter += (_, _) => leftArea.Background = HoverBrush;
        leftArea.MouseLeave += (_, _) => leftArea.Background = Brushes.Transparent;

        leftArea.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _mouseDownScreenPos = CapsulePointerScreenPosition(e);
            _isMaybeDragging = true;
            leftArea.CaptureMouse();
            e.Handled = true;
        };

        leftArea.PreviewMouseMove += (s, e) =>
        {
            if (!_isMaybeDragging) return;

            Point currentScreenPos = CapsulePointerScreenPosition(e);
            double deltaX = Math.Abs(currentScreenPos.X - _mouseDownScreenPos.X);
            double deltaY = Math.Abs(currentScreenPos.Y - _mouseDownScreenPos.Y);

            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
                deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isMaybeDragging = false;

                leftArea.ReleaseMouseCapture();
                leftArea.Background = Brushes.Transparent;
                leftArea.Cursor = Cursors.SizeAll;

                try
                {
                    _collapsedFromSnappedBounds = null;
                    _collapsedFromMaximized = false;
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore if mouse state changed unexpectedly
                }
                finally
                {
                    leftArea.Cursor = Cursors.Hand;
                    ClearCapsuleInteractionKeyboardFocus();
                }

                e.Handled = true;
            }
        };

        leftArea.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_isMaybeDragging)
            {
                _isMaybeDragging = false;
                leftArea.ReleaseMouseCapture();

                try
                {
                    ActivateFromCollapsedCapsule();
                }
                finally
                {
                    ClearCapsuleInteractionKeyboardFocus();
                }
                e.Handled = true;
            }
        };

        leftArea.LostMouseCapture += (s, e) =>
        {
            _isMaybeDragging = false;
        };

        leftArea.ContextMenu = BuildPaperContextMenu();
        _capsuleLeftArea = leftArea;

        Grid.SetColumn(leftArea, 0);
        _capsuleShell.Children.Add(leftArea);

        var closeGlyphOffset = new TranslateTransform(0, 0);
        var closeGlyph = new TextBlock
        {
            Text = "×",
            Foreground = WeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = closeGlyphOffset
        };
        _capsuleCloseGlyph = closeGlyph;
        _capsuleCloseGlyphOffset = closeGlyphOffset;

        var capsuleClose = new Border
        {
            Width = CapsuleCloseWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            // Concentric with the capsule pill's right edge.
            CornerRadius = new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipHideThisPaper"),
            Child = closeGlyph
        };
        _capsuleCloseArea = capsuleClose;
        UpdateCapsuleClosePlacement();
        capsuleClose.MouseEnter += (_, _) =>
        {
            leftArea.Background = Brushes.Transparent;
            capsuleClose.Background = HoverBrush;
            closeGlyph.Foreground = TextBrush;
        };
        capsuleClose.MouseLeave += (_, _) =>
        {
            capsuleClose.Background = Brushes.Transparent;
            closeGlyph.Foreground = WeakTextBrush;
            capsuleClose.Opacity = 1.0;
        };
        capsuleClose.MouseLeftButtonDown += (_, e) =>
        {
            capsuleClose.Opacity = 0.72;
            e.Handled = true;
        };
        capsuleClose.MouseLeftButtonUp += (_, e) =>
        {
            capsuleClose.Opacity = 1.0;
            _controller.HidePaper(_paper);
            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };

        Grid.SetColumn(capsuleClose, 1);
        _capsuleShell.Children.Add(capsuleClose);
    }

    public void SetCollapsedState(
        bool collapsed,
        bool animate = true,
        bool saveGeometry = true,
        bool alignExpandedToDockedEdge = false,
        bool activateOnExpand = false)
    {
        animate = animate && _controller.State.EnableAnimations;

        if (collapsed && !CanDisplayAsCapsule())
        {
            if (_paper.IsCollapsed)
            {
                SetCollapsedState(false, animate, saveGeometry, alignExpandedToDockedEdge);
            }
            else
            {
                RefreshCloseButton();
                _paperChrome.ContextMenu = BuildPaperContextMenu();
            }
            return;
        }

        if (_paper.IsCollapsed == collapsed)
        {
            return;
        }

        if (_isApplyingCollapsedState)
        {
            // Capture current animated values to prevent snapping
            double currentWidth = Width;
            double currentHeight = Height;
            double currentShellOpacity = _shell.Opacity;
            double currentCapsuleOpacity = _capsuleShell.Opacity;

            // Set them as local values
            Width = currentWidth;
            Height = currentHeight;
            _shell.Opacity = currentShellOpacity;
            _capsuleShell.Opacity = currentCapsuleOpacity;

            // Clear ongoing animations safely
            BeginAnimation(TransitionProgressProperty, null);
            _shell.BeginAnimation(UIElement.OpacityProperty, null);
            _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
            ResetTransitionVisuals();

            _shell.Width = double.NaN;
            _shell.Height = double.NaN;
            _isApplyingCollapsedState = false;
        }

        _isApplyingCollapsedState = true;
        var transitionGeneration = ++_collapseTransitionGeneration;
        var shouldRestoreCollapseStartPosition = collapsed &&
            IsVisible &&
            WindowState == WindowState.Normal &&
            IsFiniteWindowCoordinate(Left) &&
            IsFiniteWindowCoordinate(Top);
        var collapseStartLeft = shouldRestoreCollapseStartPosition ? Left : 0;
        var collapseStartTop = shouldRestoreCollapseStartPosition ? Top : 0;

        var capsuleWidth = CapsuleWindowWidth();
        double targetWidth = collapsed ? capsuleWidth : _paper.Width;
        double targetHeight = collapsed ? PaperLayoutDefaults.CapsuleHeight : _paper.Height;
        var usesDeepCapsuleMode = _paper.IsVisible && _controller.State.UseCapsuleMode && _controller.State.UseDeepCapsuleMode;
        var arrangeDeepCapsulesAfterCollapse = collapsed && usesDeepCapsuleMode;
        Rect? restoreSnappedBounds = null;
        if (collapsed)
        {
            var wasMaximized = WindowState == WindowState.Maximized;
            var wasSnapped = _isSnappedPresentation;
            _collapsedFromMaximized = wasMaximized;
            // Collapsing ends the snap relationship: a collapsed paper is a free capsule, so it
            // should expand back to its own recorded paper size — NOT re-snap. So we do not
            // remember the snap tile for restore here (only maximized windows are restored, via
            // _collapsedFromMaximized). Clearing the snapped flag also lets geometry save resume.
            EndSnapRelationshipForCollapse();
            // Both maximized AND Windows-snapped windows keep a native "restore rect". After we
            // shrink the WPF window to a capsule, that native memory still says "paper-sized", so
            // the next DragMove would make Windows un-snap/un-maximize the tiny capsule back to
            // full size mid-drag. Clear the native state now (SW_RESTORE). Suppress geometry save
            // so the transient restored rect isn't written over the paper's saved size.
            if (wasMaximized || wasSnapped)
            {
                MoveWindowWithoutGeometrySave(() =>
                {
                    WindowNative.RestoreNativeWindow(this);
                    if (wasMaximized)
                    {
                        WindowState = WindowState.Normal;
                    }
                });
            }
        }
        else if (_collapsedFromSnappedBounds is Rect snappedBounds)
        {
            restoreSnappedBounds = snappedBounds;
            targetWidth = snappedBounds.Width;
            targetHeight = snappedBounds.Height;
        }

        var wasDeepCapsulePlaced = HasDeepCapsuleSlotPlacement;
        var expandingFromDeepCapsuleEdge = !collapsed && usesDeepCapsuleMode && wasDeepCapsulePlaced;
        var arrangeDeepCapsulesAfterExpand = expandingFromDeepCapsuleEdge;
        var keepDeepCapsuleSlotReservation = !collapsed
            && expandingFromDeepCapsuleEdge
            && usesDeepCapsuleMode
            && _controller.State.ShowDeepCapsuleWhileExpanded
            && CanDisplayAsCapsule();
        var returningToHiddenDeepCapsuleSlot = collapsed
            && usesDeepCapsuleMode
            && ExpandedFromDeepCapsuleEdge
            && !_controller.State.ShowDeepCapsuleWhileExpanded;
        Rect? rememberedDeepCapsuleExpandedGeometry = null;
        if (restoreSnappedBounds is null &&
            expandingFromDeepCapsuleEdge &&
            _controller.TryGetRememberedDeepCapsuleExpandedGeometry(_paper, targetWidth, targetHeight, out var rememberedGeometry))
        {
            rememberedDeepCapsuleExpandedGeometry = rememberedGeometry;
            targetWidth = rememberedGeometry.Width;
            targetHeight = rememberedGeometry.Height;
        }
        double finalTargetWidth = RoundToDevicePixelX(targetWidth);
        double finalTargetHeight = RoundToDevicePixelY(targetHeight);

        _paper.IsCollapsed = collapsed;
        if (!collapsed)
        {
            if (_controller.State.ShowDeepCapsuleWhileExpanded &&
                _controller.CanPaperDisplayAsCapsule(_paper) &&
                (expandingFromDeepCapsuleEdge || _controller.State.UseDeepCapsuleMode))
            {
                SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
            }
            SetDeepCapsuleSlotState(keepDeepCapsuleSlotReservation ? DeepCapsuleSlotState.ExpandedReserved : DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(keepDeepCapsuleSlotReservation ? DeepCapsuleVisualState.Active : DeepCapsuleVisualState.Resting);
            if (restoreSnappedBounds is Rect snappedRect)
            {
                MoveWindowWithoutGeometrySave(() =>
                {
                    Left = RoundToDevicePixelX(snappedRect.Left);
                    Top = RoundToDevicePixelY(snappedRect.Top);
                });
            }
            else if (alignExpandedToDockedEdge || expandingFromDeepCapsuleEdge)
            {
                var requiredEdgeInset = keepDeepCapsuleSlotReservation
                    ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                    : 0;
                MoveWindowWithoutGeometrySave(() =>
                {
                    if (rememberedDeepCapsuleExpandedGeometry is Rect rememberedRect)
                    {
                        Left = RoundToDevicePixelX(rememberedRect.Left);
                        Top = RoundToDevicePixelY(rememberedRect.Top);
                    }
                    else
                    {
                        AlignExpandedToDockedEdge(finalTargetWidth, finalTargetHeight, requiredEdgeInset);
                    }
                });
            }
            if (arrangeDeepCapsulesAfterExpand)
            {
                _controller.ArrangeDeepCapsules(animate: true);
            }
        }
        else
        {
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
            }
            if (usesDeepCapsuleMode && !wasDeepCapsulePlaced && !returningToHiddenDeepCapsuleSlot)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
                SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
                _controller.ArrangeDeepCapsules(animate: false);
            }
        }

        RefreshEffectiveTopmost();
        ApplySystemVisibility();
        _controller.MarkDirty();

        if (collapsed)
        {
            RefreshCapsuleLabel();
            _capsuleShell.Visibility = Visibility.Visible;
        }
        else
        {
            _shell.Visibility = Visibility.Visible;

            if (_paper.Type == PaperTypes.Todo)
            {
                RebuildTodoRows();
            }
        }

        // Shadow/margin/corner selection is form-aware and snap-aware; centralize it so a
        // null Effect (snap suppression) can't make the local `is DropShadowEffect` update
        // silently no-op and leave the wrong shadow parameters on the capsule/expanded form.
        ApplyPaperChromePresentation();
        RestoreCollapseStartPositionIfNeeded(shouldRestoreCollapseStartPosition, collapseStartLeft, collapseStartTop);

        if (animate)
        {
            var expandedWidth = collapsed ? RoundToDevicePixelX(Width) : finalTargetWidth;
            var expandedHeight = collapsed ? RoundToDevicePixelY(Height) : finalTargetHeight;
            _transitionBaseWidth = expandedWidth;
            _transitionBaseHeight = expandedHeight;
            _startTransitionWidth = collapsed ? expandedWidth : capsuleWidth;
            _startTransitionHeight = collapsed ? expandedHeight : PaperLayoutDefaults.CapsuleHeight;
            _targetTransitionWidth = collapsed ? finalTargetWidth : expandedWidth;
            _targetTransitionHeight = collapsed ? finalTargetHeight : expandedHeight;
            _isTransitionVisualsActive = true;

            // Prevent shell content reflow/wrapping by locking its size to the expanded dimensions
            _shell.Width = Math.Max(0, expandedWidth - WindowChromeInset);
            _shell.Height = Math.Max(0, expandedHeight - WindowChromeInset);

            TransitionProgress = 0.0;
            UpdateTransitionVisuals(0.0);
            if (!collapsed)
            {
                Width = expandedWidth;
                Height = expandedHeight;
            }

            var easeOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

            var progressAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(collapsed ? CollapseResizeMilliseconds : ExpandAnimationMilliseconds),
                BeginTime = collapsed ? TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds) : TimeSpan.Zero,
                EasingFunction = easeOut
            };

            if (collapsed)
            {
                _shell.Opacity = 1.0;
                _capsuleShell.Opacity = 0.0;

                var fadeOutShell = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds),
                    EasingFunction = easeOut
                };
                _shell.BeginAnimation(UIElement.OpacityProperty, fadeOutShell);

                var fadeInCapsule = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(CollapseResizeMilliseconds),
                    BeginTime = TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds),
                    EasingFunction = easeOut
                };
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeInCapsule);
            }
            else
            {
                _shell.Opacity = 0.0;

                _capsuleShell.Opacity = 1.0;

                var fadeOutCapsule = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(ExpandCapsuleFadeOutMilliseconds),
                    EasingFunction = easeOut
                };
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeOutCapsule);

                var fadeInShell = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(ExpandShellFadeInMilliseconds),
                    BeginTime = TimeSpan.FromMilliseconds(ExpandCapsuleFadeOutMilliseconds),
                    EasingFunction = easeOut
                };
                _shell.BeginAnimation(UIElement.OpacityProperty, fadeInShell);
            }

            progressAnim.Completed += (s, e) =>
            {
                if (transitionGeneration != _collapseTransitionGeneration)
                {
                    return;
                }

                // 1. Set local values before clearing animations to prevent snapping/flicker
                TransitionProgress = 1.0;
                UpdateTransitionVisuals(1.0);

                if (collapsed)
                {
                    _shell.Opacity = 0.0;
                    _capsuleShell.Opacity = 1.0;
                    _shell.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _capsuleShell.Opacity = 0.0;
                    _capsuleShell.Visibility = Visibility.Collapsed;
                    _shell.Opacity = 1.0;
                }

                if (collapsed)
                {
                    MinWidth = capsuleWidth;
                    MinHeight = PaperLayoutDefaults.CapsuleHeight;
                    ResizeMode = ResizeMode.NoResize;
                }
                else
                {
                    MinWidth = PaperLayoutDefaults.MinWidth;
                    MinHeight = PaperLayoutDefaults.MinHeight;
                    ResizeMode = ResizeMode.CanResizeWithGrip;
                }

                Width = finalTargetWidth;
                Height = finalTargetHeight;
                // Re-measure at the final window size before removing the visual scale.
                UpdateLayout();

                // 2. Clear animations
                BeginAnimation(TransitionProgressProperty, null);
                _shell.BeginAnimation(UIElement.OpacityProperty, null);
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
                ResetTransitionVisuals();

                // 3. Unlock shell layout
                _shell.Width = double.NaN;
                _shell.Height = double.NaN;

                _isApplyingCollapsedState = false;
                // Re-judge snap state and force re-apply: transition-time position messages
                // were guarded off, and ResetTransitionVisuals rewrote the corner radius.
                RefreshSnappedPresentation(forceApply: true);
                if (saveGeometry)
                {
                    _controller.UpdateGeometry(_paper, this);
                }
                if (arrangeDeepCapsulesAfterCollapse)
                {
                    _controller.ArrangeDeepCapsules(animate: true);
                    SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.Normal);
                    HideMainWindowForDeepCapsuleRest();
                }
                if (!collapsed)
                {
                    FinishExpandSnapStateRestore();
                }
            };

            BeginAnimation(TransitionProgressProperty, progressAnim);
        }
        else
        {
            BeginAnimation(TransitionProgressProperty, null);
            _shell.BeginAnimation(UIElement.OpacityProperty, null);
            _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
            ResetTransitionVisuals();

            TransitionProgress = 0.0;

            if (collapsed)
            {
                _shell.Visibility = Visibility.Collapsed;
                _shell.Opacity = 0;
                _capsuleShell.Visibility = Visibility.Visible;
                _capsuleShell.Opacity = 1;

                MinWidth = capsuleWidth;
                MinHeight = PaperLayoutDefaults.CapsuleHeight;
                ResizeMode = ResizeMode.NoResize;
            }
            else
            {
                _shell.Visibility = Visibility.Visible;
                _shell.Opacity = 1;
                _capsuleShell.Visibility = Visibility.Collapsed;
                _capsuleShell.Opacity = 0;

                MinWidth = PaperLayoutDefaults.MinWidth;
                MinHeight = PaperLayoutDefaults.MinHeight;
                ResizeMode = ResizeMode.CanResizeWithGrip;
            }

            _shell.Width = double.NaN;
            _shell.Height = double.NaN;

            Width = finalTargetWidth;
            Height = finalTargetHeight;

            _isApplyingCollapsedState = false;
            // Same as the animated path: re-judge and force re-apply after the transition
            // rewrote the chrome visuals.
            RefreshSnappedPresentation(forceApply: true);
            if (saveGeometry)
            {
                _controller.UpdateGeometry(_paper, this);
            }
            if (arrangeDeepCapsulesAfterCollapse)
            {
                _controller.ArrangeDeepCapsules(animate: true);
                SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.Normal);
                HideMainWindowForDeepCapsuleRest();
            }
            if (!collapsed)
            {
                FinishExpandSnapStateRestore();
            }
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_paper.Type == PaperTypes.Note)
        {
            _controller.RefreshTodoRowsForLinkedNote(_paper.Id);
        }

        if (!collapsed && activateOnExpand)
        {
            QueueActivateAfterExpandInteraction(transitionGeneration);
        }
    }

    private void QueueActivateAfterExpandInteraction(int transitionGeneration)
    {
        // Capsule mouse-up handlers clear native keyboard focus after SetCollapsedState returns,
        // and repeat that cleanup at Background priority. Run below Background so the explicit
        // user activation is the final focus operation even when animations are disabled.
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (transitionGeneration != _collapseTransitionGeneration ||
                    _paper.IsCollapsed ||
                    !IsVisible ||
                    _controller.SuppressTopmostForFullscreenForeground)
                {
                    return;
                }

                if (!IsActive && !Activate())
                {
                    return;
                }

                Focus();
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    // Runs when an expand transition finishes. If the paper was collapsed while maximized,
    // re-enter the real maximized window state (rather than leaving a manually-sized rect).
    // Clears the one-shot capture flags either way.
    private void FinishExpandSnapStateRestore()
    {
        if (_collapsedFromMaximized)
        {
            MoveWindowWithoutGeometrySave(() => WindowState = WindowState.Maximized);
        }

        _collapsedFromMaximized = false;
        _collapsedFromSnappedBounds = null;
    }

    // A collapsed paper is a free capsule. Clear both the active snap presentation and the
    // delayed hide/show restore cache so a later ShowPaper cannot resurrect the old tile.
    private void EndSnapRelationshipForCollapse()
    {
        _collapsedFromSnappedBounds = null;
        _snappedPresentationBoundsForRestore = null;
        _isSnappedPresentation = false;
    }

    private void RestoreCollapseStartPositionIfNeeded(bool shouldRestore, double startLeft, double startTop)
    {        if (!shouldRestore ||
            !IsFiniteWindowCoordinate(Left) ||
            !IsFiniteWindowCoordinate(Top))
        {
            return;
        }

        const double tolerance = 0.5;
        if (Math.Abs(Left - startLeft) <= tolerance &&
            Math.Abs(Top - startTop) <= tolerance)
        {
            return;
        }

        MoveWindowWithoutGeometrySave(() =>
        {
            Left = startLeft;
            Top = startTop;
        });
    }

    private static bool IsFiniteWindowCoordinate(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

}
