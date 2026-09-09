using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Brush _header = Brushes.Transparent;
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
        byte alpha = opaque ? (byte)255 : Skin switch
        {
            PaperSkins.TracingPaper => (byte)(_dark ? 226 : 211),
            PaperSkins.Aero => (byte)(_dark ? 168 : 112),
            _ => (byte)(_dark ? 70 : 44)
        };
        _fill = Frozen(new SolidColorBrush(WithAlpha(paper, alpha)));
        _shine = _glint = _header = Brushes.Transparent;
        switch (Skin)
        {
            case PaperSkins.Pearl:
                _fill = Frozen(new SolidColorBrush(Mix(paper, _dark ? Color.FromRgb(29, 28, 41) : Colors.White, .30)));
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
                break;
            case PaperSkins.Aero:
                var glass = Mix(paper, _dark ? Color.FromRgb(25, 45, 62) : Color.FromRgb(185, 220, 243), .38);
                _fill = Gradient(0, WithAlpha(Mix(glass, Colors.White, _dark ? .025 : .12), alpha),
                    1, WithAlpha(glass, alpha));
                // One broad, soft light reflection. Closely spaced, discontinuous stops
                // looked like diagonal tape pasted across the editor rather than glass.
                _shine = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 4 : 10), 0), new(White(_dark ? 9 : 28), .17),
                    new(White(_dark ? 20 : 66), .32), new(White(_dark ? 13 : 40), .43),
                    new(White(_dark ? 5 : 12), .59), new(Colors.Transparent, .78),
                    new(White(_dark ? 4 : 12), 1)
                }, new Point(0, 0), new Point(1, .55)));
                _glint = Gradient(0, White(_dark ? 60 : 154), 1, White(_dark ? 4 : 12));
                break;
            case PaperSkins.LiquidGlass:
                // A clear, neutral lens, not a cream plate over system Acrylic.
                // The native adapter supplies alpha composition without background blur.
                var clear = _dark ? Color.FromRgb(26, 34, 44) : Color.FromRgb(246, 251, 255);
                _fill = Frozen(new SolidColorBrush(WithAlpha(clear, alpha)));
                _shine = Frozen(new RadialGradientBrush(White(_dark ? 6 : 18), Colors.Transparent)
                { Center = new Point(.25, 0), GradientOrigin = new Point(.25, 0), RadiusX = .85, RadiusY = .5 });
                // Keep reflection on a single device-pixel rim. No concentric dark frame,
                // inner white rectangle or broad bottom gradient behind the text.
                _glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 100 : 155), 0), new(White(10), .36),
                    new(Colors.Transparent, .53), new(White(_dark ? 46 : 88), 1)
                }, new Point(0, 0), new Point(1, 1)));
                _lensLight.GradientStops[0].Color = White(_dark ? 100 : 150);
                break;
            case PaperSkins.Ceramic:
                var glaze = Mix(paper, _dark ? Color.FromRgb(38, 42, 47) : Color.FromRgb(255, 254, 247), .64);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(glaze, Colors.White, _dark ? .06 : .25), 0), new(glaze, .20),
                    new(Mix(glaze, paper, .16), .78), new(Mix(glaze, Colors.Black, _dark ? .09 : .055), 1)
                }, new Point(0, 0), new Point(.12, 1)));
                _shine = Frozen(new RadialGradientBrush(White(_dark ? 14 : 88), Colors.Transparent)
                { Center = new Point(.2, -.1), GradientOrigin = new Point(.2, -.1), RadiusX = 1.1, RadiusY = .44 });
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
        if (Skin is PaperSkins.Aero or PaperSkins.LiquidGlass)
            dc.DrawGeometry(_glint, null, _glintRing);
        if (Skin == PaperSkins.LiquidGlass)
            dc.DrawGeometry(_lensLight, null, _glintRing);
        if (Skin == PaperSkins.Pixel && !IsCapsule && HeaderHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bottom = Math.Min(ActualHeight, Math.Round((HeaderHeight + BorderThickness.Top) * dpi.DpiScaleY) / dpi.DpiScaleY);
            if (bottom > 0)
            {
                // Fill to the existing shape, not an inset panel that makes a second bezel.
                dc.DrawRectangle(_header, null, new Rect(0, 0, ActualWidth, bottom));
                dc.DrawRectangle(Theme.PaperBorderBrush, null, new Rect(0, bottom, ActualWidth, 1 / dpi.DpiScaleY));
            }
        }
    }
    private static Color White(int alpha) => Color.FromArgb((byte)alpha, 255, 255, 255);
}
