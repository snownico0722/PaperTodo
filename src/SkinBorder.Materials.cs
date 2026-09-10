using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Brush _header = Brushes.Transparent;
    private readonly TranslateTransform _reflectionShift = new();
    private readonly LinearGradientBrush _aeroReflection = new()
    {
        MappingMode = BrushMappingMode.Absolute,
        StartPoint = new Point(-160, -120), EndPoint = new Point(700, 420),
        SpreadMethod = GradientSpreadMethod.Reflect
    };
    private readonly RadialGradientBrush _lensLight = new(Colors.White, Colors.Transparent)
    {
        Center = new Point(.24, .05), GradientOrigin = new Point(.24, .05), RadiusX = .65, RadiusY = .65
    };
    private DrawingGroup? _relief;
    private (Size Size, CornerRadius Radius, Thickness Border, double DpiX, double DpiY, string Skin, bool Dark)? _reliefKey;

    private void EnsureBrushes(Color background)
    {
        var key = (Skin, _dark, IsCapsule, background);
        if (_brushKey == key) return;
        _brushKey = key;
        var paper = ((SolidColorBrush)Theme.PaperBrush).Color;
        var opaque = !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;
        byte alpha = opaque ? (byte)255 : (byte)(_dark ? 226 : 211);
        _fill = Frozen(new SolidColorBrush(WithAlpha(paper, alpha)));
        _shine = _glint = _header = Brushes.Transparent;
        switch (Skin)
        {
            case PaperSkins.Aero:
                // Transparent blue glass, not system Acrylic underneath a pale paint layer.
                // Reflections have world-space scale/parallax; they are not resized with
                // the paper into enormous diagonal white ribbons.
                var glass = Mix(paper, _dark ? Color.FromRgb(25, 53, 73) : Color.FromRgb(111, 171, 205), .70);
                var top = Mix(glass, Colors.White, _dark ? .035 : .19);
                var low = Mix(glass, _dark ? Colors.Black : Color.FromRgb(48, 94, 121), .17);
                byte a = opaque ? (byte)255 : (byte)(_dark ? 124 : 76);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(top, a), 0), new(WithAlpha(glass, a), .18),
                    new(WithAlpha(Mix(glass, paper, .24), a), .74), new(WithAlpha(low, a), 1)
                }, new Point(0, 0), new Point(0, 1)));
                _aeroReflection.GradientStops = new GradientStopCollection
                {
                    new(White(0), 0), new(White(_dark ? 3 : 8), .08),
                    new(White(_dark ? 22 : 68), .18), new(White(_dark ? 30 : 88), .25),
                    new(White(_dark ? 8 : 24), .39), new(White(0), .49),
                    new(White(0), .67), new(White(_dark ? 12 : 32), .79),
                    new(White(_dark ? 4 : 12), .89), new(White(0), 1)
                };
                _aeroReflection.Transform = _reflectionShift;
                _shine = _aeroReflection;
                break;
            case PaperSkins.LiquidGlass:
                var lens = LiquidTint;
                var clear = Color.FromRgb((byte)Math.Round(lens.X * 255), (byte)Math.Round(lens.Y * 255), (byte)Math.Round(lens.Z * 255));
                _fill = Frozen(new SolidColorBrush(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Round(lens.W * 255))));
                // A direct-alpha center, no stale captured center or faux milk gradient.
                // Actual curved-edge reflectance is part of the same refraction shader.
                _glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 92 : 170), 0), new(White(18), .25),
                    new(White(0), .55), new(White(_dark ? 36 : 78), 1)
                }, new Point(0, 0), new Point(1, 1)));
                _lensLight.GradientStops[0].Color = White(_dark ? 70 : 110);
                break;
            case PaperSkins.Ceramic:
                // Separate opaque diffuse body and clear-coat specular. Most of the body
                // has a stable ivory tone; a finite softbox reflection has a visible edge.
                // Avoid the old top-to-bottom grey wash that only looked like dirty paper.
                var body = Mix(paper, _dark ? Color.FromRgb(39, 43, 49) : Color.FromRgb(241, 229, 207), .78);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(body, Colors.White, _dark ? .016 : .035), 0),
                    new(body, .30), new(body, .84),
                    new(Mix(body, _dark ? Colors.Black : Color.FromRgb(219, 214, 205), .07), 1)
                }, new Point(0, 0), new Point(.06, 1)));
                var glaze = new DrawingGroup();
                using (var dc = glaze.Open())
                {
                    var softbox = Frozen(new RadialGradientBrush(new GradientStopCollection
                    {
                        new(White(_dark ? 45 : 218), 0), new(White(_dark ? 41 : 204), .42),
                        new(White(_dark ? 23 : 120), .67), new(White(0), 1)
                    }) { Center = new Point(.25, IsCapsule ? .22 : .055),
                        GradientOrigin = new Point(.20, IsCapsule ? .18 : .045),
                        RadiusX = .71, RadiusY = IsCapsule ? .54 : .21 });
                    dc.DrawRectangle(softbox, null, new Rect(0, 0, 1, 1));
                    var bounce = Frozen(new RadialGradientBrush(White(_dark ? 10 : 30), Colors.Transparent)
                    { Center = new Point(.95, .82), GradientOrigin = new Point(.95, .82), RadiusX = .48, RadiusY = .5 });
                    dc.DrawRectangle(bounce, null, new Rect(0, 0, 1, 1));
                }
                _shine = Frozen(new DrawingBrush(glaze) { Stretch = Stretch.Fill });
                break;
            case PaperSkins.Pixel:
                var retro = Mix(paper, _dark ? Color.FromRgb(22, 29, 46) : Color.FromRgb(240, 235, 217), .42);
                _fill = Frozen(new SolidColorBrush(retro));
                _header = Frozen(new SolidColorBrush(Mix(retro, ((SolidColorBrush)Theme.ActiveBrush).Color, _dark ? .12 : .16)));
                break;
            case PaperSkins.TracingPaper:
                var tracing = Mix(paper, _dark ? Color.FromRgb(29, 30, 32) : Colors.White, .50);
                _fill = Gradient(0, WithAlpha(tracing, alpha), 1, WithAlpha(Mix(tracing, paper, .10), alpha));
                break;
        }
    }

    private void PaintMaterialDetails(DrawingContext dc)
    {
        if (Skin is PaperSkins.Aero or PaperSkins.Ceramic || Skin == PaperSkins.LiquidGlass && _refractionVisual == null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var key = (RenderSize, CornerRadius, BorderThickness, dpi.DpiScaleX, dpi.DpiScaleY, Skin, _dark);
            if (_reliefKey != key)
            {
                _relief = MaterialRelief.Create(RenderSize, CornerRadius, BorderThickness, dpi, Skin, _dark);
                _reliefKey = key;
            }
            dc.DrawDrawing(_relief);
        }
        if (Skin == PaperSkins.LiquidGlass && _refractionVisual == null)
        {
            dc.DrawGeometry(_glint, null, _glintRing);
            dc.DrawGeometry(_lensLight, null, _glintRing);
        }
        if (Skin == PaperSkins.Pixel && !IsCapsule && HeaderHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bottom = Math.Min(ActualHeight, Math.Round((HeaderHeight + BorderThickness.Top) * dpi.DpiScaleY) / dpi.DpiScaleY);
            if (bottom > 0)
            {
                dc.DrawRectangle(_header, null, new Rect(0, 0, ActualWidth, bottom));
                dc.DrawRectangle(Theme.PaperBorderBrush, null, new Rect(0, bottom, ActualWidth, 1 / dpi.DpiScaleY));
            }
        }
    }
    private static Color White(int alpha) => Color.FromArgb((byte)alpha, 255, 255, 255);
}
