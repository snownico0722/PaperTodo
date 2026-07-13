using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

internal sealed record EdgeCapsuleDragWindowOptions
{
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double BodyHeight { get; init; }
    public required double BodyRadius { get; init; }
    public required double WindowChromeMargin { get; init; }
    public required double OutlineMargin { get; init; }
    public required double OutlineThickness { get; init; }
    public required double OutlineOverlap { get; init; }
    public required double CloseWidth { get; init; }
    public required double LeftPadding { get; init; }
    public required double IconGap { get; init; }
    public required double RightPadding { get; init; }
    public required string Icon { get; init; }
    public required string Label { get; init; }
    public required double IconFontSize { get; init; }
    public required double LabelFontSize { get; init; }
    public required FontFamily UiFontFamily { get; init; }
    public required FontFamily SymbolFontFamily { get; init; }
    public required XmlLanguage Language { get; init; }
    public required Brush PaperBrush { get; init; }
    public required Brush PaperBorderBrush { get; init; }
    public required Brush IconBrush { get; init; }
    public required Brush LabelBrush { get; init; }
    public required Brush OutlineBrush { get; init; }
    public required bool ShowOutline { get; init; }
    public required bool Topmost { get; init; }
}

// A detached capsule is a complete, real-size pill in its own HWND. It never reuses the docked
// one-sided tag or any of its edge-specific columns, margins, corners, or width animation state.
internal sealed class EdgeCapsuleDragWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private readonly ScaleTransform _entranceScale = new(1, 1);
    private readonly double _widthDip;
    private readonly double _heightDip;
    private DeviceScreenPoint _lastPointer;
    private int _dpiSettleGeneration;
    private bool _closingByOwner;
    private bool _isClosed;

    public EdgeCapsuleDragWindow(EdgeCapsuleDragWindowOptions options)
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = options.UiFontFamily;
        Language = options.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Topmost = options.Topmost;
        Opacity = 0;
        _widthDip = options.Width;
        _heightDip = options.Height;
        Content = BuildContent(options);

        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }
        };
    }

    public event EventHandler? UnexpectedlyClosed;

    public void ShowWithEntrance(
        DeviceScreenPoint pointer,
        bool animate,
        double scaleFrom,
        int durationMilliseconds)
    {
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate)
        {
            _entranceScale.ScaleX = 1;
            _entranceScale.ScaleY = 1;
            MoveCenteredAt(pointer);
            Show();
            MoveCenteredAt(pointer);
            Opacity = 1;
            return;
        }

        _entranceScale.ScaleX = scaleFrom;
        _entranceScale.ScaleY = scaleFrom;
        MoveCenteredAt(pointer);
        Show();
        MoveCenteredAt(pointer);
        Opacity = 1;
        var animation = new DoubleAnimation
        {
            From = scaleFrom,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void MoveCenteredAt(DeviceScreenPoint pointer)
    {
        _lastPointer = pointer;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, this, out var geometry))
        {
            return;
        }

        var width = Math.Max(1, (int)Math.Round(_widthDip * geometry.DpiScaleX, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(_heightDip * geometry.DpiScaleY, MidpointRounding.AwayFromZero));
        var left = (int)Math.Round(pointer.X - width / 2.0, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(pointer.Y - height / 2.0, MidpointRounding.AwayFromZero);
        WindowNative.TrySetWindowDeviceBounds(
            this,
            new DeviceScreenRect(left, top, left + width, top + height));
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            var generation = ++_dpiSettleGeneration;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!_isClosed && generation == _dpiSettleGeneration)
                    {
                        MoveCenteredAt(_lastPointer);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    public void CloseFromOwner()
    {
        if (_closingByOwner)
        {
            return;
        }

        _closingByOwner = true;
        Content = null;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _dpiSettleGeneration++;
        base.OnClosed(e);
        if (!_closingByOwner)
        {
            UnexpectedlyClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private Grid BuildContent(EdgeCapsuleDragWindowOptions options)
    {
        var root = new Grid
        {
            Background = null,
            IsHitTestVisible = false,
            RenderTransform = _entranceScale,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        root.Children.Add(new Border
        {
            Margin = new Thickness(options.WindowChromeMargin),
            Background = options.PaperBrush,
            BorderBrush = options.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(options.BodyRadius),
            SnapsToDevicePixels = true
        });

        var shell = new Grid
        {
            Margin = new Thickness(options.WindowChromeMargin),
            Height = options.BodyHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new Grid
        {
            Margin = new Thickness(options.LeftPadding, 0, options.RightPadding, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = options.Icon,
            Foreground = options.IconBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = options.IconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var label = new TextBlock
        {
            Text = options.Label,
            Foreground = options.LabelBrush,
            FontFamily = options.UiFontFamily,
            FontSize = options.LabelFontSize,
            Margin = new Thickness(options.IconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var contentArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(options.BodyRadius, 0, 0, options.BodyRadius),
            Child = content
        };
        Grid.SetColumn(contentArea, 0);
        shell.Children.Add(contentArea);

        var closeArea = new Border
        {
            Width = options.CloseWidth,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0, options.BodyRadius, options.BodyRadius, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "×",
                Foreground = options.LabelBrush,
                FontFamily = options.SymbolFontFamily,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(closeArea, 1);
        shell.Children.Add(closeArea);
        Panel.SetZIndex(shell, 10);
        root.Children.Add(shell);

        var outline = new Border
        {
            Margin = new Thickness(options.OutlineMargin),
            BorderBrush = options.OutlineBrush,
            BorderThickness = new Thickness(options.OutlineThickness),
            CornerRadius = new CornerRadius(options.BodyRadius + options.OutlineThickness - options.OutlineOverlap),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = options.ShowOutline ? Visibility.Visible : Visibility.Collapsed,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(outline, 20);
        root.Children.Add(outline);
        return root;
    }
}
