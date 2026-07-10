using System.IO;
using System.Globalization;
using System.Security.Cryptography;
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
        var imported = new List<NoteImageAsset>();
        foreach (var path in paths)
        {
            if (!IsSupportedImageFile(path))
            {
                continue;
            }

            imported.Add(ImportImageFile(noteId, path));
        }

        return imported;
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

    public string ConvertMarkdownForExternalEditor(string noteId, string markdown, string imageDirectory)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        try
        {
            if (Directory.Exists(imageDirectory))
            {
                Directory.Delete(imageDirectory, recursive: true);
            }
            Directory.CreateDirectory(imageDirectory);
        }
        catch
        {
            return markdown;
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
                        return null;
                    }
                }

                var extension = ExtensionFromMime(asset.Mime);
                var fileName = $"{asset.Id}{extension}";
                var fullPath = Path.Combine(imageDirectory, fileName);
                if (!TryWriteImageFile(asset.Id, fullPath))
                {
                    return null;
                }

                var relative = "./" + Path.GetFileName(imageDirectory) + "/" + fileName;
                exported[imageId] = relative;
                return relative;
            });
    }

    public void TrackReferences(AppState state, bool reserveRemovedIdsUntilRestart = true)
    {
        if (_writeDisabled)
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var liveNotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in state.Papers.Where(p => p.Type == PaperTypes.Note))
        {
            liveNotes.Add(note.Id);
            foreach (var imageId in MarkdownImageReferences.CollectImageIds(note.Content))
            {
                referenced.Add(imageId);
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

                if (referenced.Contains(asset.Id))
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
            SaveLocked();
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
            if (file?.Images == null)
            {
                return false;
            }

            images = file.Images;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReplaceImages(IEnumerable<NoteImageAsset> images)
    {
        _images.Clear();
        foreach (var asset in images)
        {
            if (!IsValidAsset(asset) || _images.ContainsKey(asset.Id))
            {
                continue;
            }

            asset.Mime = NormalizeMime(asset.Mime);
            asset.Width = Math.Max(1, asset.Width);
            asset.Height = Math.Max(1, asset.Height);
            _images[asset.Id] = asset;
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

    private static bool IsValidAsset(NoteImageAsset asset)
    {
        if (asset == null ||
            string.IsNullOrWhiteSpace(asset.Id) ||
            string.IsNullOrWhiteSpace(asset.NoteId) ||
            string.IsNullOrWhiteSpace(asset.Base64))
        {
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(asset.Base64);
            return true;
        }
        catch
        {
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
