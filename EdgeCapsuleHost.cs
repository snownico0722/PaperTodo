using System.Diagnostics;
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
    FontWeight LabelFontWeight,
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
    Action PointerInvalidated,
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
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private readonly EdgeCapsuleHostOptions _options;
    private EdgeCapsuleHostCallbacks? _callbacks;
    private Brush _hoverBrush;
    private Brush _textBrush;
    private Brush _weakTextBrush;
    private double _maximumCloseWidth;
    private double _appliedCloseWidth;
    private EdgeCapsuleEdge? _appliedEdge;
    private EdgeCapsulePresentationFrame _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
    private int _nativeMetricsVersion;
    private int _appliedNativeMetricsVersion;
    private bool _disposed;
    private Window Window { get; }
    private Grid Root { get; }
    private Grid VisualSurface { get; }
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
        Grid visualSurface,
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
        VisualSurface = visualSurface;
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
                source.AddHook(OnNativeMessage);
                source.AddHook(hook);
            }
        };
        window.Deactivated += (_, _) => deactivated();
    }

    /// <summary>
    /// The only docked-surface effect entry. The native HWND owns stable expanded capacity while
    /// the real, wall-aligned visual surface follows frame.Bounds. Horizontal animation therefore
    /// changes only one WPF tree and never races a native resize.
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
            if (window.IsVisible)
            {
                window.Hide();
            }
            if (Math.Abs(window.Opacity - 1) > 0.001)
            {
                window.Opacity = 1;
            }
            if (Math.Abs(root.Opacity - 1) > 0.001)
            {
                root.Opacity = 1;
            }
            if (root.IsHitTestVisible)
            {
                root.IsHitTestVisible = false;
            }
            _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
            return true;
        }

        Debug.Assert(
            frame.Surface != EdgeCapsuleSurfaceKind.FloatingFree,
            "FloatingFree is rendered by EdgeCapsuleDragWindow, never the docked host.");
        Debug.Assert(
            frame.HostBounds.Width >= frame.Bounds.Width &&
            frame.HostBounds.Top == frame.Bounds.Top &&
            frame.HostBounds.Bottom == frame.Bounds.Bottom &&
            (frame.Edge == EdgeCapsuleEdge.Left
                ? frame.HostBounds.Left == frame.Bounds.Left
                : frame.HostBounds.Right == frame.Bounds.Right),
            "The visible capsule must fit inside a host pinned to the same wall.");
        var previousFrame = _appliedFrame;
        var nativeMetricsVersion = _nativeMetricsVersion;
        var nativeMetricsChanged = _appliedNativeMetricsVersion != nativeMetricsVersion;
        var firstShow = !window.IsVisible;
        var edgeChanged = firstShow ||
            !previousFrame.Visible ||
            previousFrame.Edge != frame.Edge;
        var visualSurfaceChanged = nativeMetricsChanged ||
            edgeChanged ||
            previousFrame.Bounds.Width != frame.Bounds.Width ||
            previousFrame.Bounds.Height != frame.Bounds.Height ||
            Math.Abs(previousFrame.DpiScaleX - frame.DpiScaleX) > 0.001 ||
            Math.Abs(previousFrame.DpiScaleY - frame.DpiScaleY) > 0.001;
        var segmentLayoutChanged = visualSurfaceChanged ||
            previousFrame.BodyWindowWidthDevice != frame.BodyWindowWidthDevice ||
            Math.Abs(previousFrame.MaximumCloseWidthDip - frame.MaximumCloseWidthDip) > 0.001;
        var nativeBoundsChanged = firstShow ||
            !previousFrame.Visible ||
            previousFrame.HostBounds != frame.HostBounds ||
            !WindowNative.TryGetWindowDeviceBounds(window, out var actualHostBounds) ||
            actualHostBounds != frame.HostBounds;

        if (firstShow)
        {
            window.Opacity = 0;
        }
        if (nativeBoundsChanged && !WindowNative.TrySetWindowDeviceBounds(window, frame.HostBounds))
        {
            return false;
        }

        // Move the HWND before changing edge-specific columns and corners. If the native move is
        // rejected or superseded by a per-monitor DPI hand-off, the old monitor must never display
        // a visual tree that has already been flipped for the destination edge.
        if (edgeChanged)
        {
            ApplyFixedLayout(frame.Edge);
        }
        if (visualSurfaceChanged)
        {
            ApplyVisualSurface(frame);
        }
        if (segmentLayoutChanged)
        {
            var closeWidth = EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
                frame.Bounds.Width,
                frame.BodyWindowWidthDevice,
                frame.DpiScaleX,
                frame.MaximumCloseWidthDip);
            ApplySegmentWidths(
                frame,
                closeWidth,
                frame.MaximumCloseWidthDip,
                frame.IsHitTestVisible);
        }
        else if (previousFrame.IsHitTestVisible != frame.IsHitTestVisible)
        {
            CloseArea.IsHitTestVisible = frame.IsHitTestVisible &&
                _maximumCloseWidth > 0 &&
                _appliedCloseWidth >= _maximumCloseWidth - 0.5;
        }

        _appliedFrame = frame;
        if (firstShow)
        {
            window.Show();
            if (!WindowNative.TrySetWindowDeviceBounds(window, frame.HostBounds))
            {
                // Show succeeded but the post-Show placement did not. Hide immediately so the
                // half-committed edge layout never stays on a wrong HWND; the next apply treats
                // this as a fresh firstShow and retries the full path.
                window.Hide();
                window.Opacity = 0;
                _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
                return false;
            }
        }

        var contentOpacity = Math.Clamp(frame.ContentOpacity, 0, 1);
        if (Math.Abs(root.Opacity - contentOpacity) > 0.001)
        {
            root.Opacity = contentOpacity;
        }
        if (root.IsHitTestVisible != frame.IsHitTestVisible)
        {
            root.IsHitTestVisible = frame.IsHitTestVisible;
        }
        var outlineVisibility = frame.OutlineVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (Outline.Visibility != outlineVisibility)
        {
            Outline.Visibility = outlineVisibility;
        }
        var opacity = Math.Clamp(frame.Opacity, 0, 1);
        if (Math.Abs(window.Opacity - opacity) > 0.001)
        {
            window.Opacity = opacity;
        }
        _appliedNativeMetricsVersion = nativeMetricsVersion;
        return true;
    }

    public void InvalidateNativeMetrics()
    {
        if (_disposed)
        {
            return;
        }

        unchecked
        {
            _nativeMetricsVersion++;
        }
    }

    private bool ContainsScreenPoint(DeviceScreenPoint point)
    {
        if (_disposed || !Window.IsVisible || !_appliedFrame.IsHitTestVisible)
        {
            return false;
        }

        // The frame carries the physical body/close rectangle and excludes both the transparent
        // host reserve and the shadow margin. Pointer intent never uses the larger HWND rectangle.
        return EdgeCapsuleGeometry.Contains(_appliedFrame.InteractiveBounds, point);
    }

    private IntPtr OnNativeMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var point = new DeviceScreenPoint(
            unchecked((short)(packed & 0xffff)),
            unchecked((short)((packed >> 16) & 0xffff)));
        if (ContainsScreenPoint(point))
        {
            return IntPtr.Zero;
        }

        // The fixed host reserves the fully expanded rectangle. Pixels outside the current real
        // capsule are only a transparent composition canvas and must behave as if no HWND exists.
        handled = true;
        return HtTransparent;
    }

    public static EdgeCapsuleHost Create(EdgeCapsuleHostOptions options)
    {
        var root = new Grid
        {
            Background = null,
            ClipToBounds = false,
            Opacity = 1
        };
        var visualSurface = new Grid
        {
            Background = null,
            ClipToBounds = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        root.Children.Add(visualSurface);
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
        visualSurface.Children.Add(chrome);

        var shell = new Grid
        {
            Width = double.NaN,
            Height = options.BodyHeight,
            Margin = new Thickness(options.WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

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
            FontWeight = options.LabelFontWeight,
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
            FontSize = AppTypography.Scale(18),
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
        visualSurface.Children.Add(shell);

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
        visualSurface.Children.Add(outline);

        var window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            FontFamily = options.UiFontFamily,
            Language = options.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = options.Topmost,
            Content = root
        };
        AppTypography.ApplyTextRendering(window);
        AppTypography.ApplyTextRendering(label);

        return new EdgeCapsuleHost(
            options,
            window,
            root,
            visualSurface,
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
        shell.MouseEnter += (_, _) => callbacks.PointerInvalidated();
        shell.MouseLeave += (_, _) => callbacks.PointerInvalidated();
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
            close.Opacity = Math.Clamp(_appliedCloseWidth / Math.Max(1, _maximumCloseWidth), 0, 1);
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
        System.Windows.Markup.XmlLanguage language,
        double iconFontSize,
        double labelFontSize,
        FontWeight labelFontWeight,
        double closeGlyphFontSize)
    {
        if (_disposed)
        {
            return;
        }
        Window.FontFamily = uiFontFamily;
        Window.Language = language;
        AppTypography.ApplyTextRendering(Window);
        AppTypography.ApplyTextRendering(Label);
        Icon.FontFamily = symbolFontFamily;
        Icon.FontSize = iconFontSize;
        Label.FontFamily = uiFontFamily;
        Label.FontSize = labelFontSize;
        Label.FontWeight = labelFontWeight;
        CloseGlyph.FontSize = closeGlyphFontSize;
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
        if (_disposed)
        {
            return default;
        }
        if (_appliedFrame.Visible && !_appliedFrame.Bounds.IsEmpty)
        {
            return new DeviceScreenPoint(
                _appliedFrame.Bounds.Left,
                _appliedFrame.Bounds.Top);
        }
        return DeviceScreenPoint.FromPoint(Window.PointToScreen(new Point(0, 0)));
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

    private void ApplyVisualSurface(EdgeCapsulePresentationFrame frame)
    {
        var surface = VisualSurface;
        surface.HorizontalAlignment = frame.Edge == EdgeCapsuleEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        surface.Width = frame.Bounds.Width / Math.Max(1, frame.DpiScaleX);
        surface.Height = frame.Bounds.Height / Math.Max(1, frame.DpiScaleY);
    }

    private void ApplySegmentWidths(
        EdgeCapsulePresentationFrame frame,
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
        _appliedCloseWidth = width;
        var bodyWindowWidthDip = frame.BodyWindowWidthDevice / Math.Max(1, frame.DpiScaleX);
        var contentWidth = Math.Max(0, bodyWindowWidthDip - _options.WindowChromeMargin);
        if (frame.Edge == EdgeCapsuleEdge.Left)
        {
            Shell.ColumnDefinitions[0].Width = new GridLength(width);
            Shell.ColumnDefinitions[1].Width = new GridLength(contentWidth);
        }
        else
        {
            Shell.ColumnDefinitions[0].Width = new GridLength(contentWidth);
            Shell.ColumnDefinitions[1].Width = new GridLength(width);
        }
        CloseArea.Width = double.NaN;
        CloseArea.Opacity = maximumWidth <= 0 ? 0 : width / maximumWidth;
        CloseArea.IsHitTestVisible =
            enableHitTest && maximumWidth > 0 && width >= maximumWidth - 0.5;
    }

    private void ApplyContentOrder(EdgeCapsuleEdge edge, EdgeCapsuleHostOptions options)
    {
        var leftEdge = edge == EdgeCapsuleEdge.Left;
        ContentArea.Cursor = Cursors.Hand;
        Grid.SetColumn(ContentArea, leftEdge ? 1 : 0);
        Grid.SetColumn(CloseArea, leftEdge ? 0 : 1);

        ContentGrid.Margin = leftEdge
            ? new Thickness(0, 0, options.LeftPadding, 0)
            : new Thickness(options.LeftPadding, 0, 0, 0);
        if (ContentGrid.ColumnDefinitions.Count >= 2)
        {
            ContentGrid.ColumnDefinitions[0].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            ContentGrid.ColumnDefinitions[1].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
        }
        Grid.SetColumn(Icon, leftEdge ? 1 : 0);
        Icon.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Icon.TextAlignment = leftEdge ? TextAlignment.Right : TextAlignment.Left;
        Grid.SetColumn(Label, leftEdge ? 0 : 1);
        Label.Margin = leftEdge
            ? new Thickness(0, 0, options.IconGap, 0)
            : new Thickness(options.IconGap, 0, 0, 0);
        Label.TextAlignment = leftEdge ? TextAlignment.Right : TextAlignment.Left;
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
