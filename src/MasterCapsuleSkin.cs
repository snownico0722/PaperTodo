using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

/// <summary>Frozen frames and stable artwork geometry. Playback never owns queue geometry.</summary>
internal sealed class MasterCapsuleVisualClip
{
    private BitmapSource[] _frames;
    internal MasterCapsuleVisualClip(BitmapSource[] frames)
    {
        if (frames.Length == 0) throw new ArgumentException("A visual clip needs a frame.", nameof(frames));
        _frames = frames;
        CanvasWidth = frames[0].PixelWidth;
        CanvasHeight = frames[0].PixelHeight;
    }
    internal int FrameCount => _frames.Length;
    internal BitmapSource FirstFrame => _frames[0];
    internal BitmapSource FrameAt(int index) => _frames[index];
    internal int CanvasWidth { get; }
    internal int CanvasHeight { get; }
    internal event Action? FramesChanged;
    private Func<MasterCapsuleVisualClip?>? _reload;
    private readonly Dictionary<object, Size> _displayRequests = new();
    private System.Windows.Threading.DispatcherTimer? _resizeTimer;

    internal void SetReload(Func<MasterCapsuleVisualClip?> reload) => _reload = reload;

    // The docked masters and floating cover share ONE raster, sized for their largest
    // visible physical-pixel demand. No per-frame decoding and no per-window frame caches.
    internal void RequestDisplaySize(object owner, Size? pixels)
    {
        if (pixels is { Width: > 0, Height: > 0 } size &&
            double.IsFinite(size.Width) && double.IsFinite(size.Height))
        {
            if (_displayRequests.TryGetValue(owner, out var previous) && previous == size) return;
            _displayRequests[owner] = size;
        }
        else if (!_displayRequests.Remove(owner)) return;

        _resizeTimer?.Stop();
        if (_displayRequests.Count == 0) return;
        _resizeTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(180), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { _resizeTimer!.Stop(); ApplyDisplaySize(); },
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _resizeTimer.Start();
    }

    internal void ApplyDisplaySize()
    {
        _resizeTimer?.Stop();
        if (_displayRequests.Count == 0) return;
        var scale = Math.Min(1, _displayRequests.Values.Max(size =>
            Math.Min(size.Width / CanvasWidth, size.Height / CanvasHeight)));
        var width = Math.Max(1, (int)Math.Ceiling(CanvasWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(CanvasHeight * scale));
        if (FirstFrame.PixelWidth == width && FirstFrame.PixelHeight == height) return;
        try
        {
            // Always resample the original decoded canvas, not an already reduced frame.
            // The initial full-resolution frames can be consumed directly and then released.
            var source = FirstFrame.PixelWidth == CanvasWidth && FirstFrame.PixelHeight == CanvasHeight
                ? this : _reload?.Invoke();
            if (source == null || source.CanvasWidth != CanvasWidth ||
                source.CanvasHeight != CanvasHeight || source.FrameCount != FrameCount) return;
            var frames = new BitmapSource[FrameCount];
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            for (var i = 0; i < frames.Length; i++)
            {
                BitmapSource frame = source.FrameAt(i);
                if (width != CanvasWidth || height != CanvasHeight)
                    frame = new TransformedBitmap(frame,
                        new ScaleTransform((double)width / CanvasWidth, (double)height / CanvasHeight));
                frame.CopyPixels(pixels, stride, 0);
                frames[i] = BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, stride);
                frames[i].Freeze();
            }
            _frames = frames;
            FramesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Optional artwork can disappear between startup and a settings/DPI change.
            // Keep the already playable raster; no retry loop or hot-reload watcher.
            Trace.TraceWarning("Master capsule raster resize skipped: {0}", ex.Message);
        }
    }
}

internal sealed record MasterCapsuleVisualSet(
    MasterCapsuleVisualClip? Normal,
    MasterCapsuleVisualClip? Drag)
{
    internal MasterCapsuleVisualClip? DragOrNormal => Drag ?? Normal;
}

/// <summary>
/// Optional artwork beside PaperTodo.exe, discovered once per process. Static images, GIFs and
/// numbered images share one clip. Resizing may reread the selected source; playback never does IO.
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
        DisplayWidthDip(image.PixelWidth, image.PixelHeight, size);

    internal static double DisplayWidthDip(
        MasterCapsuleVisualClip clip, int size = MasterCapsuleSizes.Default) =>
        DisplayWidthDip(clip.CanvasWidth, clip.CanvasHeight, size);

    private static double DisplayWidthDip(int width, int height, int size) =>
        Math.Clamp(
            Math.Ceiling(PaperLayoutDefaults.CapsuleHeight * (double)width / height),
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
        byte[]? pixels = null;
        AlphaBounds? bounds = null;
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
            BitmapSource converted = bitmap.Format == PixelFormats.Pbgra32 ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
            var stride = checked(size.Width * 4);
            pixels ??= new byte[checked(stride * size.Height)];
            bounds ??= new AlphaBounds(size.Width, size.Height);
            converted.CopyPixels(pixels, stride, 0);
            bounds.Include(pixels, stride);
            var normalized = BitmapSource.Create(size.Width, size.Height, 96, 96,
                PixelFormats.Pbgra32, null, pixels, stride);
            normalized.Freeze();
            frames[index] = normalized;
        }
        return FinishClip(frames, bounds!, paths, () => DecodeSequence(paths));
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
        var stride = checked(size.Width * 4);
        var pixels = new byte[checked(stride * size.Height)];
        var bounds = new AlphaBounds(size.Width, size.Height);
        for (var index = 0; index < count; index++)
        {
            image.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, index);
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(image, rectangle, 0, 0, image.Width, image.Height, System.Drawing.GraphicsUnit.Pixel);
            var data = canvas.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                if (data.Stride == stride)
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                else
                    for (var y = 0; y < size.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                bounds.Include(pixels, stride);
                var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, size.Width * 4);
                bitmap.Freeze();
                frames[index] = bitmap;
            }
            finally { canvas.UnlockBits(data); }
        }
        // File delays and finite repeat counts intentionally do not drive this feature: the
        // user's common frames-per-second setting loops GIF and numbered frames identically.
        return FinishClip(frames, bounds, [path], () => DecodeGif(path));
    }

    private static MasterCapsuleVisualClip? FinishClip(BitmapSource[] frames, AlphaBounds bounds,
        string[] paths, Func<MasterCapsuleVisualClip?> reload)
    {
        if (bounds.Empty) return null;
        var crop = bounds.Rectangle;
        var width = frames[0].PixelWidth;
        var height = frames[0].PixelHeight;
        if (crop.Width != width || crop.Height != height)
        {
            // Materialize useful crops so the original large bitmaps can be collected.
            // Tiny crops keep the cheap view until display-sized rasterization detaches it.
            var detach = (long)crop.Width * crop.Height * 4 <= (long)width * height * 3;
            var stride = checked(crop.Width * 4);
            var pixels = detach ? new byte[checked(stride * crop.Height)] : null;
            for (var index = 0; index < frames.Length; index++)
            {
                if (pixels != null)
                {
                    frames[index].CopyPixels(crop, pixels, stride, 0);
                    frames[index] = BitmapSource.Create(crop.Width, crop.Height, 96, 96,
                        PixelFormats.Pbgra32, null, pixels, stride);
                }
                else frames[index] = new CroppedBitmap(frames[index], crop);
                frames[index].Freeze();
            }
        }
        var clip = new MasterCapsuleVisualClip(frames);
        // Replacement still takes effect on restart. Keep only source identities, not
        // duplicate full-size frames or every compressed sequence file in memory.
        var versions = paths.Select(path =>
        {
            var file = new FileInfo(path);
            return (file.Length, file.LastWriteTimeUtc);
        }).ToArray();
        clip.SetReload(() =>
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var file = new FileInfo(paths[i]);
                if (!file.Exists || (file.Length, file.LastWriteTimeUtc) != versions[i]) return null;
            }
            return reload();
        });
        return clip;
    }

    // Inspect the Pbgra32 alpha bytes while they are already available. Once the
    // union covers the canvas, later frames cannot change the common crop.
    private sealed class AlphaBounds
    {
        private readonly int _width, _height;
        private int _left, _top, _right = -1, _bottom = -1;
        internal AlphaBounds(int width, int height)
        {
            _left = _width = width;
            _top = _height = height;
        }
        internal bool Empty => _right < _left;
        internal Int32Rect Rectangle => new(_left, _top, _right - _left + 1, _bottom - _top + 1);
        internal void Include(byte[] pixels, int stride)
        {
            if (_left == 0 && _top == 0 && _right == _width - 1 && _bottom == _height - 1) return;
            for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                // Pixels inside the established union cannot expand its bounding box.
                if (y >= _top && y <= _bottom && x >= _left && x <= _right)
                {
                    x = _right;
                    continue;
                }
                if (pixels[y * stride + x * 4 + 3] <= VisibleAlphaThreshold) continue;
                _left = Math.Min(_left, x); _top = Math.Min(_top, y);
                _right = Math.Max(_right, x); _bottom = Math.Max(_bottom, y);
            }
        }
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
