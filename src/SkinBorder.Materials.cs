using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Brush _bevel = Brushes.Transparent, _inner = Brushes.Transparent, _header = Brushes.Transparent;
    // Mutable brushes are retained by WPF's drawing. Moving their transforms does not
    // reconstruct a surface, remeasure text or allocate a new brush/geometry every frame.
    private readonly RadialGradientBrush _lensLight = new(Colors.White, Colors.Transparent)
    {
        Center = new Point(.24, .05), GradientOrigin = new Point(.24, .05), RadiusX = .65, RadiusY = .65
    };

    private void EnsureBrushes(Color background)
    {
        var key = (Skin, _dark, IsCapsule, background);
        if (_brushKey == key) return;
        _brushKey = key;
        var paper = ((SolidColorBrush)Theme.PaperBrush).Color;
        var opaque = !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;
        // This is the ONLY tint wash above the compositor. No opaque editor/heading plate.
        byte alpha = opaque ? (byte)255 : Skin switch
        {
            PaperSkins.TracingPaper => (byte)(_dark ? 226 : 211),
            PaperSkins.Aero => (byte)(_dark ? 204 : 156),
            _ => (byte)(_dark ? 208 : 168)
        };
        _fill = Frozen(new SolidColorBrush(WithAlpha(paper, alpha)));
        _shine = _bevel = _inner = _header = Brushes.Transparent;
        _glint = Gradient(0, White(_dark ? 92 : 212), 1, White(_dark ? 16 : 48));
        _depth = Gradient(0, Colors.Transparent, 1, Black(_dark ? 60 : 30));
        switch (Skin)
        {
            case PaperSkins.Pearl:
                _fill = Frozen(new SolidColorBrush(Mix(paper, _dark ? Color.FromRgb(29, 28, 41) : Colors.White, .30)));
                // A continuous spectral film with a narrow white reflection, not a nearly
                // invisible pink overlay. The transform is intentionally never frozen.
                _shine = new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(Color.FromRgb(242, 147, 204), _dark ? (byte)56 : (byte)102), 0),
                    new(WithAlpha(Color.FromRgb(183, 159, 242), _dark ? (byte)48 : (byte)86), .24),
                    new(WithAlpha(Color.FromRgb(97, 211, 217), _dark ? (byte)48 : (byte)96), .46),
                    new(White(_dark ? 48 : 160), .53),
                    new(WithAlpha(Color.FromRgb(224, 198, 132), _dark ? (byte)40 : (byte)80), .61),
                    new(WithAlpha(Color.FromRgb(236, 157, 206), _dark ? (byte)52 : (byte)92), .82),
                    new(WithAlpha(Color.FromRgb(157, 200, 239), _dark ? (byte)44 : (byte)88), 1)
                }, new Point(-.2, 0), new Point(1.2, .8))
                { SpreadMethod = GradientSpreadMethod.Reflect, RelativeTransform = _filmTransform };
                _bevel = Gradient(0, White(_dark ? 60 : 164), 1, Black(20));
                break;
            case PaperSkins.Aero:
                var glass = Mix(paper, _dark ? Color.FromRgb(25, 45, 62) : Color.FromRgb(185, 220, 243), .42);
                _fill = Gradient(0, WithAlpha(Mix(glass, Colors.White, _dark ? .025 : .18), alpha),
                    1, WithAlpha(glass, alpha));
                _shine = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 18 : 42), 0), new(White(_dark ? 18 : 42), .22),
                    new(White(_dark ? 4 : 9), .225), new(Colors.Transparent, .34),
                    new(White(_dark ? 32 : 110), .36), new(White(_dark ? 20 : 56), .42),
                    new(White(_dark ? 3 : 6), .425), new(Colors.Transparent, .57),
                    new(White(_dark ? 12 : 38), .59), new(Colors.Transparent, .68),
                    new(White(_dark ? 10 : 30), 1)
                }, new Point(0, 0), new Point(1, .5)));
                _bevel = Gradient(0, White(_dark ? 56 : 168), 1, Black(_dark ? 70 : 40));
                _inner = Gradient(0, Black(40), 1, White(_dark ? 78 : 220));
                break;
            case PaperSkins.LiquidGlass:
                var clear = Mix(paper, _dark ? Color.FromRgb(24, 30, 40) : Colors.White, .68);
                _fill = Gradient(0, WithAlpha(clear, alpha), 1,
                    WithAlpha(Mix(clear, _dark ? Colors.Black : Color.FromRgb(224, 235, 241), .10), alpha));
                _shine = Frozen(new RadialGradientBrush(White(_dark ? 8 : 36), Colors.Transparent)
                { Center = new Point(.28, 0), GradientOrigin = new Point(.28, 0), RadiusX = .85, RadiusY = .48 });
                // A broad convex rim, opposing light/dark arcs and an inner caustic line.
                // This models lens thickness; it does not claim to refract other windows.
                _bevel = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 132 : 242), 0), new(White(_dark ? 36 : 100), .19),
                    new(Black(_dark ? 110 : 72), .44), new(Colors.Transparent, .60),
                    new(WithAlpha(Color.FromRgb(143, 211, 255), 72), .80), new(White(210), 1)
                }, new Point(0, 0), new Point(1, 1)));
                _inner = Gradient(0, Black(_dark ? 96 : 60), 1, White(_dark ? 150 : 238));
                _glint = Gradient(0, White(242), .8, White(_dark ? 60 : 118));
                _lensLight.GradientStops[0].Color = White(_dark ? 176 : 242);
                break;
            case PaperSkins.Ceramic:
                var glaze = Mix(paper, _dark ? Color.FromRgb(38, 42, 47) : Color.FromRgb(255, 254, 247), .64);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(glaze, Colors.White, _dark ? .07 : .30), 0), new(glaze, .16),
                    new(Mix(glaze, paper, .22), .70), new(Mix(glaze, Colors.Black, _dark ? .12 : .09), 1)
                }, new Point(0, 0), new Point(.16, 1)));
                _shine = Frozen(new RadialGradientBrush(White(_dark ? 14 : 100), Colors.Transparent)
                { Center = new Point(.2, -.1), GradientOrigin = new Point(.2, -.1), RadiusX = 1.1, RadiusY = .44 });
                _bevel = Gradient(0, White(_dark ? 90 : 232), 1, Black(_dark ? 130 : 74));
                _inner = Gradient(0, Black(_dark ? 30 : 12), 1, White(_dark ? 50 : 130));
                break;
            case PaperSkins.Pixel:
                var retro = Mix(paper, _dark ? Color.FromRgb(22, 29, 46) : Color.FromRgb(240, 235, 217), .42);
                _fill = Frozen(new SolidColorBrush(retro));
                _bevel = Theme.ActiveBrush;
                _inner = Frozen(new SolidColorBrush(Mix(retro, _dark ? Colors.White : Colors.Black, .20)));
                _glint = Gradient(0, White(_dark ? 68 : 220), .7, Black(_dark ? 80 : 44));
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
        dc.DrawGeometry(_bevel, null, _bevelRing);
        dc.DrawGeometry(_inner, null, _innerRing);
        if (Skin == PaperSkins.LiquidGlass)
        {
            dc.DrawGeometry(_depth, null, _depthRing);
            dc.DrawGeometry(_lensLight, null, _bevelRing);
        }
        if (Skin == PaperSkins.Pixel && !IsCapsule && HeaderHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bottom = Math.Min(ActualHeight - 7, Math.Round((HeaderHeight + BorderThickness.Top) * dpi.DpiScaleY) / dpi.DpiScaleY);
            // A deliberate dark RPG header divider; never a white gap or separate material.
            if (bottom > 7 && ActualWidth > 14)
            {
                dc.DrawRectangle(_header, null, new Rect(7, 7, ActualWidth - 14, bottom - 7));
                dc.DrawRectangle(_inner, null, new Rect(7, bottom, ActualWidth - 14, 1 / dpi.DpiScaleY));
            }
        }
    }
    private static Color White(byte alpha) => Color.FromArgb(alpha, 255, 255, 255);
    private static Color White(int alpha) => White((byte)alpha);
    private static Color Black(int alpha) => Color.FromArgb((byte)alpha, 0, 0, 0);
}
