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
            if (HasDeepCapsuleSlotPlacement)
            {
                ClearDeepCapsulePlacement();
            }
            SetCollapsedState(false, animate: true);
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
            if (HasDeepCapsuleSlotPlacement)
            {
                ClearDeepCapsulePlacement();
            }

            SetCollapsedState(false, animate: true);
            return;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
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
        if (_deepCapsuleSlotShell != null && !IsDeepCapsuleSlotHorizontalAnimating)
        {
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }
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

    private void UpdateDeepCapsuleSlotClosePlacement(bool updateHostViewport = true)
    {
        var usesActivePresentation = _deepCapsuleVisualState is DeepCapsuleVisualState.Active or DeepCapsuleVisualState.Hovered;
        var leftEdge = DeepCapsuleLayout.IsLeftEdge;
        if (_deepCapsuleSlotCloseArea != null)
        {
            _deepCapsuleSlotCloseArea.Width = CapsuleCloseWidth;
            // Breathing gap sits on the interior side of the close button (right edge: right;
            // left edge: left), and only when the close is actually revealed.
            _deepCapsuleSlotCloseArea.Margin = usesActivePresentation
                ? (leftEdge ? new Thickness(2, 0, 0, 0) : new Thickness(0, 0, 2, 0))
                : new Thickness(0);
        }

        if (_deepCapsuleSlotCloseGlyphOffset != null)
        {
            _deepCapsuleSlotCloseGlyphOffset.X = leftEdge ? -CapsuleCloseGlyphDeepOffset : CapsuleCloseGlyphDeepOffset;
        }

        if (_deepCapsuleSlotShell != null && !IsDeepCapsuleSlotHorizontalAnimating)
        {
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }

        if (updateHostViewport && _deepCapsuleSlotHost != null && HasDeepCapsuleSlotPlacement)
        {
            ApplyDeepCapsuleSlotHostViewport(_deepCapsuleSlotHost.Width);
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
            Text = _paper.Type == PaperTypes.Note ? "✎" : "✓",
            Foreground = BrightWeakTextBrush,
            // Explicit font so the rendered glyph matches what MeasureCapsuleTextWidth measures.
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleIconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleIconText = iconText;
        leftStack.Children.Add(iconText);

        _capsuleLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            // Explicit font so the rendered title matches the measured width (the window
            // default is Segoe UI, which has different digit/halfwidth metrics).
            FontFamily = NoteTypography.FontFamily,
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
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore if mouse state changed unexpectedly
                }
                finally
                {
                    leftArea.Cursor = Cursors.Hand;
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

                SetCollapsedState(false);
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
            e.Handled = true;
        };

        Grid.SetColumn(capsuleClose, 1);
        _capsuleShell.Children.Add(capsuleClose);
    }

    public void SetCollapsedState(bool collapsed, bool animate = true, bool saveGeometry = true, bool alignExpandedToDockedEdge = false)
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

        var capsuleWidth = CapsuleWindowWidth();
        double targetWidth = collapsed ? capsuleWidth : _paper.Width;
        double targetHeight = collapsed ? PaperLayoutDefaults.CapsuleHeight : _paper.Height;
        double finalTargetWidth = RoundToDevicePixelX(targetWidth);
        double finalTargetHeight = RoundToDevicePixelY(targetHeight);
        var usesDeepCapsuleMode = _paper.IsVisible && _controller.State.UseCapsuleMode && _controller.State.UseDeepCapsuleMode;
        var arrangeDeepCapsulesAfterCollapse = collapsed && usesDeepCapsuleMode;

        var wasDeepCapsulePlaced = HasDeepCapsuleSlotPlacement;
        var expandingFromDeepCapsuleEdge = !collapsed && usesDeepCapsuleMode && wasDeepCapsulePlaced;
        var arrangeDeepCapsulesAfterExpand = expandingFromDeepCapsuleEdge;
        var keepDeepCapsuleSlotReservation = !collapsed
            && expandingFromDeepCapsuleEdge
            && usesDeepCapsuleMode
            && _controller.State.ShowDeepCapsuleWhileExpanded;
        var returningToHiddenDeepCapsuleSlot = collapsed
            && usesDeepCapsuleMode
            && ExpandedFromDeepCapsuleEdge
            && !_controller.State.ShowDeepCapsuleWhileExpanded;

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
            if (alignExpandedToDockedEdge || expandingFromDeepCapsuleEdge)
            {
                var requiredEdgeInset = keepDeepCapsuleSlotReservation
                    ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                    : 0;
                MoveWindowWithoutGeometrySave(() => AlignExpandedToDockedEdge(finalTargetWidth, finalTargetHeight, requiredEdgeInset));
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
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            if (usesDeepCapsuleMode && !wasDeepCapsulePlaced && !returningToHiddenDeepCapsuleSlot)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
                SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
                _controller.ArrangeDeepCapsules(animate: false);
            }
        }

        RefreshEffectiveTopmost();
        _controller.MarkDirty();

        if (collapsed)
        {
            RefreshCapsuleLabel();
            _capsuleShell.Visibility = Visibility.Visible;

            if (_paperChrome.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            {
                shadow.BlurRadius = 8;
                shadow.Opacity = 0.08;
            }
        }
        else
        {
            _shell.Visibility = Visibility.Visible;

            if (_paper.Type == PaperTypes.Todo)
            {
                RebuildTodoRows();
            }

            if (_paperChrome.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            {
                shadow.BlurRadius = 14;
                shadow.Opacity = 0.18;
            }
        }

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
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
    }

}
