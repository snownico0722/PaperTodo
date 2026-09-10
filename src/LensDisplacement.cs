using System;
using System.Windows;

namespace PaperTodo;

/// <summary>Reference-inspired inward bezel refraction, not a magnifying sheet.
/// The center has exactly zero displacement. The shader evaluates this field directly;
/// resizing does not rasterize or upload a full-window displacement map.</summary>
internal static class LensDisplacement
{
    internal const double BezelDip = 18;
    internal const double MaxShiftDip = 18;

    internal static (Vector Offset, double Coverage) Sample(Point p, Size size, double radius)
    {
        var half = new Vector(size.Width / 2, size.Height / 2);
        var v = new Vector(p.X - half.X, p.Y - half.Y);
        radius = Math.Clamp(radius, 0, Math.Min(half.X, half.Y));
        var q = new Vector(Math.Abs(v.X) - half.X + radius, Math.Abs(v.Y) - half.Y + radius);
        var outside = new Vector(Math.Max(q.X, 0), Math.Max(q.Y, 0));
        var length = outside.Length;
        var distance = radius - length - Math.Min(Math.Max(q.X, q.Y), 0);
        var bezel = Math.Min(BezelDip, Math.Min(half.X, half.Y));
        if (bezel <= 0 || distance < 0 || distance >= bezel) return (new Vector(), 0);
        Vector normal;
        if (length > .0001)
            normal = new Vector(outside.X / length * (v.X < 0 ? -1 : 1), outside.Y / length * (v.Y < 0 ? -1 : 1));
        else
            normal = q.X > q.Y ? new Vector(v.X < 0 ? -1 : 1, 0) : new Vector(0, v.Y < 0 ? -1 : 1);
        var rim = Math.Clamp(1 - distance / bezel, 0, 1);
        var coverage = Math.Clamp(rim * 5, 0, 1);
        coverage *= coverage * (3 - 2 * coverage);
        return (-normal * (bezel * rim * Math.Sqrt(rim)), coverage);
    }
}
