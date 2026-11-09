using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double FindPopupBaseWidth = 296;

    private Popup? _findPopup;
    private Border? _findHost;
    private TextBox? _findInput;
    private TextBlock? _findCountText;
    private Border? _findDragHandle;
    private Button? _findPreviousButton;
    private Button? _findNextButton;
    private Point? _findDragStart;
    private Point _findDragPopupStart;

    internal bool IsBuiltInFindOpen => _findPopup?.IsOpen == true;

    private void EnsureBuiltInFindPopup()
    {
        if (_findPopup != null || !_isShellBuilt)
        {
            return;
        }

        var host = new Border
        {
            Padding = new Thickness(4, 4, 2, 4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusControl),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Cursor = Cursors.SizeAll
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 72
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var input = new TextBox
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
            ToolTip = "Ctrl+F"
        };
        input.TextChanged += (_, _) =>
        {
            if (!_findSuppressInputTextChanged)
            {
                RebuildFindMatches(preserveCurrent: false);
            }
        };
        input.AddHandler(
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, _) =>
                QueueBuiltInFindPopupFocusExitCheck()),
            handledEventsToo: true);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                HideBuiltInFind(restoreFocus: true);
                return;
            }

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                input.SelectAll();
                return;
            }

            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                HideBuiltInFind(restoreFocus: false);
                CloseExpandedPaperFromWindowGesture();
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                MoveFindMatch(1);
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                MoveFindMatch(-1);
            }
        };
        Grid.SetColumn(input, 0);
        row.Children.Add(input);

        var count = new TextBlock
        {
            Text = "0 / 0",
            MinWidth = 42,
            Margin = new Thickness(5, 0, 3, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 1);
        row.Children.Add(count);

        var previous = FindIconButton("M8,13 V3 M4,7 L8,3 L12,7", "Shift+Enter");
        previous.Click += (_, _) => MoveFindMatch(-1);
        Grid.SetColumn(previous, 2);
        row.Children.Add(previous);

        var next = FindIconButton("M8,3 V13 M4,9 L8,13 L12,9", "Enter");
        next.Click += (_, _) => MoveFindMatch(1);
        Grid.SetColumn(next, 3);
        row.Children.Add(next);

        var close = FindIconButton("M4,4 L12,12 M12,4 L4,12", "Esc");
        close.Click += (_, _) => HideBuiltInFind(restoreFocus: true);
        Grid.SetColumn(close, 4);
        row.Children.Add(close);

        var dragHandle = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetColumn(dragHandle, 5);
        row.Children.Add(dragHandle);

        host.Child = row;
        host.PreviewMouseLeftButtonDown += OnBuiltInFindDragStart;
        host.PreviewMouseMove += OnBuiltInFindDragMove;
        host.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_findDragStart == null)
            {
                return;
            }
            EndBuiltInFindDrag();
            e.Handled = true;
        };
        host.LostMouseCapture += (_, _) =>
        {
            if (!host.IsMouseCaptured)
            {
                _findDragStart = null;
            }
        };

        var popup = new Popup
        {
            PlacementTarget = _paperChrome,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlaceBuiltInFindPopup,
            AllowsTransparency = true,
            StaysOpen = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = host
        };
        popup.Opened += (_, _) =>
        {
            HookBuiltInFindContentChanges();
            RefreshTodoFindCues();
            CancelExperimentalAutoCollapse();
            RefreshExperimentalOpacity();
        };
        popup.Closed += (_, _) =>
        {
            EndBuiltInFindDrag();
            _globalFindMatches = [];
            _globalFindIndex = -1;
            _controller.ReleaseBuiltInFindResultsIfClosed();
            UnhookBuiltInFindContentChanges();
            ReleaseTodoInactiveFindSelection();
            RefreshTodoFindCues();
            RefreshExperimentalOpacity();
        };

        _findPopup = popup;
        _findHost = host;
        _findInput = input;
        _findCountText = count;
        _findDragHandle = dragHandle;
        _findPreviousButton = previous;
        _findNextButton = next;

        LocationChanged += (_, _) => RepositionBuiltInFindPopup();
        SizeChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && _paper.IsCollapsed)
            {
                HideBuiltInFind(restoreFocus: false);
                return;
            }
            RepositionBuiltInFindPopup();
        };
        StateChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && WindowState == WindowState.Minimized)
            {
                HideBuiltInFind(restoreFocus: false);
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsBuiltInFindOpen && !IsVisible)
            {
                HideBuiltInFind(restoreFocus: false);
            }
        };
        Deactivated += OnBuiltInFindOwnerDeactivated;
        Closed += (_, _) => HideBuiltInFind(restoreFocus: false);
        LayoutUpdated += OnBuiltInFindOwnerLayoutUpdated;

        UpdateBuiltInFindVisuals();
        UpdateFindCount();
    }

    private static Button FindIconButton(string pathData, string tooltip)
    {
        var button = IconButton("", tooltip);
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        var icon = new WpfPath
        {
            Data = geometry,
            Width = 16,
            Height = 16,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, CreateForegroundBinding(button));
        button.Content = new Viewbox
        {
            Child = icon,
            IsHitTestVisible = false
        };
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(1, 0, 0, 0);
        button.Focusable = false;
        button.Cursor = Cursors.Arrow;
        return button;
    }

    private CustomPopupPlacement[] PlaceBuiltInFindPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        if (_findPopup?.PlacementRectangle.IsEmpty == false)
        {
            return [new CustomPopupPlacement(new Point(), PopupPrimaryAxis.None)];
        }

        var y = TitleBarHeight + 33;
        var inside = new CustomPopupPlacement(
            new Point(Math.Max(6, targetSize.Width - popupSize.Width + 20), y),
            PopupPrimaryAxis.Horizontal);

        // Default slightly below the title row and let the right edge overhang the paper.
        // WPF still nudges the popup back when it approaches a screen boundary.
        return [inside];
    }

    private void UpdateBuiltInFindVisuals()
    {
        if (_findHost == null ||
            _findInput == null ||
            _findCountText == null)
        {
            return;
        }

        _findHost.Resources["PaperBrushKey"] = PaperBrush;
        _findHost.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        _findHost.Resources["TextBrushKey"] = TextBrush;
        _findHost.Resources["WeakTextBrushKey"] = WeakTextBrush;
        _findHost.Resources["HoverBrushKey"] = HoverBrush;

        _findHost.Background = PaperBrush;
        _findHost.BorderBrush = PaperBorderBrush;
        _findHost.Width = Math.Round(
            Math.Clamp(
                FindPopupBaseWidth * AppTypography.ScaleFactor,
                236,
                420));
        _findHost.Padding = new Thickness(
            AppTypography.Scale(4), AppTypography.Scale(4),
            AppTypography.Scale(2), AppTypography.Scale(4));

        _findInput.Foreground = TextBrush;
        _findInput.CaretBrush = TextBrush;
        _findInput.Background = PaperBrush;
        _findInput.BorderBrush = PaperBorderBrush;
        _findInput.FontFamily = AppTypography.FontFamilyFor(content: true, bold: false);
        _findInput.FontSize = AppTypography.Scale(11.5);
        _findInput.MinHeight = AppTypography.FitChrome(23);
        _findInput.Padding = new Thickness(
            AppTypography.Scale(5),
            AppTypography.Scale(2),
            AppTypography.Scale(5),
            AppTypography.Scale(2));

        _findCountText.Foreground = WeakTextBrush;
        _findCountText.FontFamily = AppTypography.FontFamilyFor(content: false, bold: false);
        _findCountText.FontSize = AppTypography.Scale(10.5);
        _findCountText.MinWidth = AppTypography.FitChrome(42);
        _findCountText.Margin = new Thickness(
            AppTypography.Scale(5),
            0,
            AppTypography.Scale(3),
            0);

        if (_findDragHandle != null)
        {
            _findDragHandle.Background = WeakTextBrush;
            _findDragHandle.Width = AppTypography.Scale(2);
            _findDragHandle.Height = AppTypography.Scale(11);
            _findDragHandle.CornerRadius = new CornerRadius(AppTypography.Scale(1));
            _findDragHandle.Margin = new Thickness(AppTypography.Scale(6), 0, 0, 0);
        }

        UpdateFindButtonMetrics(_findPreviousButton);
        UpdateFindButtonMetrics(_findNextButton);
        if (_findHost.Child is Grid grid && grid.Children.OfType<Button>().LastOrDefault() is { } close)
        {
            UpdateFindButtonMetrics(close);
        }

        _findObservedTypographyScale = AppTypography.ScaleFactor;
    }

    private static void UpdateFindButtonMetrics(Button? button)
    {
        if (button == null)
        {
            return;
        }

        var size = AppTypography.FitChrome(23);
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
        button.FontSize = AppTypography.Scale(11.5);
        if (button.Content is Viewbox icon)
        {
            icon.Width = AppTypography.Scale(16);
            icon.Height = AppTypography.Scale(16);
        }
    }

    private bool BuiltInFindVisualsNeedRefresh()
    {
        if (_findHost == null)
        {
            return false;
        }

        return Math.Abs(_findObservedTypographyScale - AppTypography.ScaleFactor) > 0.001 ||
               !_findHost.Resources.Contains("PaperBrushKey") ||
               !ReferenceEquals(_findHost.Resources["PaperBrushKey"], PaperBrush) ||
               !ReferenceEquals(_findHost.Resources["PaperBorderBrushKey"], PaperBorderBrush) ||
               !ReferenceEquals(_findHost.Resources["TextBrushKey"], TextBrush) ||
               !ReferenceEquals(_findHost.Resources["WeakTextBrushKey"], WeakTextBrush);
    }

    private void RepositionBuiltInFindPopup()
    {
        if (!IsBuiltInFindOpen || _findPopup == null)
        {
            return;
        }

        // WPF Popup does not consistently re-run custom placement while its target HWND moves.
        // A no-op offset invalidation is enough to force placement without closing/reopening it.
        var offset = _findPopup.HorizontalOffset;
        _findPopup.HorizontalOffset = offset + 0.01;
        _findPopup.HorizontalOffset = offset;
    }

    private void OnBuiltInFindDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!IsBuiltInFindOpen || _findHost == null || _findPopup == null)
        {
            return;
        }

        // The grip, count and surrounding space can drag; input selection and buttons keep
        // their normal gestures, including clicks on their otherwise empty padding.
        for (var source = e.OriginalSource as DependencyObject;
             source != null && !ReferenceEquals(source, _findHost);
             source = GetSafeParent(source))
        {
            if (source is TextBoxBase or ButtonBase)
            {
                return;
            }
        }

        var start = e.GetPosition(_paperChrome);
        var popupStart = _paperChrome.PointFromScreen(_findHost.PointToScreen(new Point()));
        if (!_findHost.CaptureMouse())
        {
            return;
        }

        _findDragStart = start;
        _findDragPopupStart = popupStart;
        _findInput?.Focus();
        e.Handled = true;
    }

    private void OnBuiltInFindDragMove(object sender, MouseEventArgs e)
    {
        if (_findDragStart is not Point start || _findPopup == null)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndBuiltInFindDrag();
            return;
        }

        // Use the stationary paper's coordinates, starting at the popup's actual on-screen
        // position after WPF clamping. Moving the placement rectangle also lets WPF choose
        // the monitor at the dragged position, instead of pinning it to the owner's monitor.
        var delta = e.GetPosition(_paperChrome) - start;
        _findPopup.PlacementRectangle = new Rect(_findDragPopupStart + delta, new Size());
        e.Handled = true;
    }

    private void EndBuiltInFindDrag()
    {
        _findDragStart = null;
        if (_findHost?.IsMouseCaptured == true)
        {
            _findHost.ReleaseMouseCapture();
        }
    }

    private void UpdateFindCount()
    {
        if (_findCountText == null)
        {
            return;
        }

        var current = _findMatchIndex >= 0 && _findMatches.Count > 0
            ? _findMatchIndex + 1
            : 0;
        _findCountText.Text = BuiltInFindCountText(current, _findMatches.Count);

        var enabled = _globalFindMatches.Count > 0;
        if (_findPreviousButton != null)
        {
            _findPreviousButton.IsEnabled = enabled;
        }
        if (_findNextButton != null)
        {
            _findNextButton.IsEnabled = enabled;
        }
    }

    private void OnBuiltInFindOwnerDeactivated(object? sender, EventArgs e)
    {
        // Use the same settled focus check as the popup input: a temporary deactivation while
        // restoring a hidden paper must not dismiss find after its owner has reactivated.
        QueueBuiltInFindPopupFocusExitCheck();
    }

    private void RefreshBuiltInFindTypographyFromOwner()
    {
        if (!IsBuiltInFindOpen || _findHost == null)
        {
            return;
        }

        UpdateBuiltInFindVisuals();
        _findHost.Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(_findHost);
        RepositionBuiltInFindPopup();
    }

    private void QueueBuiltInFindPopupFocusExitCheck()
    {
        if (!IsBuiltInFindOpen)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen ||
                _findHost?.IsKeyboardFocusWithin == true ||
                IsActive)
            {
                return;
            }

            HideBuiltInFind(restoreFocus: false);
            ScheduleExperimentalAutoCollapse(blockedAtDeactivation: false);
        }), DispatcherPriority.ContextIdle);
    }
}
