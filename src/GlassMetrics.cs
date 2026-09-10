using System;
using System.Windows;

namespace PaperTodo;

/// <summary>Optical dimensions are DIPs, not fractions of a stretched image. Long, narrow
/// papers retain a light material; larger reading surfaces gain gentle scattering/tint.</summary>
internal readonly record struct GlassMetrics(double Bezel, double Displacement, double Blur,
    double Tint, double Saturation)
{
    internal static GlassMetrics For(Size size, bool dark)
    {
        var shortSide = Math.Max(0, Math.Min(size.Width, size.Height));
        var opticalSize = Math.Min(Math.Sqrt(size.Width * size.Height), shortSide * 1.35);
        var t = Math.Clamp((opticalSize - 160) / 640, 0, 1);
        t = t * t * (3 - 2 * t);
        return new(Math.Min(7 + 9 * t, shortSide * .18), Math.Min(3 + 4 * t, shortSide * .08),
            1.0 + 2.4 * t, (dark ? .32 : .17) + .10 * t, 1.22 - .10 * t);
    }
}
