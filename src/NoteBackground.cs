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

    internal static bool IsAvailable => FindPath() != null;
    internal static bool IsEnabled => IsAvailable && !File.Exists(DisabledMarkerPath);

    internal static void SetEnabled(bool enabled)
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (enabled)
            {
                File.Delete(DisabledMarkerPath);
            }
            else
            {
                File.WriteAllText(DisabledMarkerPath, "disabled");
            }
        }
        catch
        {
            // Custom visual resources must never affect core note behavior.
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
            return null;
        }

        try
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

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                // Let the current paper palette remain visible under the image so light/dark
                // themes keep their intended text contrast without a second color system.
                Opacity = Theme.IsDark ? 0.24 : 0.32
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
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
