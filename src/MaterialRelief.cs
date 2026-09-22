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
        bool dark) => Create(size, corners, border, dpi, dark, out _);

    internal readonly record struct BuildMetrics(int Pixels, int LightingEvaluations, int ReusedLighting);
    internal static DrawingGroup Create(Size size, CornerRadius corners, Thickness border, DpiScale dpi,
        bool dark, out BuildMetrics metrics, bool reuseStraightEdges = true)
    {
        metrics = default;
        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        { var empty = new DrawingGroup(); empty.Freeze(); return empty; }
        var bevel = Math.Min(4, Math.Min(size.Width, size.Height) / 2);
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
        // Along an axis-aligned straight edge, illumination depends only on depth, not
        // on surface length. Reuse EXACT depth/normal samples; corners stay analytic.
        var pixels = 0; var evaluations = 0; var reused = 0;
        using (var dc = drawing.Open())
        foreach (var area in areas)
        {
            var scale = Math.Min(2, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
            // O(perimeter), at most 64K samples per strip even on a giant desktop.
            scale *= Math.Min(1, Math.Sqrt(65536 / Math.Max(1, area.Width * area.Height * scale * scale)));
            var w = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
            var h = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
            var bytes = new byte[w * h * 4];
            // Horizontal runs repeat within one row; vertical runs repeat at the same
            // column on following rows. A tiny direct cache avoids hashing every pixel.
            var horizontal = area.Width == size.Width;
            var lastHorizontal = default(EdgeSample);
            var vertical = reuseStraightEdges && !horizontal ? new EdgeSample[w] : null;
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            {
                var p = new Point(area.X + (x + .5) * area.Width / w, area.Y + (y + .5) * area.Height / h);
                var radius = p.Y < size.Height / 2
                    ? p.X < size.Width / 2 ? corners.TopLeft : corners.TopRight
                    : p.X < size.Width / 2 ? corners.BottomLeft : corners.BottomRight;
                var (distance, n) = Surface(p, size, radius);
                if (distance < .4 || distance >= bevel) continue;
                // Docked host edges remain open; neither diffuse nor glossy light may
                // manufacture a seam where its authoritative border is zero-width.
                if (n.X < 0 && border.Left == 0 || n.X > 0 && border.Right == 0 ||
                    n.Y < 0 && border.Top == 0 || n.Y > 0 && border.Bottom == 0) continue;
                uint color;
                if (reuseStraightEdges && (n.X == 0 || n.Y == 0))
                {
                    ref var cached = ref (horizontal ? ref lastHorizontal : ref vertical![x]);
                    if (cached.Depth == distance && cached.X == n.X && cached.Y == n.Y)
                    {
                        color = cached.Color;
                        reused++;
                    }
                    else
                    {
                        color = Shade(distance, n, bevel, dark);
                        cached = new EdgeSample(distance, n.X, n.Y, color);
                        evaluations++;
                    }
                }
                else
                {
                    color = Shade(distance, n, bevel, dark);
                    evaluations++;
                }
                var i = (y * w + x) * 4;
                bytes[i] = (byte)color;
                bytes[i + 1] = (byte)(color >> 8);
                bytes[i + 2] = (byte)(color >> 16);
                bytes[i + 3] = (byte)(color >> 24);
            }
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, w * 4);
            bitmap.Freeze(); dc.DrawImage(bitmap, area);
            pixels += w * h;
        }
        metrics = new(pixels, evaluations, reused);
        drawing.Freeze(); return drawing;
    }
    private readonly record struct EdgeSample(double Depth, double X, double Y, uint Color);

    private static uint Shade(double distance, Vector n, double bevel, bool dark)
    {
        var rim = 1 - distance / bevel;
        var slope = 1.65 * rim * rim;
        var nz = 1 / Math.Sqrt(1 + slope * slope);
        var nx = n.X * slope * nz; var ny = n.Y * slope * nz;
        var diffuse = Math.Clamp(-.32 * nx - .46 * ny + .83 * nz, 0, 1);
        var half = Math.Clamp(-.18 * nx - .26 * ny + .949 * nz, 0, 1);
        var specular = Math.Pow(half, 55);
        // A narrow highlight and transmitted shadow define glass thickness.
        // Neither one creates a broad opaque inner frame.
        var shadow = .16 * rim * (1 - diffuse);
        // The specular lobe rolls across the curved shoulder without a broad white bezel.
        var gloss = specular * .92 * rim;
        var bounce = Math.Pow(Math.Clamp(.35 * nx + .40 * ny + .847 * nz, 0, 1), 55) * rim * .16;
        gloss = Math.Clamp(gloss + bounce, 0, .75);
        if (dark) gloss *= .72;
        var alpha = gloss + shadow * (1 - gloss);
        if (alpha <= 0) return 0;
        return (uint)(byte)Math.Round(255 * gloss) |
            (uint)(byte)Math.Round(253 * gloss) << 8 |
            (uint)(byte)Math.Round(250 * gloss) << 16 |
            (uint)(byte)Math.Round(255 * alpha) << 24;
    }

    private static (double Distance, Vector Normal) Surface(Point p, Size size, double radius)
    {
        var half = new Vector(size.Width / 2, size.Height / 2);
        var v = new Vector(p.X - half.X, p.Y - half.Y);
        radius = Math.Clamp(radius, 0, Math.Min(half.X, half.Y));
        var q = new Vector(Math.Abs(v.X) - half.X + radius, Math.Abs(v.Y) - half.Y + radius);
        var outside = new Vector(Math.Max(q.X, 0), Math.Max(q.Y, 0));
        var length = outside.Length;
        var distance = radius - length - Math.Min(Math.Max(q.X, q.Y), 0);
        var blend = Math.Clamp((q.X - q.Y) * .5 + .5, 0, 1);
        var flatNormal = new Vector(blend, 1 - blend);
        flatNormal.Normalize();
        var normal = length > .0001
            ? new Vector(outside.X / length * (v.X < 0 ? -1 : 1), outside.Y / length * (v.Y < 0 ? -1 : 1))
            : new Vector(flatNormal.X * (v.X < 0 ? -1 : 1), flatNormal.Y * (v.Y < 0 ? -1 : 1));
        return (distance, normal);
    }

}
