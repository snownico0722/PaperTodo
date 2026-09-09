using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Brush _header = Brushes.Transparent;
    private Brush _glazeTop = Brushes.Transparent, _glazeBottom = Brushes.Transparent;
    private Brush _glazeLeft = Brushes.Transparent, _glazeRight = Brushes.Transparent;
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
            PaperSkins.Aero => (byte)(_dark ? 174 : 146),
            // A visible lens veil, not a near-empty transparent window. The rear
            // detail remains sharp; opacity is independent of background blur.
            _ => (byte)(_dark ? 196 : 152)
        };
        _fill = Frozen(new SolidColorBrush(WithAlpha(paper, alpha)));
        _shine = _glint = _header = Brushes.Transparent;
        switch (Skin)
        {
            case PaperSkins.Aero:
                var glassBlue = Mix(paper, _dark ? Color.FromRgb(28, 81, 113) : Color.FromRgb(113, 183, 218), .64);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(Mix(glassBlue, Colors.White, _dark ? .07 : .27), alpha), 0),
                    new(WithAlpha(glassBlue, alpha), .20),
                    new(WithAlpha(Mix(glassBlue, paper, .26), alpha), .72),
                    new(WithAlpha(Mix(glassBlue, Colors.Black, .06), alpha), 1)
                }, new Point(0, 0), new Point(0, 1)));
                // Two broad reflected light sources, feathered on both sides. The lower
                // blue glass remains visible: neither a uniform Acrylic wash nor white tape.
                _shine = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 8 : 15), 0), new(White(_dark ? 12 : 25), .10),
                    new(White(_dark ? 48 : 125), .19), new(White(_dark ? 51 : 134), .25),
                    new(White(_dark ? 8 : 18), .36), new(Colors.Transparent, .45),
                    new(White(_dark ? 7 : 12), .66), new(White(_dark ? 29 : 76), .80),
                    new(White(_dark ? 13 : 30), .89), new(Colors.Transparent, 1)
                }, new Point(0, 0), new Point(1, .48)));
                _glint = Gradient(0, White(_dark ? 80 : 192), 1, White(_dark ? 8 : 32));
                break;
            case PaperSkins.LiquidGlass:
                // A translucent, neutral lens with a readable center; no frosted Acrylic.
                // The native adapter supplies alpha composition without background blur.
                var clear = _dark ? Color.FromRgb(26, 34, 44) : Color.FromRgb(246, 251, 255);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Min(255, alpha + 20)), 0),
                    new(WithAlpha(clear, alpha), .20), new(WithAlpha(clear, alpha), .80),
                    new(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Min(255, alpha + 12)), 1)
                }, new Point(0, 0), new Point(0, 1)));
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
                // A warm porcelain body and broad specular shoulder, not a paper tint.
                // Smooth edge shading has no inner outline and cannot make another bezel.
                var glaze = Mix(paper, _dark ? Color.FromRgb(49, 51, 55) : Color.FromRgb(232, 218, 193), .72);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(glaze, Colors.White, _dark ? .03 : .22), 0),
                    new(glaze, .35), new(Mix(glaze, Colors.Black, _dark ? .10 : .025), 1)
                }, new Point(0, 0), new Point(.25, 1)));
                _shine = Frozen(new RadialGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 20 : 165), 0), new(White(_dark ? 18 : 150), .30),
                    new(White(_dark ? 8 : 65), .63), new(Colors.Transparent, 1)
                }) { Center = new Point(.25, .02), GradientOrigin = new Point(.18, -.03), RadiusX = .90, RadiusY = .65 });
                _glazeTop = Gradient(0, White(_dark ? 38 : 195), 1, Colors.Transparent);
                _glazeBottom = Gradient(0, Colors.Transparent, 1, Color.FromArgb(_dark ? (byte)76 : (byte)48, 65, 50, 33));
                _glazeLeft = Frozen(new LinearGradientBrush(White(_dark ? 20 : 100), Colors.Transparent, 0));
                _glazeRight = Frozen(new LinearGradientBrush(Colors.Transparent, Color.FromArgb(_dark ? (byte)56 : (byte)32, 40, 34, 26), 0));
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
        if (Skin == PaperSkins.Ceramic)
        {
            // Blend into the one outer surface. No hollow nested rectangle/ring.
            var depth = Math.Min(IsCapsule ? 3 : 6, Math.Min(ActualWidth, ActualHeight) / 2);
            if (BorderThickness.Top > 0) dc.DrawRectangle(_glazeTop, null, new Rect(0, 0, ActualWidth, depth));
            if (BorderThickness.Bottom > 0) dc.DrawRectangle(_glazeBottom, null, new Rect(0, ActualHeight - depth, ActualWidth, depth));
            if (BorderThickness.Left > 0) dc.DrawRectangle(_glazeLeft, null, new Rect(0, 0, depth, ActualHeight));
            if (BorderThickness.Right > 0) dc.DrawRectangle(_glazeRight, null, new Rect(ActualWidth - depth, 0, depth, ActualHeight));
        }
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
