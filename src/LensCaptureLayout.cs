using System;
using System.Windows;

namespace PaperTodo;

/// <summary>One coherent, bounded scene on a screen-anchored sampling grid.
/// Moving a surface changes its crop, not the phase of the downsampling filter.</summary>
internal static class LensCaptureLayout
{
    internal const long PixelBudget = 1_048_576;
    internal sealed record Scene(Int32Rect Bounds, int PixelWidth, int PixelHeight);

    internal static Scene? Create(Int32Rect window, DesktopLensCapture.Region region, Int32Rect desktop, Scene? previous = null)
    {
        var w = region.Width; var h = region.Height;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || region.Padding is < 0 or > 1024)
            throw new InvalidOperationException("Invalid liquid surface dimensions.");
        var left = Math.Max(desktop.X, window.X + region.OffsetX - region.Padding);
        var top = Math.Max(desktop.Y, window.Y + region.OffsetY - region.Padding);
        var right = Math.Min(desktop.X + desktop.Width, window.X + region.OffsetX + w + region.Padding);
        var bottom = Math.Min(desktop.Y + desktop.Height, window.Y + region.OffsetY + h + region.Padding);
        if (right <= left || bottom <= top) return null;
        // Pick density from the WHOLE surface, not its changing onscreen intersection.
        // Ordinary papers/menus stay 1:1. Large surfaces use integral source-pixel cells.
        var fullWidth = (long)w + 2 * region.Padding;
        var fullHeight = (long)h + 2 * region.Padding;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(
            Math.Sqrt((double)fullWidth * fullHeight / PixelBudget), Math.Max(fullWidth, fullHeight) / 2048d)));
        // Alignment can add one cell on either axis. Budget for the worst phase
        // before intersecting the desktop, so crossing a grid cell never changes density.
        while (step > 1 && (((fullWidth + step - 1) / step + 1) * ((fullHeight + step - 1) / step + 1) > PixelBudget ||
            (fullWidth + step - 1) / step + 1 > 2048 || (fullHeight + step - 1) / step + 1 > 2048)) step++;
        // Hysteresis prevents tiny back-and-forth resizes at the pixel budget from
        // alternating the entire scene between two sampling densities. Recover detail
        // after a substantial shrink, rather than keeping the coarse density forever.
        var previousStep = previous == null ? 0 : previous.Bounds.Width / previous.PixelWidth;
        if (previousStep > step &&
            ((fullWidth + step - 1) / step + 1) * ((fullHeight + step - 1) / step + 1) > PixelBudget * .70)
            step = previousStep;
        // Keep the world-space scene while the surface fits inside its inner guard.
        // Otherwise every tiny drag changes all pixels, defeats duplicate detection,
        // and can alternate bitmap dimensions at a downsample-cell boundary.
        // The caller invalidates previous on DPI/padding or desktop changes, not each
        // new surface size. A retained texture can still cover the resized surface at
        // HIGHER detail than a new fully padded allocation, within its original budget.
        if (previous != null && previousStep <= step &&
            previous.Bounds.Height / previous.PixelHeight == previousStep)
        {
            var guard = region.Padding / 2;
            var needed = new Int32Rect(
                Math.Max(desktop.X, window.X + region.OffsetX - guard),
                Math.Max(desktop.Y, window.Y + region.OffsetY - guard), 0, 0);
            var neededRight = Math.Min(desktop.X + desktop.Width, window.X + region.OffsetX + w + guard);
            var neededBottom = Math.Min(desktop.Y + desktop.Height, window.Y + region.OffsetY + h + guard);
            var b = previous.Bounds;
            if (needed.X >= b.X && needed.Y >= b.Y && neededRight <= b.X + b.Width && neededBottom <= b.Y + b.Height)
                return previous;
        }
        var x = (int)Math.Floor(left / (double)step) * step;
        var y = (int)Math.Floor(top / (double)step) * step;
        var pw = (int)Math.Ceiling(right / (double)step) - x / step;
        var ph = (int)Math.Ceiling(bottom / (double)step) - y / step;
        return new(new Int32Rect(x, y, pw * step, ph * step), pw, ph);
    }
}
