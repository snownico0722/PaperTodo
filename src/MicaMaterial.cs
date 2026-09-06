using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>
/// Wallpaper-derived, opaque Mica-style material for our layered WPF windows.
/// This is not a DWM system backdrop: the existing WPF tree still owns rounded
/// shapes, opacity and hit testing. Never capture the desktop or windows behind us.
/// </summary>
internal sealed class MicaMaterial
{
    internal const int SampleSize = 64;
    private const long MaxWallpaperBytes = 64L * 1024 * 1024;
    internal static Color LightBase => Color.FromRgb(243, 243, 247);
    internal static Color DarkBase => Color.FromRgb(32, 33, 40);

    public ImageBrush Light { get; }
    public ImageBrush Dark { get; }

    private MicaMaterial(ImageBrush light, ImageBrush dark)
    {
        Light = light;
        Dark = dark;
    }

    public static MicaMaterial? LoadDesktopWallpaper()
    {
        var path = new StringBuilder(260);
        return SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, path.Capacity, path, 0)
            ? LoadFile(path.ToString())
            : null;
    }

    internal static MicaMaterial? LoadFile(string path)
    {
        // Empty means a solid desktop. Do not reuse a stale TranscodedWallpaper file.
        // Only the OS-reported local image is read; there is no HTTP/UNC fetch path.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0 || stream.Length > MaxWallpaperBytes)
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            // Deliberately keep only a small tonal field, not wallpaper details or
            // a screen-position-accurate image. Both axes are bounded, including portraits.
            image.DecodePixelWidth = SampleSize;
            image.DecodePixelHeight = SampleSize;
            image.EndInit();
            image.Freeze();
            return FromBitmap(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            SecurityException or NotSupportedException or ArgumentException or
            FormatException or COMException)
        {
            // A decorative skin must not make startup or a wallpaper change fail.
            return null;
        }
    }

    internal static MicaMaterial FromBitmap(BitmapSource source)
    {
        // Also bound callers other than LoadFile (e.g. a generated test image).
        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)SampleSize / source.PixelWidth, (double)SampleSize / source.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[SampleSize * SampleSize * 4];
        converted.CopyPixels(pixels, SampleSize * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255.0;
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[i + channel] = (byte)(pixels[i + channel] * alpha + 128 * (1 - alpha));
            }
            pixels[i + 3] = 255;
        }

        // Three bounded separable box passes approximate a broad Gaussian blur.
        // Work happens once on a worker, never as a WPF BlurEffect or per-frame copy.
        for (var pass = 0; pass < 3; pass++)
        {
            pixels = Blur(pixels, horizontal: true);
            pixels = Blur(pixels, horizontal: false);
        }
        return new MicaMaterial(CreateBrush(pixels, false), CreateBrush(pixels, true));
    }

    private static byte[] Blur(byte[] source, bool horizontal)
    {
        const int radius = 6;
        var result = new byte[source.Length];
        for (var y = 0; y < SampleSize; y++)
        for (var x = 0; x < SampleSize; x++)
        {
            var target = (y * SampleSize + x) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sx = horizontal ? Math.Clamp(x + offset, 0, SampleSize - 1) : x;
                    var sy = horizontal ? y : Math.Clamp(y + offset, 0, SampleSize - 1);
                    sum += source[(sy * SampleSize + sx) * 4 + channel];
                }
                result[target + channel] = (byte)(sum / (radius * 2 + 1));
            }
            result[target + 3] = 255;
        }
        return result;
    }

    private static ImageBrush CreateBrush(byte[] blurred, bool dark)
    {
        var tint = dark ? DarkBase : LightBase;
        var weight = dark ? 0.16 : 0.18;
        var pixels = new byte[blurred.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = blurred[i] * 0.0722 + blurred[i + 1] * 0.7152 + blurred[i + 2] * 0.2126;
            // Desaturate before tinting so bright wallpapers cannot overpower the text.
            pixels[i] = Blend(blurred[i], tint.B, luminance, weight);
            pixels[i + 1] = Blend(blurred[i + 1], tint.G, luminance, weight);
            pixels[i + 2] = Blend(blurred[i + 2], tint.R, luminance, weight);
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(SampleSize, SampleSize, 96, 96,
            PixelFormats.Bgra32, null, pixels, SampleSize * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static byte Blend(byte wallpaper, byte tint, double luminance, double weight) =>
        (byte)Math.Round(tint * (1 - weight) + (wallpaper * 0.75 + luminance * 0.25) * weight);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(int action, int size, StringBuilder value, int flags);
}
