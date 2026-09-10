using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>A rounded, finite-thickness optical shoulder. The one-dimensional Snell
/// profile is shared by every size: no full-window displacement map or resize-time rays.
/// The center remains a direct view of the desktop, not a sampled magnifying sheet.</summary>
internal static class LensDisplacement
{
    internal const double BezelDip = 18;
    internal const double MaxShiftDip = 18;
    private const int ProfileSamples = 512;
    private static readonly Lazy<Brush> Profile = new(CreateProfile);
    internal static Brush ProfileBrush => Profile.Value;

    // Convex squircle cross-section: h(t) = (1 - (1-t)^4)^(1/4).
    // Ray travels air -> glass (IOR 1.5), then to a plane below the rounded shoulder.
    // This is an optical UI model, not a full multi-interface physical renderer.
    internal static (double Shift, double Slope, double Fresnel, double Coverage) ProfileAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t >= 1) return (0, 0, 0, 0);
        var u = 1 - t;
        var height = Math.Pow(Math.Max(0, 1 - u * u * u * u), .25);
        var derivative = u * u * u / Math.Max(.000001, height * height * height);
        var nz = 1 / Math.Sqrt(1 + derivative * derivative);
        var nx = derivative * nz;
        const double eta = 1 / 1.5;
        var k = eta * nz - Math.Sqrt(1 - eta * eta * (1 - nz * nz));
        var tx = k * nx;
        var tz = -eta + k * nz;
        // A small base thickness makes the edge visibly refractive, but without the
        // old discontinuous maximum-offset wall. The profile joins the center flat.
        var shift = (height + .12) * Math.Abs(tx / tz) * 1.55;
        var grazing = Math.Pow(1 - nz, 5);
        var coverage = Math.Clamp((1 - t) * 6, 0, 1);
        coverage = coverage * coverage * (3 - 2 * coverage);
        return (Math.Min(1, shift), nx, .04 + .96 * grazing, coverage);
    }

    private static Brush CreateProfile()
    {
        var bytes = new byte[ProfileSamples * 4];
        for (var x = 0; x < ProfileSamples; x++)
        {
            var p = ProfileAt(x / (double)(ProfileSamples - 1));
            // RGB carries independent coefficients; opaque alpha prevents WPF's
            // texture conversion from premultiplying these data channels.
            bytes[x * 4] = (byte)Math.Round(p.Fresnel * 255);
            bytes[x * 4 + 1] = (byte)Math.Round(p.Slope * 255);
            bytes[x * 4 + 2] = (byte)Math.Round(p.Shift * 255);
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
        var normal = length > .0001
            ? new Vector(outside.X / length * (v.X < 0 ? -1 : 1), outside.Y / length * (v.Y < 0 ? -1 : 1))
            : q.X > q.Y ? new Vector(v.X < 0 ? -1 : 1, 0) : new Vector(0, v.Y < 0 ? -1 : 1);
        return (distance, normal);
    }

    internal static (Vector Offset, double Coverage) Sample(Point p, Size size, double radius)
    {
        var (distance, normal) = Surface(p, size, radius);
        var bezel = Math.Min(BezelDip, Math.Min(size.Width, size.Height) / 2);
        if (bezel <= 0 || distance < 0 || distance >= bezel) return (new Vector(), 0);
        var profile = ProfileAt(distance / bezel);
        return (-normal * (MaxShiftDip * bezel / BezelDip * profile.Shift), profile.Coverage);
    }
}
