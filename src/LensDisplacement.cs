using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>A rounded, shallow dielectric lens. The displacement follows Snell's law
/// through a curved shoulder; the center stays almost flat so body text remains useful.
/// Only the displacement map is generated on resize, never the user's desktop image.</summary>
internal static class LensDisplacement
{
    internal const double IndexOfRefraction = 1.46;
    internal const double MaxShiftDip = 24;

    internal static (Vector Offset, double Fresnel) Sample(Point p, Size size, double radius)
    {
        var half = new Vector(size.Width / 2, size.Height / 2);
        var v = new Vector(p.X - half.X, p.Y - half.Y);
        radius = Math.Clamp(radius, 0, Math.Min(half.X, half.Y));
        var q = new Vector(Math.Abs(v.X) - half.X + radius, Math.Abs(v.Y) - half.Y + radius);
        var outside = new Vector(Math.Max(q.X, 0), Math.Max(q.Y, 0));
        var distance = -(outside.Length + Math.Min(Math.Max(q.X, q.Y), 0) - radius);
        Vector normal;
        if (outside.LengthSquared > .000001)
        {
            normal = outside; normal.Normalize();
            normal.X *= v.X < 0 ? -1 : 1; normal.Y *= v.Y < 0 ? -1 : 1;
        }
        else normal = q.X > q.Y ? new Vector(v.X < 0 ? -1 : 1, 0) : new Vector(0, v.Y < 0 ? -1 : 1);
        var shoulder = Math.Min(24, Math.Min(size.Width, size.Height) * .18);
        if (shoulder <= 0) return (new Vector(), 0);
        var f0 = Math.Pow((IndexOfRefraction - 1) / (IndexOfRefraction + 1), 2);
        if (distance >= shoulder)
            return (new Vector(Math.Clamp(-v.X * .010, -MaxShiftDip, MaxShiftDip),
                Math.Clamp(-v.Y * .010, -MaxShiftDip, MaxShiftDip)), f0);
        var u = Math.Clamp(1 - distance / shoulder, 0, .9995);
        var slope = .62 * u / Math.Sqrt(Math.Max(.0001, 1 - u * u));
        var nz = 1 / Math.Sqrt(1 + slope * slope);
        var nxy = normal * (slope * nz);
        var eta = 1 / IndexOfRefraction;
        var k = Math.Sqrt(1 - eta * eta * (1 - nz * nz)) - eta * nz;
        var rayXY = -k * nxy;
        var rayZ = -eta - k * nz;
        var thickness = 14 + 7 * Math.Sqrt(Math.Max(0, 1 - u * u));
        var displacement = rayXY * (-thickness / rayZ);
        // Mild center magnification, smoothly removed at the rim.
        displacement -= v * (.010 * Math.Clamp(distance / shoulder, 0, 1));
        displacement.X = Math.Clamp(displacement.X, -MaxShiftDip, MaxShiftDip);
        displacement.Y = Math.Clamp(displacement.Y, -MaxShiftDip, MaxShiftDip);
        var grazing = 1 - nz;
        var fresnel = f0 + (1 - f0) * grazing * grazing * grazing * grazing * grazing;
        return (displacement, fresnel);
    }

    internal static BitmapSource Create(Size dips, CornerRadius corners, DpiScale dpi)
    {
        // The map contains smoothly varying geometry, not desktop detail. Limit only this
        // map's resolution; the captured background remains native-resolution.
        var scale = Math.Min(1, 768 / Math.Max(dips.Width * dpi.DpiScaleX, dips.Height * dpi.DpiScaleY));
        var width = Math.Max(1, (int)Math.Ceiling(dips.Width * dpi.DpiScaleX * scale));
        var height = Math.Max(1, (int)Math.Ceiling(dips.Height * dpi.DpiScaleY * scale));
        var bytes = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = new Point((x + .5) * dips.Width / width, (y + .5) * dips.Height / height);
            var radius = y < height / 2
                ? (x < width / 2 ? corners.TopLeft : corners.TopRight)
                : (x < width / 2 ? corners.BottomLeft : corners.BottomRight);
            var (offset, fresnel) = Sample(p, dips, radius);
            var i = (y * width + x) * 4;
            bytes[i] = (byte)Math.Clamp((int)Math.Round(fresnel * 255), 0, 255);
            bytes[i + 1] = Encode(offset.Y); bytes[i + 2] = Encode(offset.X); bytes[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze(); return image;
    }
    private static byte Encode(double shift) => (byte)Math.Clamp((int)Math.Round(128 + shift / MaxShiftDip * 127), 1, 255);
}
