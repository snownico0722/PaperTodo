using System;
using System.Windows;

namespace PaperTodo;

/// <summary>Optical dimensions in DIPs. A broad, shallow shoulder joins a nearly flat
/// body; narrow controls do not inherit the thickness of a large reading panel.</summary>
internal readonly record struct GlassMetrics(double Bezel, double Displacement, double Blur,
    double Tint, double Saturation, double Magnification)
{
    internal const double ChromaticSpread = .24;

    // Use a rounded optical shoulder even on a sharper host. Keeping its radius at
    // least as wide as the shoulder prevents rays crossing the inner corner centre.
    // This is background geometry only; the host outline and hit area do not change.
    internal double OpticalRadius(double hostRadius) => Math.Max(Bezel, hostRadius);

    internal static GlassMetrics For(Size size, bool dark)
    {
        var shortSide = Math.Min(size.Width, size.Height);
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || shortSide <= 0)
            return new(0, 0, 0, 0, 1, 0);
        var opticalSize = Math.Min(Math.Sqrt(size.Width * size.Height), shortSide * 1.35);
        var t = Math.Clamp((opticalSize - 160) / 640, 0, 1);
        t = t * t * (3 - 2 * t);
        var shoulder = Math.Min(18 + 12 * t, shortSide * .26);
        // Quintic falloff has a peak derivative of 1.875. Bound the most displaced
        // RGB channel as well, not just green: enlarging displacement alone folds
        // background lines inside the shoulder (especially on small capsules).
        return new(shoulder, Math.Min(8 + 3 * t, shoulder * .38),
            .45 + .65 * t, (dark ? .22 : .085) + .075 * t, 1.10 - .04 * t, .006 + .003 * t);
    }
}
