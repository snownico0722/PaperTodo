using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

internal static class NoteBackground
{
    private const int MaxDecodePixelWidth = 4096;
    private static readonly string DirectoryPath =
        Path.Combine(AppContext.BaseDirectory, "custom", "note");
    private static readonly string DisabledMarkerPath =
        Path.Combine(DirectoryPath, "background.disabled");
    private static readonly string[] CandidateNames =
        ["background.png", "background.jpg", "background.jpeg"];

    private static BitmapSource? _cachedBitmap;
    private static ImageBrush? _cachedLightBrush;
    private static ImageBrush? _cachedDarkBrush;
    private static string? _cachedPath;
    private static long _cachedLength = -1;
    private static DateTime _cachedWriteTimeUtc;

    internal static bool IsAvailable => FindPath() != null;
    internal static bool IsEnabled => IsAvailable && !File.Exists(DisabledMarkerPath);

    internal static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                // Enabling is also used by "restore visual defaults". Clear a stale marker even
                // when the background image is temporarily absent.
                File.Delete(DisabledMarkerPath);
            }
            else
            {
                if (!IsAvailable)
                {
                    return;
                }

                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(DisabledMarkerPath, "disabled");
            }
        }
        catch
        {
            // Custom visual resources must never affect core note behavior.
        }
        finally
        {
            InvalidateCache();
        }
    }

    internal static void Apply(MarkdownTextBox? editor)
    {
        if (editor == null)
        {
            return;
        }

        editor.Background = CreateBrush() ?? Brushes.Transparent;
    }

    private static Brush? CreateBrush()
    {
        var path = IsEnabled ? FindPath() : null;
        if (path == null)
        {
            InvalidateCache();
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
                _cachedLightBrush = null;
                _cachedDarkBrush = null;
            }

            if (Theme.IsDark)
            {
                return _cachedDarkBrush ??= CreateImageBrush(_cachedBitmap!, 0.24);
            }

            return _cachedLightBrush ??= CreateImageBrush(_cachedBitmap!, 0.32);
        }
        catch
        {
            InvalidateCache();
            return null;
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.DecodePixelWidth = MaxDecodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageBrush CreateImageBrush(BitmapSource bitmap, double opacity)
    {
        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
            // Keep the current paper palette visible below the image so light/dark themes retain
            // their intended text contrast without introducing a second color system.
            Opacity = opacity
        };
        brush.Freeze();
        return brush;
    }

    private static void InvalidateCache()
    {
        _cachedBitmap = null;
        _cachedLightBrush = null;
        _cachedDarkBrush = null;
        _cachedPath = null;
        _cachedLength = -1;
        _cachedWriteTimeUtc = default;
    }

    private static string? FindPath()
    {
        foreach (var name in CandidateNames)
        {
            var path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}
