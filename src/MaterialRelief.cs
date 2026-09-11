using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>Small, cached lighting strips for a single continuous curved surface.
/// Normal-dependent glass reflection, not a stack of inset rectangular borders. No
/// timer, screenshot, whole-surface shader or change to the host's shape/input area.</summary>
internal static class MaterialRelief
{
    internal static DrawingGroup Create(Size size, CornerRadius corners, Thickness border, DpiScale dpi,
        string skin, bool dark)
    {
        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        { var empty = new DrawingGroup(); empty.Freeze(); return empty; }
        var liquid = skin == PaperSkins.LiquidGlass;
        var bevel = Math.Min(liquid ? Math.Clamp(GlassMetrics.For(size, dark).Bezel * .4, 3, 9) : 4,
            Math.Min(size.Width, size.Height) / 2);
        var band = Math.Min(Math.Max(bevel, Math.Max(Math.Max(corners.TopLeft, corners.TopRight),
            Math.Max(corners.BottomLeft, corners.BottomRight))) + 1, Math.Min(size.Width, size.Height) / 2);
        var areas = new List<Rect>
        {
            new(0, 0, size.Width, band), new(0, size.Height - band, size.Width, band)
        };
        if (size.Height > band * 2)
        {
            areas.Add(new(0, band, band, size.Height - band * 2));
            areas.Add(new(size.Width - band, band, band, size.Height - band * 2));
        }
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        foreach (var area in areas)
        {
            var scale = Math.Min(2, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
            // O(perimeter), at most 64K samples per strip even on a giant desktop.
            scale *= Math.Min(1, Math.Sqrt(65536 / Math.Max(1, area.Width * area.Height * scale * scale)));
            var w = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
            var h = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
            var bytes = new byte[w * h * 4];
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            {
                var p = new Point(area.X + (x + .5) * area.Width / w, area.Y + (y + .5) * area.Height / h);
                var radius = p.Y < size.Height / 2
                    ? p.X < size.Width / 2 ? corners.TopLeft : corners.TopRight
                    : p.X < size.Width / 2 ? corners.BottomLeft : corners.BottomRight;
                var (distance, n) = LensDisplacement.Surface(p, size, radius);
                if (distance < .4 || distance >= bevel) continue;
                // Docked host edges remain open; neither diffuse nor glossy light may
                // manufacture a seam where its authoritative border is zero-width.
                if (n.X < 0 && border.Left == 0 || n.X > 0 && border.Right == 0 ||
                    n.Y < 0 && border.Top == 0 || n.Y > 0 && border.Bottom == 0) continue;
                var rim = 1 - distance / bevel;
                var slope = (liquid ? 2.2 : 1.65) * rim * rim;
                var nz = 1 / Math.Sqrt(1 + slope * slope);
                var nx = n.X * slope * nz; var ny = n.Y * slope * nz;
                var diffuse = Math.Clamp(-.32 * nx - .46 * ny + .83 * nz, 0, 1);
                var half = Math.Clamp(-.18 * nx - .26 * ny + .949 * nz, 0, 1);
                var specular = Math.Pow(half, 55);
                // A narrow highlight and transmitted shadow define glass thickness.
                // Neither one creates a broad opaque inner frame.
                var shadow = .16 * rim * (1 - diffuse);
                // Grazing-angle reflection supplies a fine outer highlight, while the
                // specular lobe rolls across the curved shoulder. No broad white bezel.
                var fresnel = liquid ? .06 + .94 * Math.Pow(1 - nz, 5) : 0;
                var gloss = (specular * .92 + fresnel * .55) * rim;
                var bounce = Math.Pow(Math.Clamp(.35 * nx + .40 * ny + .847 * nz, 0, 1), 55) * rim * .16;
                gloss = Math.Clamp(gloss + bounce, 0, .75);
                if (dark) gloss *= .72;
                var alpha = gloss + shadow * (1 - gloss);
                if (alpha <= 0) continue;
                var i = (y * w + x) * 4;
                // Premultiplied white/cool reflection + a neutral transmitted shadow.
                bytes[i] = (byte)Math.Round(255 * gloss);
                bytes[i + 1] = (byte)Math.Round(253 * gloss);
                bytes[i + 2] = (byte)Math.Round(250 * gloss);
                bytes[i + 3] = (byte)Math.Round(255 * alpha);
            }
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, w * 4);
            bitmap.Freeze(); dc.DrawImage(bitmap, area);
        }
        drawing.Freeze(); return drawing;
    }
}
