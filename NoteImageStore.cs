using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed class NoteImageStore
{
    private const int StoreVersion = 1;
    private const int MaxStoredDimension = 4096;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxInputImageBytes = 32 * 1024 * 1024;
    private const int MaxTotalImageBytes = 120 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, NoteImageAsset> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _bitmapCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredImageIds = new(StringComparer.Ordinal);
    private bool _writeDisabled;
    private bool _skipNextBackupRotation;
    private int _deferredImageSaveDepth;

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "note-assets.json");
    public string BackupPath { get; } = Path.Combine(AppContext.BaseDirectory, "note-assets.backup.json");

    public bool IsWriteDisabled => _writeDisabled;

    public void Load()
    {
        lock (_gate)
        {
            _images.Clear();
            _bitmapCache.Clear();
            _retiredImageIds.Clear();
            _writeDisabled = false;
            _skipNextBackupRotation = false;

            var mainExists = File.Exists(FilePath);
            var backupExists = File.Exists(BackupPath);
            if (!mainExists && !backupExists)
            {
                return;
            }

            var mainLoaded = TryLoadFile(FilePath, out var mainImages);
            if (mainLoaded)
            {
                ReplaceImages(mainImages);
                return;
            }

            if (TryLoadFile(BackupPath, out var backupImages))
            {
                _skipNextBackupRotation = mainExists;
                ReplaceImages(backupImages);
                return;
            }

            _writeDisabled = true;
        }
    }

    public bool TryGetAsset(string imageId, out NoteImageAsset asset)
    {
        lock (_gate)
        {
            return _images.TryGetValue(imageId, out asset!);
        }
    }

    public BitmapSource? GetBitmapSource(string imageId, double targetDisplayWidth = 0)
    {
        NoteImageAsset asset;
        lock (_gate)
        {
            if (!_images.TryGetValue(imageId, out asset!))
            {
                return null;
            }
        }

        var decodeWidth = DecodePixelWidth(asset, targetDisplayWidth);
        var cacheKey = $"{imageId}|{decodeWidth}";
        lock (_gate)
        {
            if (_bitmapCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var bytes = Convert.FromBase64String(asset.Base64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            lock (_gate)
            {
                _bitmapCache[cacheKey] = bitmap;
                TrimBitmapCache();
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public NoteImageAsset ImportBitmapSource(string noteId, BitmapSource source)
    {
        if (_writeDisabled)
        {
            throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
        }

        if (string.IsNullOrWhiteSpace(noteId))
        {
            throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
        }

        var normalized = NormalizeBitmapSource(source);
        var (bytes, mime) = EncodeConstrainedImage(normalized, preferJpeg: false);
        return AddEncodedImage(noteId, bytes, mime, "clipboard");
    }

    public NoteImageAsset ImportImageFile(string noteId, string path)
    {
        if (_writeDisabled)
        {
            throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
        }

        if (string.IsNullOrWhiteSpace(noteId))
        {
            throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(Strings.Get("ImageImportFileMissing"), path);
        }

        var sourceLength = new FileInfo(path).Length;
        if (sourceLength > MaxInputImageBytes)
        {
            throw new InvalidDataException(Strings.Format("ImageImportSourceTooLarge", MaxInputImageBytes / 1024 / 1024));
        }

        var originalName = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        if (!TryReadBitmapInfo(bytes, out var frame, out var width, out var height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        var mime = MimeFromExtension(path);
        var canKeepOriginal =
            IsDirectlyStoredMime(mime) &&
            bytes.Length <= MaxImageBytes &&
            Math.Max(width, height) <= MaxStoredDimension;

        if (canKeepOriginal)
        {
            return AddEncodedImage(noteId, bytes, mime, originalName, width, height);
        }

        var normalized = NormalizeBitmapSource(frame);
        var preferJpeg = string.Equals(mime, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        var encoded = EncodeConstrainedImage(normalized, preferJpeg);
        return AddEncodedImage(noteId, encoded.Bytes, encoded.Mime, originalName);
    }

    public IReadOnlyList<NoteImageAsset> ImportImageFiles(string noteId, IEnumerable<string> paths)
    {
        lock (_gate)
        {
            var imported = new List<NoteImageAsset>();
            _deferredImageSaveDepth++;
            try
            {
                foreach (var path in paths)
                {
                    if (!IsSupportedImageFile(path))
                    {
                        continue;
                    }

                    imported.Add(ImportImageFile(noteId, path));
                }

                if (imported.Count > 0)
                {
                    SaveLocked();
                }

                return imported;
            }
            catch
            {
                foreach (var asset in imported)
                {
                    RemoveImageLocked(asset.Id, reserveIdUntilRestart: false);
                }

                throw;
            }
            finally
            {
                _deferredImageSaveDepth--;
            }
        }
    }

    public bool TryWriteImageFile(string imageId, string path)
    {
        NoteImageAsset asset;
        lock (_gate)
        {
            if (!_images.TryGetValue(imageId, out asset!))
            {
                return false;
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, Convert.FromBase64String(asset.Base64));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string ConvertMarkdownForExternalEditor(
        string noteId,
        string markdown,
        string imageDirectory,
        string allowedRootDirectory)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var references = MarkdownImageReferences.Enumerate(markdown).ToList();
        if (references.Count == 0)
        {
            return MarkdownImageReferences.StripRenderMarkers(markdown);
        }

        if (!TryPrepareImageDirectory(imageDirectory, allowedRootDirectory, out var safeImageDirectory))
        {
            throw new IOException(Strings.Get("ExternalMarkdownImageExportFailed"));
        }

        var exported = new Dictionary<string, string>(StringComparer.Ordinal);
        return MarkdownImageReferences.ReplaceForExternalMarkdown(
            markdown,
            imageId =>
            {
                if (exported.TryGetValue(imageId, out var existing))
                {
                    return existing;
                }

                NoteImageAsset asset;
                lock (_gate)
                {
                    if (!_images.TryGetValue(imageId, out asset!) ||
                        !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(Strings.Get("ExternalMarkdownImageExportFailed"));
                    }
                }

                var extension = ExtensionFromMime(asset.Mime);
                var fileName = $"{asset.Id}{extension}";
                var fullPath = Path.Combine(safeImageDirectory, fileName);
                if (!TryWriteImageFile(asset.Id, fullPath))
                {
                    throw new IOException(Strings.Get("ExternalMarkdownImageExportFailed"));
                }

                var relative = "./" + Path.GetFileName(safeImageDirectory) + "/" + fileName;
                exported[imageId] = relative;
                return relative;
            });
    }

    public string CloneForeignImageReferencesForNote(string noteId, string markdown)
    {
        if (string.IsNullOrWhiteSpace(noteId) || string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var references = MarkdownImageReferences.Enumerate(markdown).ToList();
        if (references.Count == 0)
        {
            return markdown;
        }

        lock (_gate)
        {
            var foreignAssets = references
                .Select(reference => reference.ImageId)
                .Distinct(StringComparer.Ordinal)
                .Select(imageId => _images.GetValueOrDefault(imageId))
                .Where(asset => asset != null &&
                    !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal))
                .Cast<NoteImageAsset>()
                .ToList();
            if (foreignAssets.Count == 0)
            {
                return markdown;
            }

            if (_writeDisabled)
            {
                throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
            }

            var additionalBytes = foreignAssets.Sum(EstimatedDecodedLength);
            var currentBytes = _images.Values.Sum(EstimatedDecodedLength);
            if ((long)currentBytes + additionalBytes > MaxTotalImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTotalTooLarge", MaxTotalImageBytes / 1024 / 1024));
            }

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var addedIds = new List<string>();
            try
            {
                foreach (var source in foreignAssets)
                {
                    var clone = new NoteImageAsset
                    {
                        Id = AllocateImageIdLocked(),
                        NoteId = noteId,
                        Mime = source.Mime,
                        Width = source.Width,
                        Height = source.Height,
                        Sha256 = source.Sha256,
                        Base64 = source.Base64,
                        OriginalName = source.OriginalName,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    _images[clone.Id] = clone;
                    addedIds.Add(clone.Id);
                    replacements[source.Id] = clone.Id;
                }

                var rewritten = ReplaceImageReferenceIds(markdown, replacements);
                SaveLocked();
                return rewritten;
            }
            catch
            {
                foreach (var imageId in addedIds)
                {
                    RemoveImageLocked(imageId, reserveIdUntilRestart: false);
                }
                throw;
            }
        }
    }

    private static bool TryPrepareImageDirectory(
        string imageDirectory,
        string allowedRootDirectory,
        out string safeImageDirectory)
    {
        safeImageDirectory = "";
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRootDirectory));
            var candidate = Path.GetFullPath(imageDirectory);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Directory.CreateDirectory(root);
            if (Directory.Exists(candidate))
            {
                var attributes = File.GetAttributes(candidate);
                var recursive = (attributes & FileAttributes.ReparsePoint) == 0;
                Directory.Delete(candidate, recursive);
            }
            Directory.CreateDirectory(candidate);
            safeImageDirectory = candidate;
            return true;
        }
        catch
        {
            safeImageDirectory = "";
            return false;
        }
    }

    private static string ReplaceImageReferenceIds(
        string markdown,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (replacements.Count == 0)
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var reference in MarkdownImageReferences.Enumerate(markdown))
        {
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            builder.Append(replacements.TryGetValue(reference.ImageId, out var replacementId)
                ? reference.WithUrl(MarkdownImageReferences.UriPrefix + replacementId)
                : markdown.Substring(reference.LineStart, reference.LineLength));
            cursor = reference.LineStart + reference.LineLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    public void TrackReferences(AppState state, bool reserveRemovedIdsUntilRestart = true)
    {
        if (_writeDisabled)
        {
            return;
        }

        var referenced = new HashSet<(string NoteId, string ImageId)>();
        var liveNotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in state.Papers.Where(p => p.Type == PaperTypes.Note))
        {
            liveNotes.Add(note.Id);
            foreach (var imageId in MarkdownImageReferences.CollectImageIds(note.Content))
            {
                referenced.Add((note.Id, imageId));
            }
        }

        var changed = false;
        lock (_gate)
        {
            foreach (var asset in _images.Values.ToList())
            {
                if (!liveNotes.Contains(asset.NoteId))
                {
                    RemoveImageLocked(asset.Id, reserveRemovedIdsUntilRestart);
                    changed = true;
                    continue;
                }

                if (referenced.Contains((asset.NoteId, asset.Id)))
                {
                    if (asset.OrphanedAt != null)
                    {
                        asset.OrphanedAt = null;
                        changed = true;
                    }
                    continue;
                }

                RemoveImageLocked(asset.Id, reserveRemovedIdsUntilRestart);
                changed = true;
            }

            if (changed)
            {
                SaveLocked();
            }
        }
    }

    public static bool IsSupportedImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    private NoteImageAsset AddEncodedImage(
        string noteId,
        byte[] bytes,
        string mime,
        string originalName,
        int width = 0,
        int height = 0)
    {
        if (bytes.Length <= 0)
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        if (bytes.Length > MaxImageBytes)
        {
            throw new InvalidDataException(Strings.Format("ImageImportTooLarge", MaxImageBytes / 1024 / 1024));
        }

        if (width <= 0 || height <= 0)
        {
            if (!TryReadBitmapInfo(bytes, out _, out width, out height))
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }
        }

        lock (_gate)
        {
            var totalBytes = _images.Values.Sum(EstimatedDecodedLength) + bytes.Length;
            if (totalBytes > MaxTotalImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTotalTooLarge", MaxTotalImageBytes / 1024 / 1024));
            }

            var asset = new NoteImageAsset
            {
                Id = AllocateImageIdLocked(),
                NoteId = noteId,
                Mime = NormalizeMime(mime),
                Width = Math.Max(1, width),
                Height = Math.Max(1, height),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Base64 = Convert.ToBase64String(bytes),
                OriginalName = string.IsNullOrWhiteSpace(originalName) ? null : originalName,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _images[asset.Id] = asset;
            if (_deferredImageSaveDepth == 0)
            {
                SaveLocked();
            }
            return asset;
        }
    }

    private string AllocateImageIdLocked()
    {
        for (var number = 1; number < int.MaxValue; number++)
        {
            var id = FormatImageId(number);
            if (!_images.ContainsKey(id) && !_retiredImageIds.Contains(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException(Strings.Get("ImageImportUnsupported"));
    }

    private static string FormatImageId(int number)
        => number < 1000
            ? number.ToString("000", CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

    private static BitmapSource NormalizeBitmapSource(BitmapSource source)
    {
        var bitmap = source;
        if (Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) > MaxStoredDimension)
        {
            var scale = MaxStoredDimension / (double)Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        }

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static (byte[] Bytes, string Mime) EncodeConstrainedImage(BitmapSource source, bool preferJpeg)
    {
        if (preferJpeg)
        {
            var jpeg = EncodeJpeg(source, 88);
            if (jpeg.Length <= MaxImageBytes)
            {
                return (jpeg, "image/jpeg");
            }
        }

        var png = EncodePng(source);
        if (png.Length <= MaxImageBytes)
        {
            return (png, "image/png");
        }

        var fallbackJpeg = EncodeJpeg(source, 82);
        if (fallbackJpeg.Length <= MaxImageBytes)
        {
            return (fallbackJpeg, "image/jpeg");
        }

        throw new InvalidDataException(Strings.Format("ImageImportTooLarge", MaxImageBytes / 1024 / 1024));
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        return EncodeBitmap(encoder);
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        BitmapSource bitmap = source;
        if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Bgr32)
        {
            bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 50, 95) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return EncodeBitmap(encoder);
    }

    private static byte[] EncodeBitmap(BitmapEncoder encoder)
    {
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool TryReadBitmapInfo(byte[] bytes, out BitmapFrame frame, out int width, out int height)
    {
        frame = null!;
        width = 0;
        height = 0;

        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            frame.Freeze();
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int DecodePixelWidth(NoteImageAsset asset, double targetDisplayWidth)
    {
        if (targetDisplayWidth <= 0 || asset.Width <= 0)
        {
            return 0;
        }

        var requested = (int)Math.Ceiling(targetDisplayWidth);
        if (requested <= 0 || requested >= asset.Width)
        {
            return 0;
        }

        return Math.Clamp(requested, 32, asset.Width);
    }

    private static string MimeFromExtension(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/png"
        };

    private static bool IsDirectlyStoredMime(string mime)
        => mime is "image/png" or "image/jpeg" or "image/gif";

    private static string NormalizeMime(string mime)
        => mime is "image/jpeg" or "image/png" or "image/gif" or "image/bmp" or "image/tiff"
            ? mime
            : "image/png";

    private static string ExtensionFromMime(string mime)
        => mime switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tif",
            _ => ".png"
        };

    private static int EstimatedDecodedLength(NoteImageAsset asset)
        => asset.Base64.Length / 4 * 3;

    private bool TryLoadFile(string path, out List<NoteImageAsset> images)
    {
        images = new List<NoteImageAsset>();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<NoteImageStoreFile>(json, JsonOptions);
            if (file?.Images == null || file.Version != StoreVersion)
            {
                return false;
            }

            return TryValidateImages(file.Images, out images);
        }
        catch
        {
            images.Clear();
            return false;
        }
    }

    private static bool TryValidateImages(
        IEnumerable<NoteImageAsset> source,
        out List<NoteImageAsset> images)
    {
        images = new List<NoteImageAsset>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var asset in source)
        {
            if (!TryValidateAsset(asset, out var bytes, out var width, out var height) ||
                !usedIds.Add(asset.Id))
            {
                images.Clear();
                return false;
            }

            totalBytes += bytes.Length;
            if (totalBytes > MaxTotalImageBytes)
            {
                images.Clear();
                return false;
            }

            asset.Mime = NormalizeMime(asset.Mime);
            asset.Width = width;
            asset.Height = height;
            images.Add(asset);
        }

        return true;
    }

    private void ReplaceImages(IEnumerable<NoteImageAsset> images)
    {
        _images.Clear();
        foreach (var asset in images)
        {
            _images.Add(asset.Id, asset);
        }
    }

    private void RemoveImageLocked(string imageId, bool reserveIdUntilRestart)
    {
        if (!_images.Remove(imageId))
        {
            return;
        }

        RemoveCachedBitmapsFor(imageId);
        if (reserveIdUntilRestart)
        {
            _retiredImageIds.Add(imageId);
        }
    }

    private static bool TryValidateAsset(
        NoteImageAsset asset,
        out byte[] bytes,
        out int width,
        out int height)
    {
        bytes = Array.Empty<byte>();
        width = 0;
        height = 0;
        if (asset == null ||
            !MarkdownImageReferences.IsValidImageId(asset.Id) ||
            string.IsNullOrWhiteSpace(asset.NoteId) ||
            string.IsNullOrWhiteSpace(asset.Base64) ||
            string.IsNullOrWhiteSpace(asset.Sha256))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(asset.Base64);
            if (bytes.Length is <= 0 or > MaxImageBytes ||
                !TryReadBitmapInfo(bytes, out _, out width, out height) ||
                Math.Max(width, height) > MaxStoredDimension)
            {
                return false;
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            return string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            bytes = Array.Empty<byte>();
            width = 0;
            height = 0;
            return false;
        }
    }

    private void SaveLocked()
    {
        if (_writeDisabled)
        {
            return;
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new NoteImageStoreFile
        {
            Version = StoreVersion,
            Images = _images.Values
                .OrderBy(asset => asset.CreatedAt)
                .ThenBy(asset => asset.Id, StringComparer.Ordinal)
                .ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        try
        {
            if (!_skipNextBackupRotation && File.Exists(FilePath))
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
        }
        catch
        {
            // Losing one asset backup rotation should not block saving the current image store.
        }

        File.Move(tempPath, FilePath, overwrite: true);
        _skipNextBackupRotation = false;
    }

    private void RemoveCachedBitmapsFor(string imageId)
    {
        foreach (var key in _bitmapCache.Keys.Where(key => key.StartsWith(imageId + "|", StringComparison.Ordinal)).ToList())
        {
            _bitmapCache.Remove(key);
        }
    }

    private void TrimBitmapCache()
    {
        if (_bitmapCache.Count <= 96)
        {
            return;
        }

        foreach (var key in _bitmapCache.Keys.Take(_bitmapCache.Count - 96).ToList())
        {
            _bitmapCache.Remove(key);
        }
    }

    private sealed class NoteImageStoreFile
    {
        public int Version { get; set; } = StoreVersion;
        public List<NoteImageAsset> Images { get; set; } = new();
    }
}

public sealed class NoteImageAsset
{
    public string Id { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Mime { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = "";
    public string Base64 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OrphanedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalName { get; set; }
}
