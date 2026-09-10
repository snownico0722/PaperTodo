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
                // Quiet smoked-blue glass: one reflected sky, not two opaque white ribbons.
                // The tint/reflection cover the entire shell, including its native header.
                var sky = Mix(paper, _dark ? Color.FromRgb(29, 57, 78) : Color.FromRgb(156, 198, 222), .48);
                alpha = opaque ? (byte)255 : (byte)(_dark ? 124 : 108);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(Mix(sky, Colors.White, _dark ? .08 : .22), alpha), 0),
                    new(WithAlpha(sky, alpha), .26),
                    new(WithAlpha(Mix(sky, paper, .18), alpha), .74),
                    new(WithAlpha(Mix(sky, Colors.Black, .055), alpha), 1)
                }, new Point(0, 0), new Point(.08, 1)));
                _shine = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 20 : 46), 0), new(White(_dark ? 27 : 62), .12),
                    new(White(_dark ? 24 : 53), .26), new(White(_dark ? 8 : 18), .43),
                    new(Colors.Transparent, .59), new(White(_dark ? 4 : 9), .87),
                    new(Colors.Transparent, 1)
                }, new Point(.02, 0), new Point(.91, 1)));
                _glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 115 : 210), 0), new(White(_dark ? 34 : 80), .25),
                    new(White(8), .58), new(White(_dark ? 44 : 76), 1)
                }, new Point(0, 0), new Point(.68, 1)));
                _glazeTop = Gradient(0, White(_dark ? 26 : 52), 1, Colors.Transparent);
                _glazeBottom = Gradient(0, Colors.Transparent, 1, Color.FromArgb(18, 28, 50, 72));
                break;
            case PaperSkins.LiquidGlass:
                // Match the optical strips exactly. The center is genuinely transparent,
                // not a blurred/repainted screenshot under a 58–65% opaque cover.
                var lens = LiquidTint;
                var clear = Color.FromRgb((byte)Math.Round(lens.X * 255), (byte)Math.Round(lens.Y * 255), (byte)Math.Round(lens.Z * 255));
                _fill = Frozen(new SolidColorBrush(WithAlpha(clear, opaque ? (byte)255 : (byte)Math.Round(lens.W * 255))));
                _shine = Frozen(new RadialGradientBrush(White(_dark ? 7 : 12), Colors.Transparent)
                { Center = new Point(.27, -.08), GradientOrigin = new Point(.27, -.08), RadiusX = .82, RadiusY = .38 });
                _glint = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 115 : 175), 0), new(White(22), .27),
                    new(Colors.Transparent, .55), new(White(_dark ? 48 : 96), 1)
                }, new Point(0, 0), new Point(1, 1)));
                _lensLight.GradientStops[0].Color = White(_dark ? 105 : 155);
                break;
            case PaperSkins.Ceramic:
                // Porcelain, not yellow wax: a mineral-white body, a localized softbox
                // reflection and a small cool contact shadow. No nested outline.
                var glaze = Mix(paper, _dark ? Color.FromRgb(44, 50, 61) : Color.FromRgb(235, 230, 221), .80);
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Mix(glaze, Colors.White, _dark ? .07 : .19), 0),
                    new(Mix(glaze, Colors.White, _dark ? .015 : .06), .34),
                    new(glaze, .77), new(Mix(glaze, Colors.Black, _dark ? .10 : .04), 1)
                }, new Point(0, 0), new Point(.14, 1)));
                _shine = Frozen(new RadialGradientBrush(new GradientStopCollection
                {
                    new(White(_dark ? 43 : 198), 0), new(White(_dark ? 34 : 162), .24),
                    new(White(_dark ? 14 : 72), .61), new(Colors.Transparent, 1)
                }) { Center = new Point(.28, IsCapsule ? .25 : .08), GradientOrigin = new Point(.24, IsCapsule ? .16 : .025),
                    RadiusX = .73, RadiusY = IsCapsule ? .70 : .29 });
                _glazeTop = Gradient(0, White(_dark ? 42 : 175), 1, Colors.Transparent);
                _glazeBottom = Gradient(0, Colors.Transparent, 1, Color.FromArgb(_dark ? (byte)70 : (byte)39, 37, 41, 47));
                _glazeLeft = Frozen(new LinearGradientBrush(White(_dark ? 20 : 86), Colors.Transparent, 0));
                _glazeRight = Frozen(new LinearGradientBrush(Colors.Transparent, Color.FromArgb(_dark ? (byte)32 : (byte)18, 36, 40, 45), 0));
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
        if (Skin == PaperSkins.Aero)
        {
            var shoulder = Math.Min(IsCapsule ? 5 : 34, ActualHeight * .18);
            if (BorderThickness.Top > 0) dc.DrawRectangle(_glazeTop, null, new Rect(0, 0, ActualWidth, shoulder));
            if (BorderThickness.Bottom > 0) dc.DrawRectangle(_glazeBottom, null, new Rect(0, ActualHeight - shoulder, ActualWidth, shoulder));
        }
        if (Skin == PaperSkins.Ceramic)
        {
            // Blend into the one outer surface. No hollow nested rectangle/ring.
            var depth = Math.Min(IsCapsule ? 2 : 4, Math.Min(ActualWidth, ActualHeight) / 2);
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
