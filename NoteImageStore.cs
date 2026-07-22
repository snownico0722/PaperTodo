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
    // 4096 is the hard import/storage limit. Automatic compression targets 2048, but disabling
    // compression may still store an untouched image between 2049 and 4096 pixels.
    private const int MaxStoredDimension = 4096;
    private const int AutoCompressedDimension = 2048;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxInputImageBytes = 32 * 1024 * 1024;
    private const int MaxTotalImageBytes = 120 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, NoteImageAsset> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _bitmapCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredImageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedImageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _corruptedImageIds = new(StringComparer.Ordinal);
    private readonly Queue<ReusableImageNumberRange> _reusableImageNumberRanges = new();
    private LmdbImageDatabase? _database;
    private long _totalImageBytes;
    private int _nextImageNumber = 1;
    private bool _writeDisabled;
    private bool _disposed;

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "note-assets.lmdb");

    public bool IsWriteDisabled => _writeDisabled;

    public bool AutoCompressLargeImages { get; set; } = true;

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
            _corruptedImageIds.Clear();
            _reusableImageNumberRanges.Clear();
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
                if (!TryValidateIndex(index, out var images, out var corruptedImageIds, out var totalBytes))
                {
                    _database.Dispose();
                    _database = null;
                    _writeDisabled = true;
                    return;
                }

                _totalImageBytes = totalBytes;
                _nextImageNumber = index.NextImageNumber;
                _corruptedImageIds.UnionWith(corruptedImageIds);
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

    public bool IsImageCorrupted(string imageId)
    {
        lock (_gate)
        {
            return _corruptedImageIds.Contains(imageId);
        }
    }

    public BitmapSource? GetBitmapSource(string imageId, double targetDisplayWidth = 0)
    {
        NoteImageAsset asset;
        lock (_gate)
        {
            if (_corruptedImageIds.Contains(imageId) ||
                !_images.TryGetValue(imageId, out asset!))
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
            // The blob has already passed length and SHA-256 verification. A WIC/codec or memory
            // failure is not evidence that the stored data is corrupt, so leave it retryable.
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

        var image = PrepareBitmapSource(source);
        return AddEncodedImage(
            noteId,
            image.Bytes,
            image.Mime,
            image.OriginalName,
            image.Width,
            image.Height);
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

        ThrowIfUnsupportedImageFiles(new[] { path });

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

        var candidatePaths = paths.ToList();
        ThrowIfUnsupportedImageFiles(candidatePaths);

        var images = new List<PreparedImage>(candidatePaths.Count);
        foreach (var path in candidatePaths)
        {
            images.Add(PrepareImageFile(path));
        }

        return AddEncodedImages(noteId, images);
    }

    private PreparedImage PrepareImageFile(string path)
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

        if (!AutoCompressLargeImages && sourceLength > MaxImageBytes)
        {
            throw new InvalidDataException(Strings.Format("ImageImportTooLargeCompressionDisabled", MaxImageBytes / 1024 / 1024));
        }

        var originalName = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxInputImageBytes)
        {
            throw new InvalidDataException(Strings.Format("ImageImportSourceTooLarge", MaxInputImageBytes / 1024 / 1024));
        }
        if (!AutoCompressLargeImages && bytes.Length > MaxImageBytes)
        {
            throw new InvalidDataException(Strings.Format("ImageImportTooLargeCompressionDisabled", MaxImageBytes / 1024 / 1024));
        }
        if (!TryReadBitmapInfo(bytes, out var frame, out var width, out var height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        ValidateImageDimensions(width, height);

        var mime = MimeFromEncodedBytes(bytes)
            ?? throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        if (!AutoCompressLargeImages || !RequiresAutomaticCompression(bytes.Length, width, height))
        {
            return new PreparedImage(bytes, mime, originalName, width, height);
        }

        var encoded = CompressImage(frame, bytes, mime);
        if (!TryReadBitmapInfo(encoded.Bytes, out _, out width, out height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionFailed"));
        }
        ValidateCompressedDimensions(width, height);
        return new PreparedImage(encoded.Bytes, encoded.Mime, originalName, width, height);
    }

    private PreparedImage PrepareBitmapSource(BitmapSource source)
    {
        ValidateImageDimensions(source.PixelWidth, source.PixelHeight);

        byte[] originalBytes;
        try
        {
            originalBytes = EncodePng(source);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionFailed"), ex);
        }

        if (!AutoCompressLargeImages)
        {
            if (originalBytes.Length > MaxImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportTooLargeCompressionDisabled", MaxImageBytes / 1024 / 1024));
            }

            return new PreparedImage(
                originalBytes,
                "image/png",
                "clipboard",
                source.PixelWidth,
                source.PixelHeight);
        }

        if (!RequiresAutomaticCompression(originalBytes.Length, source.PixelWidth, source.PixelHeight))
        {
            return new PreparedImage(
                originalBytes,
                "image/png",
                "clipboard",
                source.PixelWidth,
                source.PixelHeight);
        }

        var encoded = CompressImage(source, originalBytes, "image/png");
        if (!TryReadBitmapInfo(encoded.Bytes, out _, out var width, out var height))
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionFailed"));
        }
        ValidateCompressedDimensions(width, height);

        return new PreparedImage(encoded.Bytes, encoded.Mime, "clipboard", width, height);
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
            return markdown;
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
            var reusableImageNumberRanges = CloneReusableImageNumberRangesLocked();
            foreach (var source in foreignAssets)
            {
                if (!TryReadImageBytesLocked(source, out var bytes))
                {
                    throw new InvalidDataException(Strings.Get("ImageStoreUnavailable"));
                }

                var clone = new NoteImageAsset
                {
                    Id = AllocateImageIdLocked(ref nextImageNumber, reusableImageNumberRanges),
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
            ReplaceReusableImageNumberRangesLocked(reusableImageNumberRanges);

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

    public void ReleaseUnreferencedBitmapCache(AppState state)
    {
        if (_writeDisabled)
        {
            return;
        }

        var referencedImageIds = state.Papers
            .Where(paper => paper.Type == PaperTypes.Note)
            .SelectMany(paper => MarkdownImageReferences.CollectImageIds(paper.Content))
            .ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var asset in _images.Values)
            {
                if (!referencedImageIds.Contains(asset.Id))
                {
                    // Keep the original bytes for undo; only decoded pixels are disposable
                    // while the image is absent from the current document.
                    RemoveCachedBitmapsFor(asset.Id);
                }
            }
        }
    }

    internal void CollectUnprotectedImages(IReadOnlySet<string> protectedImageIds)
    {
        if (_writeDisabled)
        {
            return;
        }

        lock (_gate)
        {
            var removedIds = _images.Values
                .Where(asset => !protectedImageIds.Contains(asset.Id))
                .Select(asset => asset.Id)
                .ToList();
            if (removedIds.Count == 0)
            {
                return;
            }

            RequireDatabaseLocked().DeleteImages(removedIds);
            foreach (var imageId in removedIds)
            {
                RemoveImageLocked(imageId, reserveIdUntilRestart: true);
            }
        }
    }

    internal void PrepareReusableImageNumbers()
    {
        lock (_gate)
        {
            _reusableImageNumberRanges.Clear();
            if (_writeDisabled || _nextImageNumber <= 1)
            {
                return;
            }

            // Startup collection runs before editors and undo stacks exist, so ids retired by
            // that pass can safely join the new-session allocation pool.
            _retiredImageIds.Clear();

            var occupiedNumbers = new SortedSet<int>();
            foreach (var imageId in _images.Keys.Concat(_corruptedImageIds))
            {
                if (int.TryParse(imageId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                    number > 0 &&
                    number < _nextImageNumber)
                {
                    occupiedNumbers.Add(number);
                }
            }

            var rangeStart = 1;
            foreach (var occupiedNumber in occupiedNumbers)
            {
                if (rangeStart < occupiedNumber)
                {
                    _reusableImageNumberRanges.Enqueue(
                        new ReusableImageNumberRange(rangeStart, occupiedNumber - 1));
                }

                rangeStart = occupiedNumber + 1;
            }

            if (rangeStart < _nextImageNumber)
            {
                _reusableImageNumberRanges.Enqueue(
                    new ReusableImageNumberRange(rangeStart, _nextImageNumber - 1));
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

    private static void ThrowIfUnsupportedImageFiles(IReadOnlyList<string> paths)
    {
        var unsupported = paths
            .Where(path => !IsSupportedImageFile(path))
            .ToList();
        if (unsupported.Count == 0)
        {
            return;
        }

        var displayedNames = unsupported
            .Take(3)
            .Select(DisplayImageFileName);
        var summary = string.Join(", ", displayedNames);
        if (unsupported.Count > 3)
        {
            summary += Strings.Format("ImageImportAdditionalFiles", unsupported.Count - 3);
        }

        throw new InvalidDataException(Strings.Format("ImageImportUnsupportedFiles", summary));
    }

    private static string DisplayImageFileName(string path)
    {
        string fileName;
        try
        {
            fileName = Path.GetFileName(path);
        }
        catch
        {
            return Strings.Get("ImageImportUnknownFile");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Strings.Get("ImageImportUnknownFile");
        }

        const int maxTextElements = 48;
        var textElementStarts = StringInfo.ParseCombiningCharacters(fileName);
        return textElementStarts.Length <= maxTextElements
            ? fileName
            : fileName[..textElementStarts[maxTextElements]] + "…";
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

            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }

            if (image.Width > MaxStoredDimension || image.Height > MaxStoredDimension)
            {
                throw new InvalidDataException(Strings.Format("ImageImportDimensionsTooLarge", MaxStoredDimension));
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
            var reusableImageNumberRanges = CloneReusableImageNumberRangesLocked();
            foreach (var image in images)
            {
                var asset = new NoteImageAsset
                {
                    Id = AllocateImageIdLocked(ref nextImageNumber, reusableImageNumberRanges),
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
            ReplaceReusableImageNumberRangesLocked(reusableImageNumberRanges);
            return writes.Select(write => write.Asset).ToList();
        }
    }

    private string AllocateImageIdLocked(
        ref int nextImageNumber,
        Queue<ReusableImageNumberRange> reusableImageNumberRanges)
    {
        while (reusableImageNumberRanges.Count > 0)
        {
            var range = reusableImageNumberRanges.Peek();
            var number = range.NextNumber++;
            if (range.NextNumber > range.LastNumber)
            {
                reusableImageNumberRanges.Dequeue();
            }

            var id = FormatImageId(number);
            if (!_images.ContainsKey(id) &&
                !_retiredImageIds.Contains(id) &&
                !_corruptedImageIds.Contains(id))
            {
                return id;
            }
        }

        while (nextImageNumber <= 99_999_999)
        {
            var number = nextImageNumber++;
            var id = FormatImageId(number);
            if (!_images.ContainsKey(id) &&
                !_retiredImageIds.Contains(id) &&
                !_corruptedImageIds.Contains(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException(Strings.Get("ImageImportUnsupported"));
    }

    private Queue<ReusableImageNumberRange> CloneReusableImageNumberRangesLocked()
    {
        var clone = new Queue<ReusableImageNumberRange>(_reusableImageNumberRanges.Count);
        foreach (var range in _reusableImageNumberRanges)
        {
            clone.Enqueue(new ReusableImageNumberRange(range.NextNumber, range.LastNumber));
        }

        return clone;
    }

    private void ReplaceReusableImageNumberRangesLocked(
        Queue<ReusableImageNumberRange> ranges)
    {
        _reusableImageNumberRanges.Clear();
        foreach (var range in ranges)
        {
            _reusableImageNumberRanges.Enqueue(range);
        }
    }

    private static string FormatImageId(int number)
        => number < 1000
            ? number.ToString("000", CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

    private sealed class ReusableImageNumberRange(int nextNumber, int lastNumber)
    {
        public int NextNumber { get; set; } = nextNumber;

        public int LastNumber { get; } = lastNumber;
    }

    private static void ValidateImageDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        if (width > MaxStoredDimension || height > MaxStoredDimension)
        {
            throw new InvalidDataException(Strings.Format("ImageImportDimensionsTooLarge", MaxStoredDimension));
        }
    }

    private static bool RequiresAutomaticCompression(int byteLength, int width, int height)
        => byteLength > MaxImageBytes ||
            width > AutoCompressedDimension ||
            height > AutoCompressedDimension;

    private static void ValidateCompressedDimensions(int width, int height)
    {
        if (width <= 0 ||
            height <= 0 ||
            width > AutoCompressedDimension ||
            height > AutoCompressedDimension)
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionFailed"));
        }
    }

    private static (byte[] Bytes, string Mime) CompressImage(
        BitmapSource source,
        byte[] originalBytes,
        string originalMime)
    {
        // Re-encoding these formats would silently discard animation or additional frames.
        if (originalMime is "image/gif" or "image/tiff")
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionUnsafe"));
        }

        try
        {
            var resized = ResizeBitmapSource(source, AutoCompressedDimension);
            (byte[] Bytes, string Mime) encoded;
            if (string.Equals(originalMime, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                encoded = (EncodeJpeg(resized, 82), "image/jpeg");
            }
            else
            {
                var png = EncodePng(resized);
                encoded = (png, "image/png");

                if (png.Length > MaxImageBytes && !HasAlphaChannel(resized))
                {
                    var jpeg = EncodeJpeg(resized, 82);
                    if (jpeg.Length < png.Length)
                    {
                        encoded = (jpeg, "image/jpeg");
                    }
                }
            }

            // Automatic compression has no original-file fallback: a failed, oversized, or
            // non-beneficial result aborts the entire import before the LMDB transaction starts.
            if (encoded.Bytes.Length >= originalBytes.Length)
            {
                throw new InvalidDataException(Strings.Get("ImageImportCompressionNotSmaller"));
            }

            if (encoded.Bytes.Length > MaxImageBytes)
            {
                throw new InvalidDataException(Strings.Format("ImageImportCompressedTooLarge", MaxImageBytes / 1024 / 1024));
            }

            return encoded;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(Strings.Get("ImageImportCompressionFailed"), ex);
        }
    }

    private static BitmapSource ResizeBitmapSource(BitmapSource source, int maxDimension)
    {
        BitmapSource bitmap = source;
        var longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longestEdge > maxDimension)
        {
            var scale = maxDimension / (double)longestEdge;
            bitmap = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static bool HasAlphaChannel(BitmapSource source)
    {
        var format = source.Format;
        if (format == PixelFormats.Bgra32 ||
            format == PixelFormats.Pbgra32 ||
            format == PixelFormats.Rgba64 ||
            format == PixelFormats.Prgba64 ||
            format == PixelFormats.Rgba128Float ||
            format == PixelFormats.Prgba128Float)
        {
            return true;
        }

        return source.Palette?.Colors.Any(color => color.A < byte.MaxValue) == true;
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

    private static string? MimeFromEncodedBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        if (bytes.StartsWith("BM"u8))
        {
            return "image/bmp";
        }
        if (bytes.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
            bytes.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }))
        {
            return "image/tiff";
        }

        return null;
    }

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
        out List<NoteImageAsset> images,
        out HashSet<string> corruptedImageIds,
        out long totalBytes)
    {
        images = new List<NoteImageAsset>();
        corruptedImageIds = new HashSet<string>(index.CorruptedImageIds, StringComparer.Ordinal);
        totalBytes = 0;
        if (index.NextImageNumber is < 1 or > 100_000_000)
        {
            return false;
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in index.Assets)
        {
            if (!TryValidateAssetMetadata(asset) ||
                !usedIds.Add(asset.Id))
            {
                if (!string.IsNullOrWhiteSpace(asset?.Id))
                {
                    corruptedImageIds.Add(asset.Id);
                }
                continue;
            }

            if (asset.ByteLength > MaxTotalImageBytes - totalBytes)
            {
                corruptedImageIds.Add(asset.Id);
                continue;
            }

            totalBytes += asset.ByteLength;
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
        if (!_images.Remove(imageId, out var asset))
        {
            return;
        }

        _totalImageBytes = Math.Max(0, _totalImageBytes - asset.ByteLength);
        _verifiedImageIds.Remove(imageId);
        _corruptedImageIds.Remove(imageId);
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
        if (_corruptedImageIds.Contains(asset.Id))
        {
            return false;
        }

        if (_database == null)
        {
            return false;
        }

        if (!_database.TryReadBlob(asset.Id, out var storedBytes) ||
            storedBytes.Length != asset.ByteLength)
        {
            MarkImageCorruptedLocked(asset.Id);
            return false;
        }

        if (!_verifiedImageIds.Contains(asset.Id))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(storedBytes));
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                MarkImageCorruptedLocked(asset.Id);
                return false;
            }
            _verifiedImageIds.Add(asset.Id);
        }

        bytes = storedBytes;
        return true;
    }

    private void MarkImageCorruptedLocked(string imageId)
    {
        _corruptedImageIds.Add(imageId);
        _verifiedImageIds.Remove(imageId);
        RemoveCachedBitmapsFor(imageId);
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
            _corruptedImageIds.Clear();
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
