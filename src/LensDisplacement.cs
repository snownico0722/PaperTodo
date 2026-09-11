using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>A monotone optical shoulder inspired by index-main. The displacement joins
/// the flat body with zero slope; no interior peak/fold or full-size displacement map.</summary>
internal static class LensDisplacement
{
    private const int ProfileSamples = 512;
    private static readonly Lazy<Brush> Profile = new(CreateProfile);
    internal static Brush ProfileBrush => Profile.Value;

    // A controlled UI lens, not a physical multi-interface ray tracer. A monotone
    // falloff avoids the old bright, folded band a few pixels inside the edge.
    internal static (double Shift, double Slope, double Fresnel, double Coverage) ProfileAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t >= 1) return (0, 0, 0, 0);
        var u = 1 - t;
        // Quintic easing has zero first and second derivatives at both joins. A wider
        // shoulder can be visible without a hard ridge or an interior folded band.
        var shift = 1 - t * t * t * (10 + t * (-15 + 6 * t));
        var coverage = Math.Clamp((1 - t) * 6, 0, 1);
        coverage = coverage * coverage * (3 - 2 * coverage);
        return (shift, Math.Sqrt(u), u * u * u * u, coverage);
    }

    private static Brush CreateProfile()
    {
        var bytes = new byte[ProfileSamples * 4];
        for (var x = 0; x < ProfileSamples; x++)
        {
            var p = ProfileAt(x / (double)(ProfileSamples - 1));
            // A single 8-bit channel visibly terraces a several-DIP bend. Encode the
            // displacement in RG at 16-bit precision; a linear dot product in the
            // shader reconstructs it even across a low-byte wrap during bilinear sampling.
            var shift = (int)Math.Round(p.Shift * 65535);
            bytes[x * 4] = (byte)Math.Round(p.Fresnel * 255);
            bytes[x * 4 + 1] = (byte)(shift & 255);
            bytes[x * 4 + 2] = (byte)(shift >> 8);
            bytes[x * 4 + 3] = 255;
        }
        var bitmap = BitmapSource.Create(ProfileSamples, 1, 96, 96, PixelFormats.Bgra32, null, bytes, ProfileSamples * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; brush.Freeze();
        return brush;
    }

    internal static (double Distance, Vector Normal) Surface(Point p, Size size, double radius)
    {
        var half = new Vector(size.Width / 2, size.Height / 2);
        var v = new Vector(p.X - half.X, p.Y - half.Y);
        radius = Math.Clamp(radius, 0, Math.Min(half.X, half.Y));
        var q = new Vector(Math.Abs(v.X) - half.X + radius, Math.Abs(v.Y) - half.Y + radius);
        var outside = new Vector(Math.Max(q.X, 0), Math.Max(q.Y, 0));
        var length = outside.Length;
        var distance = radius - length - Math.Min(Math.Max(q.X, q.Y), 0);
        // Smooth the inner corner bisector instead of abruptly choosing a side.
        var blend = Math.Clamp((q.X - q.Y) * .5 + .5, 0, 1);
        var flatNormal = new Vector(blend, 1 - blend);
        flatNormal.Normalize();
        var normal = length > .0001
            ? new Vector(outside.X / length * (v.X < 0 ? -1 : 1), outside.Y / length * (v.Y < 0 ? -1 : 1))
            : new Vector(flatNormal.X * (v.X < 0 ? -1 : 1), flatNormal.Y * (v.Y < 0 ? -1 : 1));
        return (distance, normal);
    }

    internal static (Vector Offset, double Coverage) Sample(Point p, Size size, double radius)
    {
        var metrics = GlassMetrics.For(size, false);
        var bezel = metrics.Bezel;
        if (bezel <= 0 || Surface(p, size, radius).Distance < 0) return (new Vector(), 0);
        var (distance, normal) = Surface(p, size, metrics.OpticalRadius(radius));
        if (distance >= bezel) return (new Vector(), 0);
        var profile = ProfileAt(distance / bezel);
        return (-normal * (metrics.Displacement * profile.Shift), profile.Coverage);
    }
}
