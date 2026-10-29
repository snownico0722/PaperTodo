using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>Fully decoded, frozen frames on one shared canvas. Never owns a window or a clock.</summary>
internal sealed class MasterCapsuleVisualClip
{
    private readonly BitmapSource[] _frames;
    internal MasterCapsuleVisualClip(BitmapSource[] frames)
    {
        if (frames.Length == 0) throw new ArgumentException("A visual clip needs a frame.", nameof(frames));
        _frames = frames;
    }
    internal int FrameCount => _frames.Length;
    internal BitmapSource FirstFrame => _frames[0];
    internal BitmapSource FrameAt(int index) => _frames[index];
}

internal sealed record MasterCapsuleVisualSet(
    MasterCapsuleVisualClip? Normal,
    MasterCapsuleVisualClip? Drag)
{
    internal MasterCapsuleVisualClip? DragOrNormal => Drag ?? Normal;
}

/// <summary>
/// Optional artwork beside PaperTodo.exe, discovered once per process. Static images, GIFs and
/// numbered images become the same immutable clip; no file watcher, decoder or IO runs during playback.
/// </summary>
internal static class MasterCapsuleSkin
{
    private const long MaxFileBytes = 16 * 1024 * 1024;
    private const long MaxClipBytes = 64 * 1024 * 1024;
    private const int MaxDecodeDimension = 1024;
    private const int MaxGifFrames = 512;
    private const int VisibleAlphaThreshold = 8;
    private const double MaxWidthDip = 128;
    private static readonly Lazy<MasterCapsuleVisualSet> StartupVisuals =
        new(() => LoadVisualsFromDirectory(AppContext.BaseDirectory));

    internal static MasterCapsuleVisualSet Visuals => StartupVisuals.Value;
    // Geometry uses the NORMAL clip's common canvas, never the current frame or drag pose.
    internal static BitmapSource? Current => Visuals.Normal?.FirstFrame;

    internal static double DisplayWidthDip(
        BitmapSource image, int size = MasterCapsuleSizes.Default) =>
        Math.Clamp(
            Math.Ceiling(PaperLayoutDefaults.CapsuleHeight * (double)image.PixelWidth / image.PixelHeight),
            16,
            MaxWidthDip) * MasterCapsuleSizes.Scale(size);

    internal static MasterCapsuleVisualSet LoadVisualsFromDirectory(string directory) =>
        new(LoadClip(directory, "papertodomain"), LoadClip(directory, "papertodomaindrag"));

    // Retains the static-image inspection entry point used by existing lifecycle checks.
    internal static BitmapSource? LoadFromDirectory(string directory) =>
        LoadClip(directory, "papertodomain")?.FirstFrame;

    internal static MasterCapsuleVisualClip? LoadClip(string directory, string stem)
    {
        if (!Directory.Exists(directory)) return null;
        // A broken higher-priority candidate must not shadow a usable lower-priority candidate.
        var gif = TryLoad(stem + ".gif", () =>
        {
            var path = Path.Combine(directory, stem + ".gif");
            return File.Exists(path) ? DecodeGif(path) : null;
        });
        if (gif != null) return gif;
        foreach (var extension in new[] { ".png", ".webp" })
        {
            var sequence = TryLoad(stem + "1..99" + extension, () =>
            {
                var paths = FindSequence(directory, stem, extension);
                return paths.Length == 0 ? null : DecodeSequence(paths);
            });
            if (sequence != null) return sequence;
        }
        foreach (var extension in new[] { ".png", ".webp" })
        {
            var still = TryLoad(stem + extension, () =>
            {
                var path = Path.Combine(directory, stem + extension);
                return File.Exists(path) ? DecodeSequence([path]) : null;
            });
            if (still != null) return still;
        }
        return null;
    }

    private static MasterCapsuleVisualClip? TryLoad(string name, Func<MasterCapsuleVisualClip?> load)
    {
        try { return load(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("Master capsule artwork ignored ({0}): {1}", name, ex.Message);
            return null;
        }
    }

    internal static string[] FindSequence(string directory, string stem, string extension)
    {
        // Numeric ordering, gaps allowed, no format mixing. For duplicate numeric indices the
        // shortest spelling wins (1 before 01 before 001), then ordinal name breaks ties.
        return Directory.EnumerateFiles(directory)
            .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .Where(item => item.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Path, Digits: item.Name[stem.Length..]))
            .Where(item => item.Digits.Length is > 0 and <= 3 && item.Digits.All(char.IsAsciiDigit))
            .Select(item => (item.Path, Number: int.Parse(item.Digits, System.Globalization.CultureInfo.InvariantCulture)))
            .Where(item => item.Number is >= 1 and <= 99)
            .OrderBy(item => item.Number)
            .ThenBy(item => Path.GetFileName(item.Path).Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .GroupBy(item => item.Number)
            .Select(group => group.First().Path)
            .ToArray();
    }

    private static FileStream OpenImage(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > MaxFileBytes)
        {
            stream.Dispose();
            throw new InvalidDataException("Artwork file size is unsupported.");
        }
        return stream;
    }

    private static (int Width, int Height) DecodeSize(int width, int height, int frameCount)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16 * 1024 * 1024)
            throw new InvalidDataException("Artwork canvas dimensions are unsupported.");
        // Bound the WHOLE decoded clip, not merely each of its many frames.
        var scale = Math.Min(1, Math.Min(
            MaxDecodeDimension / (double)Math.Max(width, height),
            Math.Sqrt(MaxClipBytes / (4.0 * frameCount * width * height))));
        return (Math.Max(1, (int)Math.Floor(width * scale)),
                Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private static MasterCapsuleVisualClip? DecodeSequence(string[] paths)
    {
        var frames = new BitmapSource[paths.Length];
        var sourceWidth = 0;
        var sourceHeight = 0;
        for (var index = 0; index < paths.Length; index++)
        {
            using var stream = OpenImage(paths[index]);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("Artwork has no image frame.");
            var frame = decoder.Frames[0];
            if (index == 0) (sourceWidth, sourceHeight) = (frame.PixelWidth, frame.PixelHeight);
            if (frame.PixelWidth != sourceWidth || frame.PixelHeight != sourceHeight)
                throw new InvalidDataException("Numbered frames must use the same canvas size.");
            var size = DecodeSize(sourceWidth, sourceHeight, paths.Length);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = size.Width;
            bitmap.DecodePixelHeight = size.Height;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            // File DPI is printing metadata, not the animation canvas contract. Normalize
            // after bounded decoding so equal pixel canvases also have equal WPF DIP sizes.
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
            var stride = checked(size.Width * 4);
            var pixels = new byte[checked(stride * size.Height)];
            converted.CopyPixels(pixels, stride, 0);
            var normalized = BitmapSource.Create(size.Width, size.Height, 96, 96,
                PixelFormats.Pbgra32, null, pixels, stride);
            normalized.Freeze();
            frames[index] = normalized;
        }
        return TrimTogether(frames);
    }

    private static System.Drawing.Image OpenGifImage(Stream stream)
    {
        try { return System.Drawing.Image.FromStream(stream, false, false); }
        catch (OutOfMemoryException ex)
        {
            // GDI+ also reports invalid image formats as OutOfMemoryException. Only translate
            // that decoder boundary; real managed frame-buffer allocation failures still escape.
            throw new InvalidDataException("GDI+ could not decode the optional GIF artwork.", ex);
        }
    }

    private static MasterCapsuleVisualClip? DecodeGif(string path)
    {
        using var stream = OpenImage(path);
        // GDI+ already belongs to the WindowsDesktop framework used by this application. Its
        // active-frame decoder composites GIF offsets, transparency and disposal operations;
        // drawing WIC's raw delta frames as independent full images would leave trails/holes.
        // No ImageAnimator is started: our own small player is the sole animation clock.
        using var image = OpenGifImage(stream);
        if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Gif.Guid)
            throw new InvalidDataException("The .gif candidate is not a GIF image.");
        var count = image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
        if (count is < 1 or > MaxGifFrames)
            throw new InvalidDataException("GIF frame count is unsupported.");
        var size = DecodeSize(image.Width, image.Height, count);
        var frames = new BitmapSource[count];
        using var canvas = new System.Drawing.Bitmap(size.Width, size.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = System.Drawing.Graphics.FromImage(canvas);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        var rectangle = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
        for (var index = 0; index < count; index++)
        {
            image.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, index);
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(image, rectangle, 0, 0, image.Width, image.Height, System.Drawing.GraphicsUnit.Pixel);
            var data = canvas.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = new byte[checked(size.Width * size.Height * 4)];
                for (var y = 0; y < size.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels,
                        y * size.Width * 4, size.Width * 4);
                var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, size.Width * 4);
                bitmap.Freeze();
                frames[index] = bitmap;
            }
            finally { canvas.UnlockBits(data); }
        }
        // File delays and finite repeat counts intentionally do not drive this feature: the
        // user's common frames-per-second setting loops GIF and numbered frames identically.
        return TrimTogether(frames);
    }

    private static MasterCapsuleVisualClip? TrimTogether(BitmapSource[] frames)
    {
        var width = frames[0].PixelWidth;
        var height = frames[0].PixelHeight;
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        foreach (var source in frames)
        {
            if (source.PixelWidth != width || source.PixelHeight != height)
                throw new InvalidDataException("Animation frame canvases must match.");
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(pixels, stride, 0);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] <= VisibleAlphaThreshold) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top) return null;
        if (left != 0 || top != 0 || right != width - 1 || bottom != height - 1)
        {
            var crop = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
            for (var index = 0; index < frames.Length; index++)
            {
                var cropped = new CroppedBitmap(frames[index], crop);
                cropped.Freeze();
                frames[index] = cropped;
            }
        }
        return new MasterCapsuleVisualClip(frames);
    }
}

internal static class MasterCapsuleSizes
{
    internal const int Minimum = 1;
    internal const int Default = 10;
    internal const int Maximum = 50;
    internal static int Normalize(int size) => Math.Clamp(size, Minimum, Maximum);
    internal static double Scale(int size) => Normalize(size) / (double)Default;
    internal static double HeightDip(int size) => PaperLayoutDefaults.CapsuleHeight * Scale(size);
    internal static double ExtraHeightDip(int size) => Math.Max(0, HeightDip(size) - PaperLayoutDefaults.CapsuleHeight);
}

internal static class MasterCapsuleAnimationRates
{
    internal const int Minimum = 1;
    internal const int Default = 12;
    internal const int Maximum = 30;
    internal static int Normalize(int value) => Math.Clamp(value, Minimum, Maximum);
}
