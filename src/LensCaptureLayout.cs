using System;
using System.Windows;

namespace PaperTodo;

/// <summary>One coherent background sample, including a world-space drag margin. The
/// upload budget is fixed even on large/high-DPI papers; text never enters this texture.</summary>
internal static class LensCaptureLayout
{
    internal const long PixelBudget = 196_608;
    internal sealed record Tile(Int32Rect Target, Int32Rect Bounds, int PixelWidth, int PixelHeight);

    internal static Tile[] Create(Int32Rect window, DesktopLensCapture.Region region, Int32Rect desktop)
    {
        var w = region.Width; var h = region.Height;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || region.Padding is < 0 or > 1024)
            throw new InvalidOperationException("Invalid liquid surface dimensions.");
        var left = Math.Max(desktop.X, window.X + region.OffsetX - region.Padding);
        var top = Math.Max(desktop.Y, window.Y + region.OffsetY - region.Padding);
        var right = Math.Min(desktop.X + desktop.Width, window.X + region.OffsetX + w + region.Padding);
        var bottom = Math.Min(desktop.Y + desktop.Height, window.Y + region.OffsetY + h + region.Padding);
        if (right <= left || bottom <= top) return [];
        var bounds = new Int32Rect(left, top, right - left, bottom - top);
        var scale = Math.Min(1, Math.Sqrt(PixelBudget / ((double)bounds.Width * bounds.Height)));
        scale = Math.Min(scale, 2048d / Math.Max(bounds.Width, bounds.Height));
        return [new(new Int32Rect(0, 0, w, h), bounds,
            Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)))];
    }
}
