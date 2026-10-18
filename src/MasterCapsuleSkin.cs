using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>
/// Startup-only master-capsule artwork beside PaperTodo.exe. This is presentation only;
/// there is no secondary visual owner, separate settings store, timer or file watcher.
/// </summary>
internal static class MasterCapsuleSkin
{
    private const long MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxDecodeDimension = 1024;
    private const int VisibleAlphaThreshold = 8;
    private const double MaxWidthDip = 128;

    private static readonly Lazy<BitmapSource?> StartupSkin =
        new(() => LoadFromDirectory(AppContext.BaseDirectory));

    internal static BitmapSource? Current => StartupSkin.Value;

    internal static double DisplayWidthDip(BitmapSource image) =>
        Math.Clamp(
            Math.Ceiling(PaperLayoutDefaults.CapsuleHeight * (double)image.PixelWidth / image.PixelHeight),
            16,
            MaxWidthDip);

    // PNG takes precedence. A corrupt PNG does not prevent trying WebP.
    // WebP follows the existing system-WIC codec policy of Note images.
    internal static BitmapSource? LoadFromDirectory(string directory)
    {
        foreach (var name in new[] { "papertodomain.png", "papertodomain.webp" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                continue;

            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxFileBytes)
                    throw new InvalidDataException("Master-capsule skin file size is unsupported.");

                var bitmap = Decode(path);
                var trimmed = TrimTransparentPadding(bitmap);
                if (trimmed != null)
                    return trimmed;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning("Master capsule skin ignored ({0}): {1}", name, ex.Message);
            }
        }
        return null;
    }

    private static BitmapSource Decode(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Master-capsule skin contains no image frame.");

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            throw new InvalidDataException("Master-capsule skin dimensions are invalid.");

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth > MaxDecodeDimension || frame.PixelHeight > MaxDecodeDimension)
        {
            if (frame.PixelWidth >= frame.PixelHeight)
                bitmap.DecodePixelWidth = MaxDecodeDimension;
            else
                bitmap.DecodePixelHeight = MaxDecodeDimension;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? TrimTransparentPadding(BitmapSource source)
    {
        // AI-created edge stickers often contain large transparent margins.
        // The alpha bounds, not the original canvas, determine master width.
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);

        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] <= VisibleAlphaThreshold)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        if (right < left || bottom < top)
            return null;
        if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
            return source;

        var cropped = new CroppedBitmap(
            source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }
}
