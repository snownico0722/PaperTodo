using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

/// <summary>
/// Full-screen workbench over the monitor under the cursor. Every fixed workspace is one free
/// canvas, laid out left to right inside a pan/zoom world. The active workspace is live; the
/// others are read-only overviews. Leaving the window (Esc, close, the hotkey or switching to
/// another app) ends the mode.
/// </summary>
internal sealed partial class WorkbenchWindow : Window
{
    private const double SectionGap = 150;
    private const double SectionHeaderHeight = 46;
    private const double MinZoom = 0.08;
    private const double MaxZoom = 2.0;

    private readonly AppController _controller;
    private readonly Grid _viewport;
    private readonly Canvas _world = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private readonly TextBlock _zoomLabel;
    private readonly Border _undoArrangeButton;
    private readonly Border _status;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _statusTimer;
    private readonly List<WorkbenchSection> _sections = new();
    private WorkbenchSection? _live;
    private bool _closing;
    private int _holdOpen;
    private bool _fittedOnce;
    private Point? _panStart;
    private Point _panTranslateStart;
    private Dictionary<string, Point>? _undoArrange;

    public WorkbenchWindow(AppController controller)
    {
        _controller = controller;
        Title = Strings.Get("WorkbenchTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Theme.WorkbenchDeskBrush;
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);

        _world.RenderTransform = new TransformGroup { Children = { _scale, _translate } };
        _viewport = new Grid
        {
            ClipToBounds = true,
            Background = Theme.WorkbenchDeskBrush,
            Focusable = true,
            FocusVisualStyle = null
        };
        _viewport.Children.Add(_world);

        _zoomLabel = new TextBlock
        {
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(12),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            MinWidth = 44,
            TextAlignment = TextAlignment.Right
        };
        _undoArrangeButton = ToolbarButton(Strings.Get("WorkbenchUndoArrange"),
            Strings.Get("WorkbenchUndoArrangeTip"), UndoArrange);
        _undoArrangeButton.Visibility = Visibility.Collapsed;

        _statusText = new TextBlock
        {
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(13),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640
        };
        _status = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 52),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _statusText
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _status.Visibility = Visibility.Collapsed;
        };

        var root = new Grid();
        root.Children.Add(_viewport);
        root.Children.Add(BuildToolbar());
        root.Children.Add(BuildHints());
        root.Children.Add(_status);
        Content = root;

        _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += (_, _) => OnDragTick();

        SourceInitialized += (_, _) => PlaceOnCursorMonitor();
        Loaded += (_, _) =>
        {
            BuildSections();
            FitLive();
            _fittedOnce = true;
        };
        SizeChanged += (_, _) =>
        {
            if (_fittedOnce && _drag == null) ClampTranslate();
        };
        Deactivated += (_, _) =>
        {
            if (_holdOpen > 0 || _closing) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_holdOpen == 0 && !IsActive) RequestClose();
            }, DispatcherPriority.Background);
        };
        Closing += (_, _) =>
        {
            _closing = true;
            CommitFocusedEditor();
            _dragTimer.Stop();
        };
        PreviewKeyDown += OnPreviewKeyDown;

        _viewport.MouseLeftButtonDown += OnViewportMouseDown;
        _viewport.MouseDown += OnViewportMiddleDown;
        _viewport.MouseMove += OnViewportMouseMove;
        _viewport.MouseUp += OnViewportMouseUp;
        _viewport.LostMouseCapture += (_, _) => _panStart = null;
        _viewport.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ZoomAt(e.GetPosition(_viewport), e.Delta);
            e.Handled = true;
        };
        _viewport.MouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            ZoomAt(e.GetPosition(_viewport), e.Delta);
            e.Handled = true;
        };
        _viewport.MouseRightButtonUp += OnViewportRightClick;
    }

    internal void ActivateFromCommand()
    {
        if (_closing) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        _viewport.Focus();
    }

    internal void RequestClose()
    {
        if (_closing) return;
        CommitFocusedEditor();
        Close();
    }

    // ------------------------------------------------------------------ window placement

    private void PlaceOnCursorMonitor()
    {
        if (WindowNative.TryGetCursorScreenPosition(out var cursor) &&
            WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(cursor, out var monitor))
        {
            WindowNative.TryMoveWindowDevicePosition(this,
                new DeviceScreenPoint(monitor.WorkArea.Left + 16, monitor.WorkArea.Top + 16));
        }
        // A borderless maximized window covers the whole monitor it is on, taskbar included.
        WindowState = WindowState.Maximized;
    }

    // ------------------------------------------------------------------ chrome

    private UIElement BuildToolbar()
    {
        var bar = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(16, 14, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Strings.Get("WorkbenchTitle"),
            Foreground = Theme.TextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(14),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 16, 0)
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ToolbarButton(Strings.Get("WorkbenchArrange"), Strings.Get("WorkbenchArrangeTip"), ArrangeLive));
        actions.Children.Add(_undoArrangeButton);
        actions.Children.Add(ToolbarSeparator());
        actions.Children.Add(ToolbarButton(Strings.Get("WorkbenchFitCurrent"), Strings.Get("WorkbenchFitCurrentTip"),
            () => FitLive()));
        if (_controller.WorkbenchWorkspaceCount > 1)
        {
            actions.Children.Add(ToolbarButton(Strings.Get("WorkbenchOverview"), Strings.Get("WorkbenchOverviewTip"),
                () => FitAll()));
        }
        actions.Children.Add(_zoomLabel);
        actions.Children.Add(ToolbarSeparator());
        actions.Children.Add(ToolbarButton("✕", Strings.Get("WorkbenchCloseTip"), RequestClose));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        bar.Child = grid;
        return bar;
    }

    private UIElement BuildHints() => new TextBlock
    {
        Text = Strings.Get("WorkbenchHints"),
        Foreground = Theme.WeakTextBrush,
        FontSize = AppTypography.Scale(11.5),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 16),
        IsHitTestVisible = false
    };

    private static UIElement ToolbarSeparator() => new Rectangle
    {
        Width = 1,
        Height = 18,
        Fill = Theme.PaperBorderBrush,
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Border ToolbarButton(string text, string toolTip, Action action)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Theme.TextBrush,
                FontSize = AppTypography.Scale(12.5),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.MouseEnter += (_, _) => button.Background = Theme.HoverBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private void ShowStatus(string text)
    {
        _statusText.Text = text;
        _status.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_linkDrag != null) { CancelLinkDrag(); e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox)
            {
                // The first Esc leaves the editor (and commits it); the second leaves the mode.
                CommitFocusedEditor();
                e.Handled = true;
                return;
            }
            RequestClose();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.D0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            FitLive();
            e.Handled = true;
        }
    }

    private void CommitFocusedEditor()
    {
        if (Keyboard.FocusedElement is not TextBox) return;
        // LostKeyboardFocus runs each editor's synchronous command before focus settles.
        _viewport.Focus();
        if (Keyboard.FocusedElement is TextBox) Keyboard.ClearFocus();
    }

    // ------------------------------------------------------------------ pan & zoom

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || FindAncestor<WorkbenchCard>(e.OriginalSource as DependencyObject) != null ||
            FindAncestor<WorkbenchSectionButton>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }
        CommitFocusedEditor();
        _viewport.Focus();
        BeginPan(e.GetPosition(_viewport));
        e.Handled = true;
    }

    private void OnViewportMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        BeginPan(e.GetPosition(_viewport));
        e.Handled = true;
    }

    private void BeginPan(Point start)
    {
        _panStart = start;
        _panTranslateStart = new Point(_translate.X, _translate.Y);
        _viewport.CaptureMouse();
        _viewport.Cursor = Cursors.ScrollAll;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var current = e.GetPosition(_viewport);
        _translate.X = _panTranslateStart.X + (current.X - start.X);
        _translate.Y = _panTranslateStart.Y + (current.Y - start.Y);
        ClampTranslate();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null;
        _viewport.ReleaseMouseCapture();
        _viewport.Cursor = null;
        e.Handled = true;
    }

    private void ZoomAt(Point anchor, int delta)
    {
        var factor = Math.Pow(1.0015, delta);
        SetZoom(_scale.ScaleX * factor, anchor);
    }

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var old = _scale.ScaleX;
        if (Math.Abs(zoom - old) < 1e-6) return;
        var worldX = (anchor.X - _translate.X) / old;
        var worldY = (anchor.Y - _translate.Y) / old;
        _scale.ScaleX = _scale.ScaleY = zoom;
        _translate.X = anchor.X - worldX * zoom;
        _translate.Y = anchor.Y - worldY * zoom;
        ClampTranslate();
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => _zoomLabel.Text = $"{Math.Round(_scale.ScaleX * 100)}%";

    private Rect WorldBounds()
    {
        if (_sections.Count == 0) return new Rect(0, 0, 1, 1);
        var bounds = SectionBounds(_sections[0]);
        foreach (var section in _sections.Skip(1)) bounds.Union(SectionBounds(section));
        return bounds;
    }

    private static Rect SectionBounds(WorkbenchSection section) => new(
        section.WorldOrigin.X,
        section.WorldOrigin.Y - SectionHeaderHeight,
        section.CanvasSize.Width,
        section.CanvasSize.Height + SectionHeaderHeight);

    // Keep at least part of the world reachable: never pan it entirely out of view.
    private void ClampTranslate()
    {
        var bounds = WorldBounds();
        var zoom = _scale.ScaleX;
        var width = Math.Max(1, _viewport.ActualWidth);
        var height = Math.Max(1, _viewport.ActualHeight);
        const double keep = 160;
        _translate.X = Math.Clamp(_translate.X, keep - bounds.Right * zoom, width - keep - bounds.Left * zoom);
        _translate.Y = Math.Clamp(_translate.Y, keep - bounds.Bottom * zoom, height - keep - bounds.Top * zoom);
    }

    private void FitLive()
    {
        if (_live == null) { FitAll(); return; }
        Fit(SectionBounds(_live), maxZoom: 1.0);
    }

    private void FitAll() => Fit(WorldBounds(), maxZoom: 1.0);

    private void Fit(Rect bounds, double maxZoom)
    {
        var width = Math.Max(200, _viewport.ActualWidth);
        var height = Math.Max(200, _viewport.ActualHeight);
        const double top = 70;
        const double bottom = 56;
        const double side = 40;
        var zoom = Math.Clamp(Math.Min(
            (width - 2 * side) / Math.Max(1, bounds.Width),
            (height - top - bottom) / Math.Max(1, bounds.Height)), MinZoom, maxZoom);
        _scale.ScaleX = _scale.ScaleY = zoom;
        _translate.X = (width - bounds.Width * zoom) / 2 - bounds.Left * zoom;
        _translate.Y = top + (height - top - bottom - bounds.Height * zoom) / 2 - bounds.Top * zoom;
        UpdateZoomLabel();
    }

    // ------------------------------------------------------------------ helpers

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void HoldOpen(Action action)
    {
        _holdOpen++;
        try { action(); }
        finally
        {
            _holdOpen--;
            if (!_closing && !IsActive) Dispatcher.InvokeAsync(ActivateFromCommand, DispatcherPriority.Background);
        }
    }

    /// <summary>Marker so pan does not start on a section header button.</summary>
    private sealed class WorkbenchSectionButton : Border { }
}
