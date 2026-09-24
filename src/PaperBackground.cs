using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

internal static class PaperBackgroundLayouts
{
    internal const string Center = "center";
    internal const string BottomLeft = "bottomLeft";
    internal const string BottomCenter = "bottomCenter";
    internal const string BottomRight = "bottomRight";

    internal static string Normalize(string? layout) => layout switch
    {
        BottomLeft => BottomLeft,
        BottomCenter => BottomCenter,
        BottomRight => BottomRight,
        _ => Center
    };
}

internal static class PaperBackground
{
    private sealed class BackgroundPreferences
    {
        public BackgroundPreferences()
        {
        }

        public bool BlendWithTheme { get; set; } = true;
        public bool Stretch { get; set; }
        public string Layout { get; set; } = PaperBackgroundLayouts.Center;
    }

    private const int MaxDecodePixelDimension = 4096;
    private const double BlendedImageOpacity = 0.30;
    private static readonly string[] CandidateNames =
        ["papertodo.png", "papertodo.jpg", "papertodo.jpeg"];
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PaperTodo",
        "paper-background.json");

    private static BackgroundPreferences _preferences = LoadPreferences();
    private static BitmapSource? _cachedBitmap;
    private static string? _cachedPath;
    private static long _cachedLength = -1;
    private static DateTime _cachedWriteTimeUtc;
    private static string? _lastLoadError;

    internal static bool IsAvailable => FindPath() != null;
    internal static bool BlendWithTheme => _preferences.BlendWithTheme;
    internal static bool StretchImage => _preferences.Stretch;
    internal static string Layout => PaperBackgroundLayouts.Normalize(_preferences.Layout);

    internal static string? LoadError
    {
        get
        {
            if (!IsAvailable)
            {
                _lastLoadError = null;
                return null;
            }

            _ = CreateBrush(BlendWithTheme, Layout, StretchImage);
            return _lastLoadError;
        }
    }

    internal static void SetBlendWithTheme(bool enabled)
    {
        if (_preferences.BlendWithTheme == enabled)
        {
            return;
        }

        var next = new BackgroundPreferences
        {
            BlendWithTheme = enabled,
            Stretch = StretchImage,
            Layout = Layout
        };
        SavePreferences(next);
        _preferences = next;
        InvalidateCache();
    }

    internal static void SetStretch(bool enabled)
    {
        if (StretchImage == enabled)
        {
            return;
        }

        var next = new BackgroundPreferences
        {
            BlendWithTheme = BlendWithTheme,
            Stretch = enabled,
            Layout = Layout
        };
        SavePreferences(next);
        _preferences = next;
        InvalidateCache();
    }

    internal static void SetLayout(string layout)
    {
        var normalized = PaperBackgroundLayouts.Normalize(layout);
        if (string.Equals(Layout, normalized, StringComparison.Ordinal))
        {
            return;
        }

        var next = new BackgroundPreferences
        {
            BlendWithTheme = BlendWithTheme,
            Stretch = StretchImage,
            Layout = normalized
        };
        SavePreferences(next);
        _preferences = next;
        InvalidateCache();
    }

    internal static void ResetPreferences()
    {
        var next = new BackgroundPreferences();
        SavePreferences(next);
        _preferences = next;
        InvalidateCache();
    }

    internal static void Apply(Panel? host)
    {
        if (host != null)
        {
            host.Background = (Brush?)CreateBrush(
                BlendWithTheme,
                Layout,
                StretchImage,
                host.ActualWidth,
                host.ActualHeight) ?? Brushes.Transparent;
        }
    }

    internal static void Apply(Control? host)
    {
        if (host != null)
        {
            host.Background = (Brush?)CreateBrush(
                BlendWithTheme,
                Layout,
                StretchImage,
                host.ActualWidth,
                host.ActualHeight) ?? Brushes.Transparent;
        }
    }

    internal static bool NeedsSizeRefresh(Brush? currentBrush, double availableWidth, double availableHeight)
    {
        if (StretchImage ||
            currentBrush is not ImageBrush imageBrush ||
            imageBrush.ImageSource is not BitmapSource bitmap)
        {
            return false;
        }

        return imageBrush.Stretch != ResolveStretch(
            stretch: false,
            bitmap,
            availableWidth,
            availableHeight);
    }

    internal static ImageBrush? CreateBrush(
        bool blendWithTheme,
        string layout,
        bool stretch,
        double availableWidth = double.NaN,
        double availableHeight = double.NaN)
    {
        var path = FindPath();
        if (path == null)
        {
            InvalidateCache();
            _lastLoadError = null;
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            var length = info.Length;
            var writeTimeUtc = info.LastWriteTimeUtc;
            var cacheMatches =
                _cachedBitmap != null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedLength == length &&
                _cachedWriteTimeUtc == writeTimeUtc;

            if (!cacheMatches)
            {
                _cachedBitmap = LoadBitmap(path);
                _cachedPath = path;
                _cachedLength = length;
                _cachedWriteTimeUtc = writeTimeUtc;
            }

            var brush = new ImageBrush(_cachedBitmap!)
            {
                Opacity = blendWithTheme ? BlendedImageOpacity : 1.0
            };
            ApplyLayout(
                brush,
                PaperBackgroundLayouts.Normalize(layout),
                stretch,
                _cachedBitmap!,
                availableWidth,
                availableHeight);
            brush.Freeze();
            _lastLoadError = null;
            return brush;
        }
        catch (Exception ex)
        {
            InvalidateCache();
            _lastLoadError = ex.Message;
            return null;
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var (pixelWidth, pixelHeight) = ReadPixelSize(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.None;
        if (pixelWidth > MaxDecodePixelDimension ||
            pixelHeight > MaxDecodePixelDimension)
        {
            if (pixelWidth >= pixelHeight)
            {
                bitmap.DecodePixelWidth = MaxDecodePixelDimension;
            }
            else
            {
                bitmap.DecodePixelHeight = MaxDecodePixelDimension;
            }
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height) ReadPixelSize(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The background image contains no decodable frame.");
        }
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static void ApplyLayout(
        ImageBrush brush,
        string layout,
        bool stretch,
        BitmapSource bitmap,
        double availableWidth,
        double availableHeight)
    {
        brush.Stretch = ResolveStretch(
            stretch,
            bitmap,
            availableWidth,
            availableHeight);
        brush.AlignmentX = layout switch
        {
            PaperBackgroundLayouts.BottomLeft => AlignmentX.Left,
            PaperBackgroundLayouts.BottomRight => AlignmentX.Right,
            _ => AlignmentX.Center
        };
        brush.AlignmentY = layout switch
        {
            PaperBackgroundLayouts.BottomLeft or
            PaperBackgroundLayouts.BottomCenter or
            PaperBackgroundLayouts.BottomRight => AlignmentY.Bottom,
            _ => AlignmentY.Center
        };
    }

    private static Stretch ResolveStretch(
        bool stretch,
        BitmapSource bitmap,
        double availableWidth,
        double availableHeight)
    {
        if (stretch)
        {
            // Fill the paper while preserving the source aspect ratio.
            return Stretch.UniformToFill;
        }

        if (!double.IsFinite(availableWidth) ||
            !double.IsFinite(availableHeight) ||
            availableWidth <= 0 ||
            availableHeight <= 0)
        {
            return Stretch.None;
        }

        var imageWidth = bitmap.Width;
        var imageHeight = bitmap.Height;
        if (!double.IsFinite(imageWidth) ||
            !double.IsFinite(imageHeight) ||
            imageWidth <= 0 ||
            imageHeight <= 0)
        {
            return Stretch.None;
        }

        // Small images stay at their native size. Oversized images are only
        // scaled down, uniformly, until the whole image fits inside the paper.
        return imageWidth > availableWidth || imageHeight > availableHeight
            ? Stretch.Uniform
            : Stretch.None;
    }

    private static BackgroundPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return new BackgroundPreferences();
            }

            var loaded = JsonSerializer.Deserialize<BackgroundPreferences>(
                File.ReadAllText(PreferencesPath));
            if (loaded == null)
            {
                return new BackgroundPreferences();
            }

            loaded.Layout = PaperBackgroundLayouts.Normalize(loaded.Layout);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BackgroundPreferences();
        }
    }

    private static void SavePreferences(BackgroundPreferences preferences)
    {
        var directory = Path.GetDirectoryName(PreferencesPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = PreferencesPath + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, PreferencesPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original settings write failure.
            }
            throw;
        }
    }

    private static void InvalidateCache()
    {
        _cachedBitmap = null;
        _cachedPath = null;
        _cachedLength = -1;
        _cachedWriteTimeUtc = default;
    }

    private static string? FindPath()
    {
        foreach (var name in CandidateNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}