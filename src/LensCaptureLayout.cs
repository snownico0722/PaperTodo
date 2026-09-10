using System;
using System.Collections.Generic;
using System.Windows;

namespace PaperTodo;

/// <summary>Four non-overlapping optical strips. The unmodified center is supplied by
/// desktop composition, not captured/repainted by WPF. All geometry here is physical pixels.</summary>
internal static class LensCaptureLayout
{
    internal const long PixelBudget = 1_048_576;
    internal sealed record Tile(Int32Rect Target, Int32Rect Bounds, int PixelWidth, int PixelHeight);

    internal static Tile[] Create(Int32Rect window, DesktopLensCapture.Region region, Int32Rect desktop)
    {
        var w = region.Width; var h = region.Height;
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || region.Padding is < 0 or > 1024)
            throw new InvalidOperationException("Invalid liquid surface dimensions.");
        var rim = Math.Clamp(region.Rim, 0, (Math.Min(w, h) + 1) / 2);
        var targets = new List<Int32Rect>(4);
        if (rim == 0) targets.Add(new(0, 0, w, h));
        else
        {
            targets.Add(new(0, 0, w, Math.Min(rim, h)));
            var bottom = Math.Max(rim, h - rim);
            if (bottom < h) targets.Add(new(0, bottom, w, h - bottom));
            var middle = Math.Max(0, h - 2 * rim);
            if (middle > 0)
            {
                targets.Add(new(0, rim, Math.Min(rim, w), middle));
                var right = Math.Max(rim, w - rim);
                if (right < w) targets.Add(new(right, rim, w - right, middle));
            }
        }
        var tiles = new List<Tile>(4); long pixels = 0;
        foreach (var target in targets)
        {
            var left = Math.Max(desktop.X, window.X + region.OffsetX + target.X - region.Padding);
            var top = Math.Max(desktop.Y, window.Y + region.OffsetY + target.Y - region.Padding);
            var right = Math.Min(desktop.X + desktop.Width, window.X + region.OffsetX + target.X + target.Width + region.Padding);
            var bottom = Math.Min(desktop.Y + desktop.Height, window.Y + region.OffsetY + target.Y + target.Height + region.Padding);
            if (right <= left || bottom <= top) continue;
            var bounds = new Int32Rect(left, top, right - left, bottom - top);
            tiles.Add(new(target, bounds, bounds.Width, bounds.Height));
            pixels += (long)bounds.Width * bounds.Height;
        }
        // Downsample only overscanned edge samples on very large/high-DPI windows. Do not
        // silently turn off refraction just because a mostly empty center became large.
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pixels / (double)PixelBudget)));
        foreach (var tile in tiles)
            step = Math.Max(step, (Math.Max(tile.Bounds.Width, tile.Bounds.Height) + 4095) / 4096);
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            tiles[i] = tile with { PixelWidth = Math.Max(1, tile.Bounds.Width / step), PixelHeight = Math.Max(1, tile.Bounds.Height / step) };
        }
        return tiles.ToArray();
    }
}
