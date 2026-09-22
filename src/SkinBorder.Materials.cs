using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Brush _header = Brushes.Transparent;
    private readonly MatrixTransform _reflectionShift = new();
    private readonly LinearGradientBrush _aeroReflection = new()
    {
        MappingMode = BrushMappingMode.Absolute,
        StartPoint = new Point(-180, -100), EndPoint = new Point(680, 430),
        SpreadMethod = GradientSpreadMethod.Reflect
    };
    private DrawingGroup? _relief;
    internal bool HasMaterialRelief => _relief != null;
    private (Size Size, CornerRadius Radius, Thickness Border, double DpiX, double DpiY, string Skin, bool Dark)? _reliefKey;

    private void EnsureBrushes(Color background)
    {
        var palette = Theme.MaterialColors;
        var key = (Skin, _dark, IsCapsule, IsMenu, MaterialStrength, background,
            palette, Theme.PaperBrush, Theme.ActiveBrush, UseLightweightMaterial);
        if (_brushKey == key) return;
        _brushKey = key;
        BrushBuildCount++;
        _surfaceVersion++;
        var paper = palette.Surface;
        if (UseLightweightMaterial && PaperSkins.UsesNativeBackdrop(Skin))
        {
            _fill = Frozen(new SolidColorBrush(palette.Preview));
            _shine = _header = Brushes.Transparent;
            return;
        }
        var opaque = !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;
        byte alpha = opaque ? (byte)255 : palette.TransmissionAlpha;
        _fill = Frozen(new SolidColorBrush(WithAlpha(paper, alpha)));
        _shine = _header = Brushes.Transparent;
        switch (Skin)
        {
            case PaperSkins.Mica:
            case PaperSkins.Acrylic:
            case PaperSkins.ClearAcrylic:
                // Transmitted background + diffuse color, shared by both auxiliary strengths.
                // Main Mica/Acrylic keeps its DWM backdrop; a layered popup uses real local
                // background diffusion instead of pretending an opaque gradient is blur.
                var nativeHighlight = Skin == PaperSkins.Mica ? .035 : .075;
                var density = opaque ? (byte)255 : palette.TransmissionAlpha;
                _fill = Gradient(0, WithAlpha(Mix(paper, Colors.White, nativeHighlight), density),
                    1, WithAlpha(paper, density));
                _shine = Gradient(0, White(_dark ? 16 : 28), 1, White(0));
                break;
            case PaperSkins.Aero:
                // Aero needs colored transmission and bounded specular bands. Mixing
                // blue into opaque white paper and stacking a broad white wash made milk.
                var glass = opaque ? Mix(((SolidColorBrush)Theme.PaperBrush).Color, paper, .18) : paper;
                var top = Mix(glass, Colors.White, .035);
                var low = Mix(glass, Color.FromRgb(12, 39, 66), opaque ? .06 : .25);
                byte a = opaque ? (byte)255 : palette.TransmissionAlpha;
                _fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(WithAlpha(top, a), 0), new(WithAlpha(glass, a), .15),
                    new(WithAlpha(glass, a), .82), new(WithAlpha(low, a), 1)
                }, new Point(0, 0), new Point(0, 1)));
                _aeroReflection.GradientStops = new GradientStopCollection
                {
                    new(White(0), 0), new(White(0), .12),
                    new(White(_dark ? 10 : 18), .17), new(White(_dark ? 29 : 50), .21),
                    new(White(_dark ? 12 : 21), .29), new(White(0), .38),
                    new(White(0), .49), new(White(_dark ? 11 : 21), .56),
                    new(White(_dark ? 5 : 10), .63), new(White(0), .72), new(White(0), 1)
                };
                _aeroReflection.Transform = _reflectionShift;
                _shine = _aeroReflection;
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
        if (UseLightweightMaterial && Skin == PaperSkins.Aero)
        {
            // Preview changes size every frame without rebuilding relief strips.
            return;
        }
        if (Skin == PaperSkins.Aero)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var key = (RenderSize, CornerRadius, BorderThickness, dpi.DpiScaleX, dpi.DpiScaleY, Skin, _dark);
            if (_reliefKey != key)
            {
                _relief = MaterialRelief.Create(RenderSize, CornerRadius, BorderThickness, dpi, _dark);
                _reliefKey = key;
            }
            dc.DrawDrawing(_relief);
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
