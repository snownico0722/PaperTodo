using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace PaperTodo;

internal sealed record TransferResult(string OutputPath, int PaperCount, int ImageCount, IReadOnlyList<string> MissingImageIds);

internal static class Transfer
{
    internal const int MaxImageBytes = 8 * 1024 * 1024;
    internal const int MaxTotalImageBytes = 120 * 1024 * 1024;
    internal const int MaxDataBytes = 16 * 1024 * 1024;
    private const int MaxImageCount = 20_000;
    private const int MaxMetadataBytes = 8 * 1024 * 1024;
    private const int MaxDimension = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static TransferResult Export(string sourceDirectory, string outputPath, bool checkRunning = true)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputPath);
        if (checkRunning) EnsureDesktopStopped(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("电脑端数据文件夹不存在。");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("输出路径已存在，请换一个文件名以保留已有文件。");
        var dataPath = Path.Combine(source, "data.json");
        if (!File.Exists(dataPath)) throw new FileNotFoundException("文件夹中没有 data.json。", dataPath);
        if (new FileInfo(dataPath).Length > MaxDataBytes) throw new InvalidDataException("data.json 超过 16 MiB 限制。");

        using var temporary = new TemporaryDirectory();
        File.Copy(dataPath, Path.Combine(temporary.Path, "data.json"));
        var data = File.ReadAllBytes(Path.Combine(temporary.Path, "data.json"));
        using var document = ValidateData(data);
        var images = new List<LmdbImageWrite>();
        var sourceDatabase = Path.Combine(source, "note-assets.lmdb");
        if (File.Exists(sourceDatabase))
        {
            var copiedDatabase = Path.Combine(temporary.Path, "note-assets.lmdb");
            File.Copy(sourceDatabase, copiedDatabase);
            using var database = LmdbImageDatabase.Open(copiedDatabase);
            var index = database.ReadIndex();
            if (index.Assets.Count > MaxImageCount) throw new InvalidDataException("图片数量超过手机传输包的 20000 张限制。");
            if (index.CorruptedImageIds.Count > 0)
                throw new InvalidDataException("图片库包含损坏记录，未生成传输包。原文件未改动，请先备份并检查图片库。");
            long total = 0;
            foreach (var asset in index.Assets)
            {
                if (!database.TryReadBlob(asset.Id, out var bytes)) throw new InvalidDataException($"缺少图片 {asset.Id} 的数据。");
                ValidateImage(asset, bytes);
                total += bytes.Length;
                if (total > MaxTotalImageBytes) throw new InvalidDataException("图片总大小超过 120 MiB。");
                images.Add(new LmdbImageWrite(asset, bytes));
            }
        }

        var archivePath = Path.Combine(temporary.Path, "transfer.zip");
        WriteArchive(archivePath, data, images);
        var outputParent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputParent);
        var stagedOutput = Path.Combine(outputParent, $".papertodo-export-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var destination = new FileStream(stagedOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var stream = File.OpenRead(archivePath))
            {
                stream.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            // Never replace an existing export, including a racing writer. Rename only
            // after the complete archive has reached the destination volume.
            File.Move(stagedOutput, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(stagedOutput)) File.Delete(stagedOutput);
        }
        return Result(output, document, images);
    }

    internal static TransferResult Restore(string inputPath, string destinationDirectory)
    {
        var input = Path.GetFullPath(inputPath);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (!File.Exists(input)) throw new FileNotFoundException("输入 ZIP 不存在。", input);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("恢复目标已存在。为保护已有数据，请指定一个新文件夹。");
        // Validate the whole archive before creating the output directory.
        var package = ReadArchive(input);
        using var document = ValidateData(package.Data);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("恢复目标的上一级文件夹不存在。");
        var staging = Path.Combine(parent, $".papertodo-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "data.json"), package.Data);
            File.WriteAllBytes(Path.Combine(staging, "data.backup.json"), package.Data);
            using (var database = LmdbImageDatabase.Open(Path.Combine(staging, "note-assets.lmdb")))
            {
                var next = package.Images.Count == 0 ? 1 : package.Images.Max(x => int.Parse(x.Asset.Id, CultureInfo.InvariantCulture)) + 1;
                database.AddImages(package.Images, next);
            }
            // Same-volume directory rename makes the complete restored dataset appear at once.
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return Result(destination, document, package.Images);
    }

    internal static void WriteArchive(string path, byte[] data, IReadOnlyList<LmdbImageWrite> images)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes("{\"format\":\"PaperTodoTransfer\",\"version\":1}"));
        WriteEntry(archive, "data.json", data);
        WriteEntry(archive, "images/meta.json", JsonSerializer.SerializeToUtf8Bytes(images.Select(x => x.Asset).ToArray(), JsonOptions));
        foreach (var image in images) WriteEntry(archive, $"images/{image.Asset.Id}", image.Bytes);
    }

    private static (byte[] Data, List<LmdbImageWrite> Images) ReadArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entries.Count >= MaxImageCount + 3) throw new InvalidDataException("传输包文件数量超过限制。");
            var name = entry.FullName;
            if (name is not ("manifest.json" or "data.json" or "images/meta.json") &&
                !(name.StartsWith("images/", StringComparison.Ordinal) && MarkdownImageReferences.IsValidImageId(name[7..])))
                throw new InvalidDataException($"传输包含非法文件路径：{name}");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException($"传输包含重复文件：{name}");
            var limit = name switch { "manifest.json" => 4096, "data.json" => MaxDataBytes, "images/meta.json" => MaxMetadataBytes, _ => MaxImageBytes };
            if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException($"传输包文件过大：{name}");
            total += entry.Length;
            if (total > MaxTotalImageBytes + MaxDataBytes + MaxMetadataBytes + 4096L)
                throw new InvalidDataException("传输包解压大小超过限制。");
        }
        byte[] Required(string name, int limit)
        {
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException($"传输包缺少 {name}。");
            using var stream = entry.Open();
            return ReadLimited(stream, limit);
        }
        using (var manifest = JsonDocument.Parse(Required("manifest.json", 4096)))
        {
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) ||
                format.GetString() != "PaperTodoTransfer" || !root.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var number) || number != 1)
                throw new InvalidDataException("不支持此传输包格式或版本。");
        }
        var data = Required("data.json", MaxDataBytes);
        using var document = ValidateData(data);
        var metadata = JsonSerializer.Deserialize<NoteImageAsset[]>(Required("images/meta.json", MaxMetadataBytes), JsonOptions)
            ?? throw new InvalidDataException("图片元数据为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var images = new List<LmdbImageWrite>();
        long totalImages = 0;
        foreach (var asset in metadata)
        {
            if (asset is null || !MarkdownImageReferences.IsValidImageId(asset.Id)) throw new InvalidDataException("图片 ID 无效。");
            if (!ids.Add(asset.Id)) throw new InvalidDataException($"图片 ID 重复：{asset.Id}");
            var bytes = Required($"images/{asset.Id}", MaxImageBytes);
            ValidateImage(asset, bytes);
            totalImages += bytes.Length;
            if (totalImages > MaxTotalImageBytes) throw new InvalidDataException("图片总大小超过 120 MiB。");
            images.Add(new LmdbImageWrite(asset, bytes));
        }
        if (entries.Count != 3 + images.Count) throw new InvalidDataException("传输包含未声明的图片文件。");
        return (data, images);
    }

    internal static JsonDocument ValidateData(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxDataBytes) throw new InvalidDataException("data.json 为空或超过 16 MiB。");
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException exception) { throw new InvalidDataException("data.json 不是有效的 JSON。", exception); }
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("papers", out var papers) || papers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("data.json 缺少 papers 数组。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var paper in papers.EnumerateArray())
            {
                if (paper.ValueKind != JsonValueKind.Object || !paper.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!))
                    throw new InvalidDataException("纸片 ID 缺失或重复。");
                if (!paper.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("todo" or "note"))
                    throw new InvalidDataException("仅支持 v3.31 的 todo 和 note 纸片。");
            }
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal static void ValidateImage(NoteImageAsset asset, byte[] bytes)
    {
        if (!MarkdownImageReferences.IsValidImageId(asset.Id) || string.IsNullOrWhiteSpace(asset.NoteId) ||
            asset.Width is <= 0 or > MaxDimension || asset.Height is <= 0 or > MaxDimension ||
            asset.ByteLength is <= 0 or > MaxImageBytes || bytes.Length != asset.ByteLength ||
            asset.Mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/tiff" or "image/webp") ||
            asset.Sha256.Length != 64 || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"图片 {asset.Id} 的元数据、大小或 SHA-256 校验失败。");

        // Use Windows' image codecs, matching the desktop application. WebP is not
        // universally installed as a WIC codec, so its standardized RIFF header is
        // validated separately and the original bytes remain untouched.
        if (asset.Mime == "image/webp")
        {
            var dimensions = ReadWebpDimensions(bytes);
            if (dimensions.Width != asset.Width || dimensions.Height != asset.Height)
                throw new InvalidDataException($"图片 {asset.Id} 的 WebP 尺寸与元数据不符。");
            return;
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth != asset.Width || decoder.Frames[0].PixelHeight != asset.Height)
                throw new InvalidDataException($"图片 {asset.Id} 的实际尺寸与元数据不符。");
            var mime = decoder switch { PngBitmapDecoder => "image/png", JpegBitmapDecoder => "image/jpeg", GifBitmapDecoder => "image/gif", BmpBitmapDecoder => "image/bmp", TiffBitmapDecoder => "image/tiff", _ => "" };
            if (mime != asset.Mime) throw new InvalidDataException($"图片 {asset.Id} 的文件格式与元数据不符。");
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException)
        {
            throw new InvalidDataException($"图片 {asset.Id} 无法解码。", exception);
        }
    }

    private static (int Width, int Height) ReadWebpDimensions(byte[] bytes)
    {
        // Read dimensions from the standardized WebP container without decoding the
        // pixels or requiring an optional Windows WebP codec. Other formats use WIC.
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != bytes.Length - 8L)
            throw new InvalidDataException("WebP 容器头无效。");
        static int U24(ReadOnlySpan<byte> value) => value[0] | value[1] << 8 | value[2] << 16;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var start = offset + 8;
            if (size > bytes.Length - start) throw new InvalidDataException("WebP 数据块截断。");
            var kind = bytes.AsSpan(offset, 4);
            var data = bytes.AsSpan(start, (int)size);
            if (kind.SequenceEqual("VP8X"u8) && size >= 10)
                return (U24(data[4..7]) + 1, U24(data[7..10]) + 1);
            if (kind.SequenceEqual("VP8L"u8) && size >= 5 && data[0] == 0x2f)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..5]);
                return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            }
            if (kind.SequenceEqual("VP8 "u8) && size >= 10 && data[3] == 0x9d && data[4] == 0x01 && data[5] == 0x2a)
                return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]) & 0x3fff);
            offset = checked(start + (int)size + ((int)size & 1));
        }
        throw new InvalidDataException("WebP 缺少有效尺寸数据块。");
    }

    private static TransferResult Result(string path, JsonDocument document, IReadOnlyList<LmdbImageWrite> images)
    {
        var ids = images.Select(x => x.Asset.Id).ToHashSet(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var papers = document.RootElement.GetProperty("papers");
        foreach (var paper in papers.EnumerateArray())
            if (paper.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                foreach (var id in MarkdownImageReferences.CollectImageIds(content.GetString()))
                    if (!ids.Contains(id)) missing.Add(id);
        return new TransferResult(path, papers.GetArrayLength(), images.Count, missing.Order(StringComparer.Ordinal).ToArray());
    }

    private static void EnsureDesktopStopped(string sourceDirectory)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var name = process.ProcessName;
                if (!name.Equals("PaperTodo", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("PaperTodo-v", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? executablePath;
                try { executablePath = process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    throw new InvalidOperationException("检测到 PaperTodo 进程，但无法确认其数据目录。请先从托盘退出电脑端，再进行导出。", exception);
                }
                catch (InvalidOperationException) { continue; } // The process exited during enumeration.
                var runningDirectory = executablePath is null ? null : Path.GetDirectoryName(executablePath);
                if (runningDirectory is null || Path.TrimEndingDirectorySeparator(runningDirectory).Equals(
                        Path.TrimEndingDirectorySeparator(sourceDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("检测到此数据文件夹中的 PaperTodo 仍在运行。请先从托盘退出，再进行导出。");
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] ReadLimited(Stream stream, int limit)
    {
        using var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("传输包解压大小超过限制。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"papertodo-transfer-{Guid.NewGuid():N}");
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
