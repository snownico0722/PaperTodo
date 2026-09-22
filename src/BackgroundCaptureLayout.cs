using System;
using System.Windows;

namespace PaperTodo;

/// <summary>One bounded source rectangle for a one-shot auxiliary-material snapshot.</summary>
internal static class BackgroundCaptureLayout
{
    internal const long PixelBudget = 1_048_576;
    private const double StaticGuardDip = 96;

    // 48 DIP of inner guard remains after centering the surface in the snapshot, which is
    // larger than the strongest 38-DIP material diffusion radius. Dragging uses a separate
    // full-virtual-desktop texture and therefore needs no motion headroom here.
    internal static int Padding(DpiScale dpi) =>
        (int)Math.Min(1024, Math.Ceiling(StaticGuardDip * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));

    internal sealed record Scene(Int32Rect Bounds, int PixelWidth, int PixelHeight);

    internal static Scene? Create(
        Int32Rect window,
        DesktopBackgroundCapture.Region region,
        Int32Rect desktop)
    {
        var w = region.Width;
        var h = region.Height;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || region.Padding is < 0 or > 1024)
            throw new InvalidOperationException("Invalid background surface dimensions.");

        var left = Math.Max(desktop.X, window.X + region.OffsetX - region.Padding);
        var top = Math.Max(desktop.Y, window.Y + region.OffsetY - region.Padding);
        var right = Math.Min(
            desktop.X + desktop.Width,
            window.X + region.OffsetX + w + region.Padding);
        var bottom = Math.Min(
            desktop.Y + desktop.Height,
            window.Y + region.OffsetY + h + region.Padding);
        if (right <= left || bottom <= top) return null;

        var sourceWidth = right - left;
        var sourceHeight = bottom - top;
        var step = Math.Max(
            1,
            (int)Math.Ceiling(Math.Max(
                Math.Sqrt((double)sourceWidth * sourceHeight / PixelBudget),
                Math.Max(sourceWidth, sourceHeight) / 2048d)));

        var pixelWidth = (int)Math.Ceiling(sourceWidth / (double)step);
        var pixelHeight = (int)Math.Ceiling(sourceHeight / (double)step);
        while ((long)pixelWidth * pixelHeight > PixelBudget ||
               pixelWidth > 2048 ||
               pixelHeight > 2048)
        {
            step++;
            pixelWidth = (int)Math.Ceiling(sourceWidth / (double)step);
            pixelHeight = (int)Math.Ceiling(sourceHeight / (double)step);
        }

        return new(
            new Int32Rect(left, top, sourceWidth, sourceHeight),
            pixelWidth,
            pixelHeight);
    }
}
