using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

/// <summary>Paint only: no HWND, input, layout or transition ownership. NativeMicaBackdrop
/// still decides when Background may be translucent; semantic editor colors stay opaque.</summary>
internal sealed class SkinBorder : Border
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(string), typeof(SkinBorder),
        new FrameworkPropertyMetadata(PaperSkins.Paper, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((SkinBorder)d).SyncReflectionSubscription()));
    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule), typeof(bool), typeof(SkinBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Skin { get => (string)GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public bool IsCapsule { get => (bool)GetValue(IsCapsuleProperty); set => SetValue(IsCapsuleProperty, value); }
    internal bool IsOutline { get; init; }
    private bool _dark, _highContrast, _animateReflection;
    private Window? _reflectionWindow;
    private int _reflectionBand;
    private (string Skin, bool Dark, bool Capsule, Color Paper, int Band)? _brushKey;
    private Brush _fill = Brushes.Transparent, _shine = Brushes.Transparent;
    private Pen _rim = new(Brushes.Transparent, 1);
    private static readonly Brush Fibers = CreateFibers();

    internal SkinBorder()
    {
        Loaded += (_, _) => SyncReflectionSubscription();
        Unloaded += (_, _) => DetachReflection();
        RefreshSkin();
    }
    internal void RefreshSkin()
    {
        _brushKey = null;
        _dark = Theme.IsDark;
        _highContrast = SystemParameters.HighContrast;
        _animateReflection = AppController.Current?.State.EnableAnimations == true;
        Skin = Theme.Skin;
        RenderOptions.SetEdgeMode(this, PaperSkins.Decorate(Skin, _highContrast) && Skin == PaperSkins.Pixel
            ? EdgeMode.Aliased : EdgeMode.Unspecified);
        SyncReflectionSubscription();
        InvalidateVisual();
    }
    internal static void Refresh(Border? border)
    {
        if (border is SkinBorder skin) skin.RefreshSkin();
    }
    private void SyncReflectionSubscription()
    {
        var window = !IsOutline && IsLoaded && Skin == PaperSkins.Pearl && !_highContrast && _animateReflection
            ? Window.GetWindow(this) : null;
        if (ReferenceEquals(window, _reflectionWindow)) return;
        DetachReflection();
        _reflectionWindow = window;
        if (window != null)
        {
            window.LocationChanged += OnReflectionLocationChanged;
            OnReflectionLocationChanged(window, EventArgs.Empty);
        }
        else _reflectionBand = 0;
    }
    private void DetachReflection()
    {
        if (_reflectionWindow != null) _reflectionWindow.LocationChanged -= OnReflectionLocationChanged;
        _reflectionWindow = null;
    }
    private void OnReflectionLocationChanged(object? sender, EventArgs e)
    {
        if (_reflectionWindow is not { IsVisible: true } window || !IsVisible ||
            !double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
        // Position driven and quantized: no idle timer, frame subscription or desktop capture.
        var band = (int)Math.Round(Math.Sin((window.Left + window.Top * .35) / 650) * 12);
        if (_reflectionBand == band) return;
        _reflectionBand = band;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (!PaperSkins.Decorate(Skin, _highContrast)) { base.OnRender(dc); return; }
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var pixel = Skin == PaperSkins.Pixel;
        var dpi = VisualTreeHelper.GetDpi(this);
        var shape = CreateShape(RenderSize, CornerRadius, 0, pixel, dpi);
        dc.PushClip(shape);
        if (IsOutline)
        {
            if (BorderBrush != null)
                dc.DrawGeometry(null, new Pen(BorderBrush, Math.Max(1, BorderThickness.Left) * 2), shape);
            dc.Pop(); return;
        }
        var paper = Background is SolidColorBrush solid ? solid.Color : ((SolidColorBrush)Theme.PaperBrush).Color;
        EnsureBrushes(paper);
        dc.DrawGeometry(_fill, null, shape);
        var bounds = new Rect(RenderSize);
        if (Skin == PaperSkins.TracingPaper) dc.DrawRectangle(Fibers, null, bounds);
        dc.DrawRectangle(_shine, null, bounds);
        if (pixel)
        {
            // Hard inset bevel does not add an Effect above the body glyphs.
            var sx = 2 / dpi.DpiScaleX; var sy = 2 / dpi.DpiScaleY;
            dc.DrawRectangle(Theme.TextBrush, null, new Rect(0, Math.Max(0, ActualHeight - sy * 2), ActualWidth, sy * 2));
            dc.DrawRectangle(Theme.TextBrush, null, new Rect(Math.Max(0, ActualWidth - sx * 2), 0, sx * 2, ActualHeight));
        }
        else if (Skin == PaperSkins.LiquidGlass)
        {
            dc.DrawGeometry(null, new Pen(_dark ? Brushes.Black : Theme.PaperBorderBrush, IsCapsule ? 5 : 4),
                CreateShape(RenderSize, CornerRadius, 3, false, dpi));
            dc.DrawGeometry(null, _rim, CreateShape(RenderSize, CornerRadius, 2, false, dpi));
        }
        else if (Skin == PaperSkins.Ceramic)
            dc.DrawGeometry(null, _rim, CreateShape(RenderSize, CornerRadius, 2, false, dpi));
        dc.DrawGeometry(null, pixel ? new Pen(Theme.TextBrush, 2 / dpi.DpiScaleX) : _rim,
            CreateShape(RenderSize, CornerRadius, .75, pixel, dpi));
        dc.Pop();
    }
    private void EnsureBrushes(Color background)
    {
        var key = (Skin, _dark, IsCapsule, background, _reflectionBand);
        if (_brushKey == key) return;
        _brushKey = key;
        var palette = ((SolidColorBrush)Theme.PaperBrush).Color;
        var opaque = !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;
        var alpha = opaque ? (byte)255 : Skin switch
        {
            PaperSkins.TracingPaper => (byte)204, PaperSkins.Aero => (byte)112, _ => (byte)132
        };
        var top = Mix(palette, Colors.White, _dark ? .055 : .4);
        var bottom = Mix(palette, Colors.Black, _dark ? .07 : .03);
        _fill = Gradient(0, WithAlpha(top, alpha), 1, WithAlpha(bottom, alpha));
        var bright = _dark ? (byte)44 : (byte)132;
        switch (Skin)
        {
            case PaperSkins.Pearl:
                _shine = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(_dark ? (byte)45 : (byte)88, 244, 157, 219), 0),
                    new(Color.FromArgb(24, 255, 255, 255), .22),
                    new(Color.FromArgb(_dark ? (byte)48 : (byte)85, 122, 218, 226), .48),
                    new(Color.FromArgb(_dark ? (byte)45 : (byte)78, 185, 147, 239), .76),
                    new(Color.FromArgb(30, 255, 218, 158), 1)
                }, new Point(-.15 + _reflectionBand / 40.0, 0), new Point(1.15 + _reflectionBand / 40.0, 1));
                break;
            case PaperSkins.Aero:
                _shine = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(bright, 255, 255, 255), 0),
                    new(Color.FromArgb(14, 255, 255, 255), .34),
                    new(Color.FromArgb(92, 255, 255, 255), .36),
                    new(Color.FromArgb(18, 255, 255, 255), .48),
                    new(Colors.Transparent, .50), new(Color.FromArgb(30, 160, 210, 250), 1)
                }, new Point(0, 0), new Point(1, .65));
                break;
            case PaperSkins.LiquidGlass:
                _shine = new RadialGradientBrush(
                    Color.FromArgb(IsCapsule ? (byte)185 : (byte)140, 255, 255, 255), Colors.Transparent)
                {
                    Center = new Point(.28, 0), GradientOrigin = new Point(.28, 0),
                    RadiusX = .85, RadiusY = IsCapsule ? .72 : .34
                };
                break;
            case PaperSkins.Ceramic:
                _shine = Gradient(0, Color.FromArgb(bright, 255, 255, 255), .42, Colors.Transparent);
                break;
            case PaperSkins.Pixel:
                _fill = Frozen(new SolidColorBrush(palette));
                _shine = Gradient(0, Color.FromArgb(32, 255, 255, 255), .08, Colors.Transparent);
                break;
            default: _shine = Brushes.Transparent; break;
        }
        if (_shine.CanFreeze) _shine.Freeze();
        var rimBrush = Gradient(0, Color.FromArgb(_dark ? (byte)120 : (byte)224, 255, 255, 255),
            1, Color.FromArgb(_dark ? (byte)135 : (byte)180, palette.R, palette.G, palette.B));
        _rim = Frozen(new Pen(rimBrush, IsCapsule && Skin == PaperSkins.LiquidGlass ? 2 : 1.5));
    }
    internal static Geometry CreateShape(Size size, CornerRadius corners, double inset, bool pixel, DpiScale dpi)
    {
        var rect = new Rect(inset, inset, Math.Max(0, size.Width - inset * 2), Math.Max(0, size.Height - inset * 2));
        if (pixel)
        {
            var left = Math.Ceiling(rect.Left * dpi.DpiScaleX) / dpi.DpiScaleX;
            var top = Math.Ceiling(rect.Top * dpi.DpiScaleY) / dpi.DpiScaleY;
            var right = Math.Floor(rect.Right * dpi.DpiScaleX) / dpi.DpiScaleX;
            var bottom = Math.Floor(rect.Bottom * dpi.DpiScaleY) / dpi.DpiScaleY;
            rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
        if (rect.Width <= 0 || rect.Height <= 0) return Geometry.Empty;
        var limit = Math.Min(rect.Width, rect.Height) / 2;
        double Radius(double r) => Math.Clamp(r - inset, 0, limit);
        var radii = new[] { Radius(corners.TopRight), Radius(corners.BottomRight), Radius(corners.BottomLeft), Radius(corners.TopLeft) };
        var centers = new[]
        {
            new Point(rect.Right - radii[0], rect.Top + radii[0]),
            new Point(rect.Right - radii[1], rect.Bottom - radii[1]),
            new Point(rect.Left + radii[2], rect.Bottom - radii[2]),
            new Point(rect.Left + radii[3], rect.Top + radii[3])
        };
        Point Snap(Point p) => pixel
            ? new Point(Math.Round(p.X * dpi.DpiScaleX) / dpi.DpiScaleX, Math.Round(p.Y * dpi.DpiScaleY) / dpi.DpiScaleY) : p;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Snap(new Point(rect.Left + radii[3], rect.Top)), true, true);
            for (var corner = 0; corner < 4; corner++)
            {
                var r = radii[corner]; var c = centers[corner];
                Point Rotate(double x, double y) => Snap(corner switch
                {
                    0 => new Point(c.X + x, c.Y + y), 1 => new Point(c.X - y, c.Y + x),
                    2 => new Point(c.X - x, c.Y - y), _ => new Point(c.X + y, c.Y - x)
                });
                ctx.LineTo(Rotate(0, -r), true, false);
                if (pixel && r > 0)
                {
                    ctx.LineTo(Rotate(r / 3, -r), true, false);
                    ctx.LineTo(Rotate(r / 3, -r * 2 / 3), true, false);
                    ctx.LineTo(Rotate(r * 2 / 3, -r * 2 / 3), true, false);
                    ctx.LineTo(Rotate(r * 2 / 3, -r / 3), true, false);
                    ctx.LineTo(Rotate(r, -r / 3), true, false);
                    ctx.LineTo(Rotate(r, 0), true, false);
                }
                else if (r > 0)
                    ctx.ArcTo(Rotate(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                else ctx.LineTo(Rotate(0, 0), true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }
    internal static DropShadowEffect CreateShadow(double blur, double depth, double opacity) => new()
    {
        BlurRadius = Theme.IsPixelSkin ? 0 : blur, ShadowDepth = Theme.IsPixelSkin ? 2 : depth, Opacity = opacity
    };
    private static Brush CreateFibers()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(13, 124, 110, 91)), .6);
            for (var i = 0; i < 14; i++)
            {
                var x = i * 19 % 61; var y = i * 37 % 59;
                dc.DrawLine(pen, new Point(x, y), new Point(x + 2 + i % 4, y + .7));
            }
        }
        return Frozen(new DrawingBrush(drawing)
        {
            Viewport = new Rect(0, 0, 64, 64), Viewbox = new Rect(0, 0, 64, 64),
            ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile, Stretch = Stretch.Fill
        });
    }
    private static Brush Gradient(double a, Color first, double b, Color last) => Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(first, a), new(last, b) }, new Point(0, 0), new Point(0, 1)));
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
