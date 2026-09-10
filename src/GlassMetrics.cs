using System;
using System.Windows;

namespace PaperTodo;

/// <summary>Optical dimensions in DIPs. A broad, shallow shoulder joins a nearly flat
/// body; narrow controls do not inherit the thickness of a large reading panel.</summary>
internal readonly record struct GlassMetrics(double Bezel, double Displacement, double Blur,
    double Tint, double Saturation, double Magnification)
{
    internal static GlassMetrics For(Size size, bool dark)
    {
        var shortSide = Math.Min(size.Width, size.Height);
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || shortSide <= 0)
            return new(0, 0, 0, 0, 1, 0);
        var opticalSize = Math.Min(Math.Sqrt(size.Width * size.Height), shortSide * 1.35);
        var t = Math.Clamp((opticalSize - 160) / 640, 0, 1);
        t = t * t * (3 - 2 * t);
        var shoulder = Math.Min(12 + 14 * t, shortSide * .22);
        return new(shoulder, Math.Min(3 + 2.8 * t, shoulder * .32),
            .75 + 1.3 * t, (dark ? .24 : .105) + .10 * t, 1.12 - .06 * t, .004 + .004 * t);
    }
}
