using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace PaperTodo;

// A file document resolves local images relative to its own directory. Imported images
// stay in this run's recovery folder until an explicit document save publishes them.
internal sealed class StandaloneStudioImages(string assetsDirectory) : IMarkdownImageSource
{
    internal const string OwnerId = "standalone-studio";
    private readonly Dictionary<string, NoteImageAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _corrupted = new(StringComparer.OrdinalIgnoreCase);
    private string? _documentPath;
    public bool UsesFilePaths => true;

    internal string? DocumentPath
    {
        get => _documentPath;
        set { _documentPath = value; _assets.Clear(); _bitmaps.Clear(); _corrupted.Clear(); }
    }

    public bool TryParseReference(string line, out MarkdownImageReference reference)
    {
        reference = default;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("![", StringComparison.Ordinal) ||
            !MarkdownImageReferences.TrySplitMarkdownImage(trimmed, out var label, out var url, out var attributes) ||
            !TryResolvePath(url, out var path)) return false;
        reference = new MarkdownImageReference(path, 0, line.Length, label, attributes,
            MarkdownImageReferences.ParseDisplayOptions(label, attributes));
        return true;
    }

    internal bool TryResolvePath(string url, out string path)
    {
        path = "";
        url = url.Trim().Trim('<', '>');
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile) return false;
                path = Path.GetFullPath(uri.LocalPath);
            }
            else
            {
                var decoded = Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar);
                if (!Path.IsPathRooted(decoded) && _documentPath == null) return false;
                path = Path.GetFullPath(decoded, _documentPath == null
                    ? Environment.CurrentDirectory : Path.GetDirectoryName(_documentPath)!);
            }
            return NoteImageStore.IsSupportedImageFile(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException)
        {
            return false;
        }
    }

    public string CreateReference(string id) => $"![image|100%]({new Uri(id).AbsoluteUri})";

    public bool TryGetAsset(string id, out NoteImageAsset asset)
    {
        if (_assets.TryGetValue(id, out asset!)) return true;
        try
        {
            using var stream = File.OpenRead(id);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            asset = new NoteImageAsset
            {
                Id = id, NoteId = OwnerId, OriginalName = Path.GetFileName(id),
                Width = frame.PixelWidth, Height = frame.PixelHeight,
                ByteLength = checked((int)stream.Length), Mime = Mime(id)
            };
            _assets[id] = asset;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                                     System.IO.FileFormatException or ArgumentException or OverflowException)
        {
            _corrupted.Add(id);
            asset = null!;
            return false;
        }
    }

    public bool IsImageCorrupted(string id) => _corrupted.Contains(id);

    public BitmapSource? GetBitmapSource(string id, double targetPixelWidth = 0,
        bool allowDecodeUpgrade = true, bool protectInViewport = false)
    {
        var width = Math.Max(0, (int)Math.Ceiling(targetPixelWidth));
        if (_bitmaps.TryGetValue(id, out var cached) &&
            (!allowDecodeUpgrade || width > 0 && cached.PixelWidth >= width)) return cached;
        try
        {
            var bitmap = Decode(id, width);
            _bitmaps[id] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                                     System.IO.FileFormatException or ArgumentException)
        {
            _corrupted.Add(id);
            return null;
        }
    }

    public BitmapSource? GetBitmapSourceForClipboard(string id) => Decode(id, 0);
    public bool TryGetEncodedImageBytes(string id, out NoteImageAsset asset, out byte[] bytes)
    {
        bytes = [];
        if (!TryGetAsset(id, out asset)) return false;
        bytes = File.ReadAllBytes(id);
        return true;
    }

    public void SetViewportProtectedBitmapIds(string ownerKey, IReadOnlyCollection<string> imageIds)
    {
        if (_bitmaps.Count <= 20) return;
        var visible = imageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _bitmaps.Keys.Where(id => !visible.Contains(id)).ToArray()) _bitmaps.Remove(id);
    }

    public IReadOnlyList<NoteImageAsset> ImportImageFiles(string noteId, IEnumerable<string> paths)
    {
        var imported = new List<NoteImageAsset>();
        try
        {
            Directory.CreateDirectory(assetsDirectory);
            foreach (var path in paths)
            {
                if (!NoteImageStore.IsSupportedImageFile(path)) throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
                var target = Path.Combine(assetsDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant());
                File.Copy(path, target);
                if (!TryGetAsset(target, out var asset))
                {
                    File.Delete(target);
                    throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
                }
                imported.Add(asset);
            }
            return imported;
        }
        catch
        {
            foreach (var image in imported) { File.Delete(image.Id); _assets.Remove(image.Id); }
            throw;
        }
    }

    public NoteImageAsset ImportBitmapSource(string noteId, BitmapSource source, bool repairMissingAlpha)
    {
        Directory.CreateDirectory(assetsDirectory);
        var path = Path.Combine(assetsDirectory, Guid.NewGuid().ToString("N") + ".png");
        if (repairMissingAlpha) source = NoteImageStore.NormalizeVacuousAlpha(source);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var bytes = new MemoryStream())
        {
            encoder.Save(bytes);
            StandaloneStudioDocument.AtomicWriteBytes(path, bytes.ToArray());
        }
        if (TryGetAsset(path, out var asset)) return asset;
        File.Delete(path);
        throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
    }

    // Foreign i: IDs belong to PaperTodo's image database. They are ordinary source text
    // in a file document; never open the normal database to resolve or clone them.
    public string CloneForeignImageReferencesForNote(string noteId, string markdown) => markdown;

    internal PreparedSave PrepareSave(string text, string destination)
    {
        var created = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var folder = Path.GetFileNameWithoutExtension(destination) + ".assets";
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        try
        {
            // Use Markdig's image spans, including images embedded in a paragraph. File
            // persistence must not depend on whether the editor renders an image as a block.
            var syntax = MarkdownSemanticSnapshot.ParseDocument(text);
            foreach (var link in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(syntax)
                         .Where(link => link.IsImage && !string.IsNullOrEmpty(link.Url) && !link.UrlSpan.IsEmpty)
                         .GroupBy(link => (link.UrlSpan.Start, link.UrlSpan.End)).Select(group => group.First())
                         .OrderBy(link => link.UrlSpan.Start))
            {
                var start = link.UrlSpan.Start;
                var length = link.UrlSpan.Length;
                if (start < cursor || length <= 0 || start + length > text.Length ||
                    !TryResolvePath(link.Url!, out var source) || !IsTemporaryAsset(source)) continue;
                if (!replacements.TryGetValue(source, out var relative))
                {
                    var directory = Path.Combine(destinationDirectory, folder);
                    Directory.CreateDirectory(directory);
                    var fileName = Guid.NewGuid().ToString("N") + Path.GetExtension(source);
                    var target = Path.Combine(directory, fileName);
                    File.Copy(source, target);
                    created.Add(target);
                    relative = EscapeRelative(folder + "/" + fileName);
                    replacements[source] = relative;
                }
                builder.Append(text, cursor, start - cursor).Append(relative);
                cursor = start + length;
            }
            builder.Append(text, cursor, text.Length - cursor);
            return new PreparedSave(builder.ToString(), created);
        }
        catch
        {
            foreach (var path in created) TryDelete(path);
            throw;
        }
    }

    private bool IsTemporaryAsset(string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(assetsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static string EscapeRelative(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff", ".webp" => "image/webp", _ => "image/png"
    };
    private static BitmapSource Decode(string path, int width)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal sealed class PreparedSave(string text, List<string> files) : IDisposable
    {
        internal string Text { get; } = text;
        internal bool Committed { get; set; }
        public void Dispose() { if (!Committed) foreach (var path in files) TryDelete(path); }
    }
}
