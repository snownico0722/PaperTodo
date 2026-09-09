using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

/// <summary>Surface paint only. The existing host still owns layout, input, shape transitions
/// and the native/solid Background decision. Never replace its focus border with a skin rim.</summary>
internal sealed partial class SkinBorder : Border
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(string), typeof(SkinBorder),
        new FrameworkPropertyMetadata(PaperSkins.Paper, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((SkinBorder)d).SyncLensLight()));
    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule), typeof(bool), typeof(SkinBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Skin { get => (string)GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public bool IsCapsule { get => (bool)GetValue(IsCapsuleProperty); set => SetValue(IsCapsuleProperty, value); }
    internal bool IsOutline { get; init; }
    private bool _dark, _highContrast, _animateReflection;
    private (string Skin, bool Dark, bool Capsule, Color Paper)? _brushKey;
    private Brush _fill = Brushes.Transparent, _shine = Brushes.Transparent;
    private Brush _glint = Brushes.Transparent;
    private (Size Size, CornerRadius Corners, Thickness Border, bool Pixel, bool Capsule, double X, double Y)? _geometryKey;
    private Geometry _shape = Geometry.Empty, _borderRing = Geometry.Empty;
    private Geometry _glintRing = Geometry.Empty;
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight), typeof(double), typeof(SkinBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double HeaderHeight { get => (double)GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }
    private static readonly Brush LightFibers = CreateFibers(false);
    private static readonly Brush DarkFibers = CreateFibers(true);

    internal SkinBorder()
    {
        Loaded += (_, _) => SyncLensLight();
        Unloaded += (_, _) => DetachLensLight();
        IsVisibleChanged += (_, _) => SyncLensLight();
        RefreshSkin();
    }

    internal void RefreshSkin()
    {
        _brushKey = null;
        _geometryKey = null;
        _dark = Theme.IsDark;
        _highContrast = SystemParameters.HighContrast;
        _animateReflection = AppController.Current?.State.EnableAnimations == true;
        Skin = Theme.Skin;
        RenderOptions.SetEdgeMode(this, PaperSkins.Decorate(Skin, _highContrast) && Skin == PaperSkins.Pixel
            ? EdgeMode.Aliased : EdgeMode.Unspecified);
        SyncLensLight();
        InvalidateVisual();
    }
    internal static void Refresh(Border? border)
    {
        if (border is SkinBorder skin) skin.RefreshSkin();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // In particular, original Mica and Acrylic must go through the original Border
        // renderer without any decorative fill, alpha wash, glint or texture.
        if (!PaperSkins.Decorate(Skin, _highContrast)) { base.OnRender(dc); return; }
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureGeometry();
        if (IsOutline)
        {
            dc.DrawGeometry(BorderBrush, null, _borderRing);
            return;
        }
        var background = Background is SolidColorBrush solid
            ? solid.Color : ((SolidColorBrush)Theme.PaperBrush).Color;
        EnsureBrushes(background);
        dc.PushClip(_shape);
        dc.DrawGeometry(_fill, null, _shape);
        if (Skin == PaperSkins.TracingPaper)
            dc.DrawRectangle(_dark ? DarkFibers : LightFibers, null, new Rect(RenderSize));
        dc.DrawRectangle(_shine, null, new Rect(RenderSize));
        PaintMaterialDetails(dc);
        // The owner's stroke wins. Transparent/zero-width borders really disappear, and
        // left/right docked open edges stay open instead of acquiring a white seam.
        dc.DrawGeometry(BorderBrush, null, _borderRing);
        dc.Pop();
    }

    private void EnsureGeometry()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixel = Skin == PaperSkins.Pixel;
        var key = (RenderSize, CornerRadius, BorderThickness, pixel, IsCapsule, dpi.DpiScaleX, dpi.DpiScaleY);
        if (_geometryKey == key) return;
        _geometryKey = key;
        _shape = CreateShape(RenderSize, CornerRadius, 0, pixel, dpi);
        _borderRing = Ring(new Thickness(), BorderThickness);
        // Only a hairline optical reflection, never the old nested 3-9 DIP bezel.
        var unit = 1 / dpi.DpiScaleX;
        _glintRing = Ring(Sides(unit), Sides(unit));

        Thickness Sides(double width) => new(
            BorderThickness.Left > 0 ? width : 0, BorderThickness.Top > 0 ? width : 0,
            BorderThickness.Right > 0 ? width : 0, BorderThickness.Bottom > 0 ? width : 0);
        Geometry Ring(Thickness inset, Thickness width)
        {
            if (width == new Thickness()) return Geometry.Empty;
            var outside = Inset(inset);
            var inside = Inset(new Thickness(inset.Left + width.Left, inset.Top + width.Top,
                inset.Right + width.Right, inset.Bottom + width.Bottom));
            return Frozen(new CombinedGeometry(GeometryCombineMode.Exclude, outside, inside));
        }
        Geometry Inset(Thickness inset)
        {
            if (pixel)
                inset = new Thickness(
                    Math.Ceiling(inset.Left * dpi.DpiScaleX) / dpi.DpiScaleX,
                    Math.Ceiling(inset.Top * dpi.DpiScaleY) / dpi.DpiScaleY,
                    Math.Ceiling(inset.Right * dpi.DpiScaleX) / dpi.DpiScaleX,
                    Math.Ceiling(inset.Bottom * dpi.DpiScaleY) / dpi.DpiScaleY);
            var size = new Size(Math.Max(0, ActualWidth - inset.Left - inset.Right),
                Math.Max(0, ActualHeight - inset.Top - inset.Bottom));
            var corners = new CornerRadius(
                Math.Max(0, CornerRadius.TopLeft - Math.Max(inset.Left, inset.Top)),
                Math.Max(0, CornerRadius.TopRight - Math.Max(inset.Right, inset.Top)),
                Math.Max(0, CornerRadius.BottomRight - Math.Max(inset.Right, inset.Bottom)),
                Math.Max(0, CornerRadius.BottomLeft - Math.Max(inset.Left, inset.Bottom)));
            var geometry = CreateShape(size, corners, 0, pixel, dpi);
            return Frozen(new GeometryGroup
            {
                Children = new GeometryCollection { geometry },
                Transform = new TranslateTransform(inset.Left, inset.Top)
            });
        }
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
        // A small three-step chamfer, not a coarse staircase around a whole semicircle.
        double Radius(double r) => Math.Clamp((pixel ? Math.Min(r, 6) : r) - inset, 0, limit);
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
    private static Brush CreateFibers(bool dark)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var pen = new Pen(new SolidColorBrush(dark
                ? Color.FromArgb(12, 245, 239, 223) : Color.FromArgb(14, 130, 120, 102)), .45);
            for (var i = 0; i < 36; i++)
            {
                var x = i * 31 % 109; var y = i * 47 % 107;
                dc.DrawLine(pen, new Point(x, y), new Point(x + 2 + i % 4, y + .5));
            }
        }
        return Frozen(new DrawingBrush(drawing)
        {
            Viewport = new Rect(0, 0, 112, 112), Viewbox = new Rect(0, 0, 112, 112),
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
