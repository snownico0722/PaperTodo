using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;

namespace PaperTodo;

internal sealed record EdgeCapsuleHostOptions(
    double WindowChromeMargin,
    double ChromeCornerRadius,
    double InnerCornerRadius,
    double OutlineThickness,
    double OutlineOverlap,
    double BodyHeight,
    double LeftPadding,
    double IconGap,
    string IconText,
    double IconFontSize,
    double LabelFontSize,
    string CloseToolTip,
    Brush PaperBrush,
    Brush PaperBorderBrush,
    Brush OutlineBrush,
    Brush HoverBrush,
    Brush IconBrush,
    Brush StrongTextBrush,
    Brush TextBrush,
    FontFamily UiFontFamily,
    FontFamily SymbolFontFamily,
    XmlLanguage Language,
    bool Topmost);

internal sealed record EdgeCapsuleHostCallbacks(
    Action<bool> HoverChanged,
    Action<DeviceScreenPoint> PointerPressed,
    Func<DeviceScreenPoint, bool, bool> PointerMoved,
    Func<DeviceScreenPoint, bool> PointerReleased,
    Func<bool, EdgeCapsuleCaptureAction> CaptureLost,
    Action CloseInvoked);

/// <summary>
/// Owns the docked HWND and every visual belonging to it. PaperWindow supplies content and event
/// callbacks, but host lifetime can no longer be partially cleared across unrelated fields.
/// </summary>
internal sealed class EdgeCapsuleHost : IDisposable
{
    private readonly EdgeCapsuleHostOptions _options;
    private EdgeCapsuleHostCallbacks? _callbacks;
    private Brush _hoverBrush;
    private Brush _textBrush;
    private Brush _weakTextBrush;
    private double _maximumCloseWidth;
    private EdgeCapsuleEdge? _appliedEdge;
    private bool _disposed;
    private Window Window { get; }
    private Grid Root { get; }
    private Border Chrome { get; }
    private Border Outline { get; }
    private Grid Shell { get; }
    private Border ContentArea { get; }
    private Grid ContentGrid { get; }
    private TextBlock Icon { get; }
    private Border CloseArea { get; }
    private TextBlock CloseGlyph { get; }
    private TextBlock Label { get; }

    private EdgeCapsuleHost(
        EdgeCapsuleHostOptions options,
        Window window,
        Grid root,
        Border chrome,
        Border outline,
        Grid shell,
        Border contentArea,
        Grid contentGrid,
        TextBlock icon,
        Border closeArea,
        TextBlock closeGlyph,
        TextBlock label)
    {
        _options = options;
        _hoverBrush = options.HoverBrush;
        _textBrush = options.StrongTextBrush;
        _weakTextBrush = options.TextBrush;
        Window = window;
        Root = root;
        Chrome = chrome;
        Outline = outline;
        Shell = shell;
        ContentArea = contentArea;
        ContentGrid = contentGrid;
        Icon = icon;
        CloseArea = closeArea;
        CloseGlyph = closeGlyph;
        Label = label;
    }

    public bool IsVisible => !_disposed && Window.IsVisible;
    public Dispatcher Dispatcher => Window.Dispatcher;

    public void AttachNativeHooks(HwndSourceHook hook, Action deactivated)
    {
        if (_disposed)
        {
            return;
        }
        var window = Window;
        window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(window);
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(hook);
            }
        };
        window.Deactivated += (_, _) => deactivated();
    }

    /// <summary>
    /// The only docked-surface effect entry. Native bounds, WPF close layout, opacity and hit
    /// testing are committed from the same immutable frame; callers cannot advance one channel
    /// independently of another.
    /// </summary>
    public bool Apply(EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || !frame.IsUsable)
        {
            return false;
        }
        var window = Window;
        var root = Root;

        if (!frame.Visible)
        {
            window.Hide();
            window.Opacity = 1;
            root.Opacity = 1;
            root.IsHitTestVisible = true;
            return true;
        }

        ApplyFixedLayout(frame.Edge);
        var closeWidth = EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
            frame.Bounds.Width,
            frame.RestingWidthDevice,
            frame.DpiScaleX,
            frame.MaximumCloseWidthDip);
        SetCloseWidth(
            closeWidth,
            frame.MaximumCloseWidthDip,
            frame.IsHitTestVisible);
        root.Opacity = Math.Clamp(frame.ContentOpacity, 0, 1);
        root.IsHitTestVisible = frame.IsHitTestVisible;
        Outline.Visibility = frame.OutlineVisible ? Visibility.Visible : Visibility.Collapsed;

        var firstShow = !window.IsVisible;
        if (firstShow)
        {
            window.Opacity = 0;
        }
        if (!WindowNative.TrySetWindowDeviceBounds(window, frame.Bounds))
        {
            return false;
        }
        if (firstShow)
        {
            window.Show();
            if (!WindowNative.TrySetWindowDeviceBounds(window, frame.Bounds))
            {
                return false;
            }
        }
        window.Opacity = Math.Clamp(frame.Opacity, 0, 1);
        return true;
    }

    public bool ContainsScreenPoint(DeviceScreenPoint point)
    {
        var window = Window;
        if (_disposed || !window.IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(window, out var bounds))
        {
            return false;
        }

        // Use the native rectangle, not WPF ActualWidth. During a right-edge SetWindowPos the
        // HWND moves left before WPF's next arrange pass; sampling the stale visual width there
        // turns a synthetic MouseLeave into a real collapse request and causes the visible twitch.
        return point.X >= bounds.Left &&
            point.X < bounds.Right &&
            point.Y >= bounds.Top &&
            point.Y < bounds.Bottom;
    }

    public static EdgeCapsuleHost Create(EdgeCapsuleHostOptions options)
    {
        var root = new Grid
        {
            Background = null,
            ClipToBounds = false,
            Opacity = 1
        };
        var chrome = new Border
        {
            Margin = new Thickness(options.WindowChromeMargin),
            CornerRadius = new CornerRadius(options.ChromeCornerRadius),
            BorderThickness = new Thickness(1),
            Background = options.PaperBrush,
            BorderBrush = options.PaperBorderBrush,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(chrome, 0);
        root.Children.Add(chrome);

        var shell = new Grid
        {
            Width = double.NaN,
            Height = options.BodyHeight,
            Margin = new Thickness(options.WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var contentArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(options.InnerCornerRadius, 0, 0, options.InnerCornerRadius),
            Cursor = Cursors.Hand
        };
        var contentGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(options.LeftPadding, 0, 0, 0)
        };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = options.IconText,
            Foreground = options.IconBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = options.IconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        contentGrid.Children.Add(icon);

        var label = new TextBlock
        {
            Foreground = options.TextBrush,
            FontFamily = options.UiFontFamily,
            FontSize = options.LabelFontSize,
            Margin = new Thickness(options.IconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        contentGrid.Children.Add(label);
        contentArea.Child = contentGrid;
        Grid.SetColumn(contentArea, 0);
        shell.Children.Add(contentArea);

        var closeGlyph = new TextBlock
        {
            Text = "×",
            Foreground = options.TextBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeArea = new Border
        {
            Width = 0,
            Opacity = 0,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Cursor = Cursors.Hand,
            ToolTip = options.CloseToolTip,
            IsHitTestVisible = false,
            Child = closeGlyph
        };
        Grid.SetColumn(closeArea, 1);
        shell.Children.Add(closeArea);
        Panel.SetZIndex(shell, 10);
        root.Children.Add(shell);

        var outlineMargin = options.WindowChromeMargin - options.OutlineThickness + options.OutlineOverlap;
        var outline = new Border
        {
            Margin = new Thickness(outlineMargin),
            CornerRadius = new CornerRadius(
                options.ChromeCornerRadius + options.OutlineThickness - options.OutlineOverlap),
            BorderThickness = new Thickness(options.OutlineThickness),
            BorderBrush = options.OutlineBrush,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(outline, 20);
        root.Children.Add(outline);

        var window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = options.UiFontFamily,
            Language = options.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = options.Topmost,
            Content = root
        };

        return new EdgeCapsuleHost(
            options,
            window,
            root,
            chrome,
            outline,
            shell,
            contentArea,
            contentGrid,
            icon,
            closeArea,
            closeGlyph,
            label);
    }

    public void AttachInput(EdgeCapsuleHostCallbacks callbacks)
    {
        if (_disposed || _callbacks != null)
        {
            return;
        }

        _callbacks = callbacks;
        var shell = Shell;
        var content = ContentArea;
        var close = CloseArea;
        var closeGlyph = CloseGlyph;

        content.MouseEnter += (_, _) => content.Background = _hoverBrush;
        content.MouseLeave += (_, _) => content.Background = Brushes.Transparent;
        shell.MouseEnter += (_, _) => callbacks.HoverChanged(true);
        shell.MouseLeave += (_, _) => callbacks.HoverChanged(false);
        content.PreviewMouseLeftButtonDown += (_, e) =>
        {
            callbacks.PointerPressed(PointerScreenPosition(e));
            content.CaptureMouse();
            e.Handled = true;
        };
        content.PreviewMouseMove += (_, e) =>
        {
            e.Handled = callbacks.PointerMoved(
                PointerScreenPosition(e),
                e.LeftButton == MouseButtonState.Pressed);
        };
        content.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!callbacks.PointerReleased(PointerScreenPosition(e)))
            {
                return;
            }
            if (content.IsMouseCaptured)
            {
                content.ReleaseMouseCapture();
            }
            e.Handled = true;
        };
        content.LostMouseCapture += (_, _) =>
        {
            var action = callbacks.CaptureLost(Mouse.LeftButton == MouseButtonState.Pressed);
            if (action == EdgeCapsuleCaptureAction.Recapture && content.IsVisible && content.IsEnabled)
            {
                content.CaptureMouse();
            }
        };

        close.MouseEnter += (_, _) =>
        {
            content.Background = Brushes.Transparent;
            close.Background = _hoverBrush;
            closeGlyph.Foreground = _textBrush;
        };
        close.MouseLeave += (_, _) =>
        {
            close.Background = Brushes.Transparent;
            closeGlyph.Foreground = _weakTextBrush;
            close.Opacity = Math.Clamp(close.Width / Math.Max(1, _maximumCloseWidth), 0, 1);
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            close.Opacity = 0.72;
            e.Handled = true;
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            callbacks.CloseInvoked();
            e.Handled = true;
        };
    }

    public void SetContextMenu(ContextMenu contextMenu)
    {
        if (!_disposed)
        {
            ContentArea.ContextMenu = contextMenu;
        }
    }

    public bool IsContentPointerCaptured => !_disposed && ContentArea.IsMouseCaptured;
    public bool IsSurfaceMouseOver =>
        WindowNative.TryGetCursorScreenPosition(out var pointer) && ContainsScreenPoint(pointer);
    public bool IsTopmost => !_disposed && Window.Topmost;

    public void CaptureContentPointer()
    {
        if (!_disposed)
        {
            ContentArea.CaptureMouse();
        }
    }

    public void ReleaseContentPointer()
    {
        if (!_disposed && ContentArea.IsMouseCaptured)
        {
            ContentArea.ReleaseMouseCapture();
        }
    }

    public void SetLabel(string label, string toolTip)
    {
        if (!_disposed)
        {
            Label.Text = label;
            Label.ToolTip = toolTip;
        }
    }

    public void ApplyToolTipSetting(bool enabled)
    {
        if (!_disposed)
        {
            ToolTipPreferences.Apply(Window, enabled);
        }
    }

    public void UpdateTypography(
        FontFamily uiFontFamily,
        FontFamily symbolFontFamily,
        System.Windows.Markup.XmlLanguage language)
    {
        if (_disposed)
        {
            return;
        }
        Window.FontFamily = uiFontFamily;
        Window.Language = language;
        Icon.FontFamily = symbolFontFamily;
        Label.FontFamily = uiFontFamily;
    }

    public void SetTopmost(bool topmost, IntPtr insertAfter)
    {
        if (_disposed)
        {
            return;
        }
        Window.Topmost = topmost;
        if (Window.IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(Window, topmost, insertAfter);
        }
    }

    public DeviceScreenPoint ScreenOrigin()
    {
        return _disposed
            ? default
            : DeviceScreenPoint.FromPoint(Window.PointToScreen(new Point(0, 0)));
    }

    public bool ContainsWindowScreenPoint(Point screenPoint)
    {
        return ContainsScreenPoint(DeviceScreenPoint.FromPoint(screenPoint));
    }

    public bool TryGetMonitorGeometry(string? deviceName, out MonitorGeometry geometry)
    {
        if (_disposed)
        {
            geometry = default;
            return false;
        }
        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(deviceName, Window, out geometry);
    }

    public DpiScale Dpi => !_disposed
        ? VisualTreeHelper.GetDpi(Window)
        : new DpiScale(1, 1);

    public void BringToFrontNoActivate()
    {
        if (!_disposed)
        {
            WindowNative.BringToFrontNoActivate(Window);
        }
    }

    private DeviceScreenPoint PointerScreenPosition(MouseEventArgs e)
    {
        if (!_disposed && PresentationSource.FromVisual(Shell) != null)
        {
            return DeviceScreenPoint.FromPoint(Shell.PointToScreen(e.GetPosition(Shell)));
        }
        return default;
    }

    public void UpdateTheme(
        Brush paperBrush,
        Brush paperBorderBrush,
        Brush outlineBrush,
        Brush hoverBrush,
        Brush iconBrush,
        Brush strongTextBrush,
        Brush weakTextBrush,
        string iconText,
        double iconFontSize)
    {
        if (_disposed)
        {
            return;
        }
        _hoverBrush = hoverBrush;
        _textBrush = strongTextBrush;
        _weakTextBrush = weakTextBrush;
        Chrome.Background = paperBrush;
        Chrome.BorderBrush = paperBorderBrush;
        Outline.BorderBrush = outlineBrush;
        Label.Foreground = weakTextBrush;
        Icon.Text = iconText;
        Icon.FontSize = iconFontSize;
        Icon.Foreground = iconBrush;
        CloseGlyph.Foreground = weakTextBrush;
    }

    private void ApplyFixedLayout(EdgeCapsuleEdge edge)
    {
        if (_appliedEdge == edge)
        {
            return;
        }
        if (_disposed)
        {
            return;
        }

        var options = _options;
        var leftEdge = edge == EdgeCapsuleEdge.Left;
        ApplyContentOrder(edge, options);
        var outlineMargin = options.WindowChromeMargin - options.OutlineThickness + options.OutlineOverlap;
        var bodyCorner = EdgeCorner(edge, options.ChromeCornerRadius);
        var outlineCorner = EdgeCorner(
            edge,
            options.ChromeCornerRadius + options.OutlineThickness - options.OutlineOverlap);

        Chrome.Margin = leftEdge
            ? new Thickness(0, options.WindowChromeMargin, options.WindowChromeMargin, options.WindowChromeMargin)
            : new Thickness(options.WindowChromeMargin, options.WindowChromeMargin, 0, options.WindowChromeMargin);
        Chrome.HorizontalAlignment = HorizontalAlignment.Stretch;
        Chrome.VerticalAlignment = VerticalAlignment.Top;
        Chrome.Width = double.NaN;
        Chrome.Height = options.BodyHeight;
        Chrome.CornerRadius = bodyCorner;
        Chrome.BorderThickness = leftEdge
            ? new Thickness(0, 1, 1, 1)
            : new Thickness(1, 1, 0, 1);

        Shell.Margin = leftEdge
            ? new Thickness(0, options.WindowChromeMargin, options.WindowChromeMargin, options.WindowChromeMargin)
            : new Thickness(options.WindowChromeMargin, options.WindowChromeMargin, 0, options.WindowChromeMargin);
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.Width = double.NaN;
        Shell.Height = options.BodyHeight;

        Outline.Margin = leftEdge
            ? new Thickness(0, outlineMargin, outlineMargin, outlineMargin)
            : new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
        Outline.HorizontalAlignment = HorizontalAlignment.Stretch;
        Outline.VerticalAlignment = VerticalAlignment.Top;
        Outline.Width = double.NaN;
        Outline.Height = Math.Max(0, options.BodyHeight + options.WindowChromeMargin * 2 - outlineMargin * 2);
        Outline.CornerRadius = outlineCorner;
        Outline.BorderThickness = leftEdge
            ? new Thickness(0, options.OutlineThickness, options.OutlineThickness, options.OutlineThickness)
            : new Thickness(options.OutlineThickness, options.OutlineThickness, 0, options.OutlineThickness);

        ApplySegmentCorners(edge, options.InnerCornerRadius);
        _appliedEdge = edge;
    }

    private void SetCloseWidth(
        double width,
        double maximumWidth,
        bool enableHitTest)
    {
        if (_disposed)
        {
            return;
        }

        width = Math.Clamp(width, 0, maximumWidth);
        _maximumCloseWidth = maximumWidth;
        CloseArea.Width = width;
        CloseArea.Opacity = maximumWidth <= 0 ? 0 : width / maximumWidth;
        CloseArea.IsHitTestVisible = enableHitTest && width >= maximumWidth - 0.5;
    }

    private void ApplyContentOrder(EdgeCapsuleEdge edge, EdgeCapsuleHostOptions options)
    {
        var leftEdge = edge == EdgeCapsuleEdge.Left;
        ContentArea.Cursor = Cursors.Hand;
        if (Shell.ColumnDefinitions.Count >= 2)
        {
            Shell.ColumnDefinitions[0].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            Shell.ColumnDefinitions[1].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
        }
        Grid.SetColumn(ContentArea, leftEdge ? 1 : 0);
        Grid.SetColumn(CloseArea, leftEdge ? 0 : 1);

        ContentGrid.Margin = leftEdge
            ? new Thickness(options.LeftPadding, 0, 0, 0)
            : new Thickness(0, 0, options.LeftPadding, 0);
        if (ContentGrid.ColumnDefinitions.Count >= 2)
        {
            ContentGrid.ColumnDefinitions[0].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            ContentGrid.ColumnDefinitions[1].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
        }
        Grid.SetColumn(Icon, leftEdge ? 0 : 1);
        Icon.HorizontalAlignment = leftEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Icon.TextAlignment = leftEdge ? TextAlignment.Left : TextAlignment.Right;
        Grid.SetColumn(Label, leftEdge ? 1 : 0);
        Label.Margin = leftEdge
            ? new Thickness(options.IconGap, 0, 0, 0)
            : new Thickness(0, 0, options.IconGap, 0);
        Label.TextAlignment = leftEdge ? TextAlignment.Left : TextAlignment.Right;
    }

    private void ApplySegmentCorners(EdgeCapsuleEdge edge, double radius)
    {
        if (_disposed)
        {
            return;
        }

        ContentArea.CornerRadius = edge == EdgeCapsuleEdge.Left
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);
        CloseArea.CornerRadius = new CornerRadius(0);
    }

    private static CornerRadius EdgeCorner(EdgeCapsuleEdge edge, double radius) =>
        edge == EdgeCapsuleEdge.Left
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Window.Content = null;
        Window.Close();
        _callbacks = null;
        _appliedEdge = null;
    }
}
