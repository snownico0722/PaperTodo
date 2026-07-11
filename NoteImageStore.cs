using System;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

public sealed class NoteImageStore : IDisposable
{
    private const int MaxStoredDimension = 4096;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxInputImageBytes = 32 * 1024 * 1024;
    private const int MaxTotalImageBytes = 120 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, NoteImageAsset> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _bitmapCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredImageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedImageIds = new(StringComparer.Ordinal);
    private LmdbImageDatabase? _database;
    private long _totalImageBytes;
    private int _nextImageNumber = 1;
    private bool _writeDisabled;
    private bool _disposed;

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "note-assets.lmdb");

    public bool IsWriteDisabled => _writeDisabled;

    public void Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _database?.Dispose();
            _database = null;
            _images.Clear();
            _bitmapCache.Clear();
            _retiredImageIds.Clear();
            _verifiedImageIds.Clear();
            _totalImageBytes = 0;
            _nextImageNumber = 1;
            _writeDisabled = false;

            if (!File.Exists(FilePath))
            {
                return;
            }

            try
            {
                _database = LmdbImageDatabase.Open(FilePath);
                var index = _database.ReadIndex();
                if (!TryValidateIndex(index, out var images))
                {
                    _database.Dispose();
                    _database = null;
                    _writeDisabled = true;
                    return;
                }

                _totalImageBytes = index.TotalBytes;
                _nextImageNumber = index.NextImageNumber;
                ReplaceImages(images);
            }
            catch
            {
                _database?.Dispose();
                _database = null;
                _writeDisabled = true;
            }
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

        byte[] bytes;
        lock (_gate)
        {
            if (!TryReadImageBytesLocked(asset, out bytes))
            {
                return null;
            }
        }

        try
        {
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

        var image = PrepareImageFile(path);
        return AddEncodedImage(
            noteId,
            image.Bytes,
            image.Mime,
            image.OriginalName,
            image.Width,
            image.Height);
    }

    public IReadOnlyList<NoteImageAsset> ImportImageFiles(string noteId, IEnumerable<string> paths)
    {
        if (_writeDisabled)
        {
            throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
        }

        if (string.IsNullOrWhiteSpace(noteId))
        {
            throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
        }

        var images = new List<PreparedImage>();
        foreach (var path in paths)
        {
            if (!IsSupportedImageFile(path))
            {
                continue;
            }

            images.Add(PrepareImageFile(path));
        }

        return AddEncodedImages(noteId, images);
    }

    private static PreparedImage PrepareImageFile(string path)
    {
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
            return new PreparedImage(bytes, mime, originalName, width, height);
        }

        var normalized = NormalizeBitmapSource(frame);
        var preferJpeg = string.Equals(mime, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        var encoded = EncodeConstrainedImage(normalized, preferJpeg);
        if (!TryReadBitmapInfo(encoded.Bytes, out _, out width, out height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }
        return new PreparedImage(encoded.Bytes, encoded.Mime, originalName, width, height);
    }

    public bool TryWriteImageFile(string imageId, string path)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!_images.TryGetValue(imageId, out var asset) ||
                !TryReadImageBytesLocked(asset, out bytes))
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

            File.WriteAllBytes(path, bytes);
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

            var additionalBytes = foreignAssets.Sum(asset => (long)asset.ByteLength);
            if (_totalImageBytes + additionalBytes > MaxTotalImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTotalTooLarge", MaxTotalImageBytes / 1024 / 1024));
            }

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var writes = new List<LmdbImageWrite>(foreignAssets.Count);
            var nextImageNumber = _nextImageNumber;
            foreach (var source in foreignAssets)
            {
                if (!TryReadImageBytesLocked(source, out var bytes))
                {
                    throw new InvalidDataException(Strings.Get("ImageStoreUnavailable"));
                }

                var clone = new NoteImageAsset
                {
                    Id = AllocateImageIdLocked(ref nextImageNumber),
                    NoteId = noteId,
                    Mime = source.Mime,
                    Width = source.Width,
                    Height = source.Height,
                    Sha256 = source.Sha256,
                    ByteLength = source.ByteLength,
                    OriginalName = source.OriginalName,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                writes.Add(new LmdbImageWrite(clone, bytes));
                replacements[source.Id] = clone.Id;
            }

            RequireDatabaseLocked().AddImages(writes, nextImageNumber);
            foreach (var write in writes)
            {
                _images.Add(write.Asset.Id, write.Asset);
                _verifiedImageIds.Add(write.Asset.Id);
            }
            _totalImageBytes += additionalBytes;
            _nextImageNumber = nextImageNumber;

            return ReplaceImageReferenceIds(markdown, replacements);
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

        lock (_gate)
        {
            var removedIds = new List<string>();
            foreach (var asset in _images.Values.ToList())
            {
                if (!liveNotes.Contains(asset.NoteId))
                {
                    removedIds.Add(asset.Id);
                    continue;
                }

                if (referenced.Contains((asset.NoteId, asset.Id)))
                {
                    continue;
                }

                removedIds.Add(asset.Id);
            }

            if (removedIds.Count == 0)
            {
                return;
            }

            RequireDatabaseLocked().DeleteImages(removedIds);
            foreach (var imageId in removedIds)
            {
                RemoveImageLocked(imageId, reserveRemovedIdsUntilRestart);
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
        if ((width <= 0 || height <= 0) &&
            !TryReadBitmapInfo(bytes, out _, out width, out height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        var image = new PreparedImage(bytes, mime, originalName, width, height);
        return AddEncodedImages(noteId, new[] { image })[0];
    }

    private IReadOnlyList<NoteImageAsset> AddEncodedImages(
        string noteId,
        IReadOnlyList<PreparedImage> images)
    {
        if (images.Count == 0)
        {
            return Array.Empty<NoteImageAsset>();
        }

        foreach (var image in images)
        {
            if (image.Bytes.Length <= 0)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }

            if (image.Bytes.Length > MaxImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTooLarge", MaxImageBytes / 1024 / 1024));
            }

            if (image.Width <= 0 ||
                image.Height <= 0 ||
                Math.Max(image.Width, image.Height) > MaxStoredDimension)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }
        }

        lock (_gate)
        {
            if (_writeDisabled)
            {
                throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
            }

            var additionalBytes = images.Sum(image => (long)image.Bytes.Length);
            if (_totalImageBytes + additionalBytes > MaxTotalImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTotalTooLarge", MaxTotalImageBytes / 1024 / 1024));
            }

            var writes = new List<LmdbImageWrite>(images.Count);
            var nextImageNumber = _nextImageNumber;
            foreach (var image in images)
            {
                var asset = new NoteImageAsset
                {
                    Id = AllocateImageIdLocked(ref nextImageNumber),
                    NoteId = noteId,
                    Mime = NormalizeMime(image.Mime),
                    Width = image.Width,
                    Height = image.Height,
                    Sha256 = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant(),
                    ByteLength = image.Bytes.Length,
                    OriginalName = string.IsNullOrWhiteSpace(image.OriginalName) ? null : image.OriginalName,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                writes.Add(new LmdbImageWrite(asset, image.Bytes));
            }

            RequireDatabaseLocked().AddImages(writes, nextImageNumber);
            foreach (var write in writes)
            {
                _images.Add(write.Asset.Id, write.Asset);
                _verifiedImageIds.Add(write.Asset.Id);
            }
            _totalImageBytes += additionalBytes;
            _nextImageNumber = nextImageNumber;
            return writes.Select(write => write.Asset).ToList();
        }
    }

    private string AllocateImageIdLocked(ref int nextImageNumber)
    {
        while (nextImageNumber <= 99_999_999)
        {
            var number = nextImageNumber++;
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

    private static bool TryValidateIndex(
        LmdbImageIndex index,
        out List<NoteImageAsset> images)
    {
        images = new List<NoteImageAsset>();
        if (index.NextImageNumber is < 1 or > 100_000_000 ||
            index.TotalBytes is < 0 or > MaxTotalImageBytes)
        {
            return false;
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var asset in index.Assets)
        {
            if (!TryValidateAssetMetadata(asset) ||
                !usedIds.Add(asset.Id))
            {
                images.Clear();
                return false;
            }

            totalBytes += asset.ByteLength;
            if (totalBytes > MaxTotalImageBytes)
            {
                images.Clear();
                return false;
            }

            images.Add(asset);
        }

        return totalBytes == index.TotalBytes;
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
        if (!_images.Remove(imageId, out var asset))
        {
            return;
        }

        _totalImageBytes = Math.Max(0, _totalImageBytes - asset.ByteLength);
        _verifiedImageIds.Remove(imageId);
        RemoveCachedBitmapsFor(imageId);
        if (reserveIdUntilRestart)
        {
            _retiredImageIds.Add(imageId);
        }
    }

    private static bool TryValidateAssetMetadata(NoteImageAsset asset)
    {
        if (asset == null ||
            !MarkdownImageReferences.IsValidImageId(asset.Id) ||
            string.IsNullOrWhiteSpace(asset.NoteId) ||
            asset.Mime != NormalizeMime(asset.Mime) ||
            asset.Width <= 0 ||
            asset.Height <= 0 ||
            Math.Max(asset.Width, asset.Height) > MaxStoredDimension ||
            asset.ByteLength is <= 0 or > MaxImageBytes ||
            string.IsNullOrWhiteSpace(asset.Sha256) ||
            asset.Sha256.Length != 64)
        {
            return false;
        }

        try
        {
            var hexBytes = Convert.FromHexString(asset.Sha256);
            return hexBytes.Length == 32;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadImageBytesLocked(NoteImageAsset asset, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (_database == null ||
            !_database.TryReadBlob(asset.Id, out var storedBytes) ||
            storedBytes.Length != asset.ByteLength)
        {
            return false;
        }

        if (!_verifiedImageIds.Contains(asset.Id))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(storedBytes));
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            _verifiedImageIds.Add(asset.Id);
        }

        bytes = storedBytes;
        return true;
    }

    private LmdbImageDatabase RequireDatabaseLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeDisabled)
        {
            throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
        }

        if (_database != null)
        {
            return _database;
        }

        try
        {
            _database = LmdbImageDatabase.Open(FilePath);
            return _database;
        }
        catch (Exception ex)
        {
            _database?.Dispose();
            _database = null;
            _writeDisabled = true;
            throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"), ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _database?.Dispose();
            _database = null;
            _images.Clear();
            _bitmapCache.Clear();
            _verifiedImageIds.Clear();
        }
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

    private readonly record struct PreparedImage(
        byte[] Bytes,
        string Mime,
        string OriginalName,
        int Width,
        int Height);
}

public sealed class NoteImageAsset
{
    public string Id { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Mime { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = "";
    public int ByteLength { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? OriginalName { get; set; }
}
