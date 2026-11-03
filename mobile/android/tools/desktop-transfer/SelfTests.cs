using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

internal static class SelfTests
{
    internal static void Run(string? fixturePath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"papertodo-transfer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var checks = 0;
        try
        {
            void Check(bool success, string description)
            {
                if (!success) throw new InvalidOperationException($"自测试失败：{description}");
                checks++;
                Console.WriteLine($"通过：{description}");
            }
            void Reject(Action action, string description)
            {
                try { action(); }
                catch (Exception exception) when (exception is InvalidDataException or IOException or System.Text.Json.JsonException)
                {
                    Check(true, description);
                    return;
                }
                throw new InvalidOperationException($"自测试失败：未拒绝 {description}");
            }

            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var data = Encoding.UTF8.GetBytes("""
                {
                  "futureSetting": {"keep": ["原始字段", 42, true]},
                  "theme": "dark",
                  "papers": [
                    {"id":"note-one","type":"note","title":"合成笔记","x":123.25,"futureField":"保留", "content":"# 示例\n![image|50%](i:001)\n![missing](i:998)\n```markdown\n![not-a-reference](i:999)\n```"},
                    {"id":"todo-one","type":"todo","title":"合成待办","items":[{"id":"item-one","text":"链接","done":true,"order":0,"linkedNoteId":"note-one"}]}
                  ]
                }
                """);
            File.WriteAllBytes(Path.Combine(source, "data.json"), data);
            var imageBytes = MakePng();
            var asset = new NoteImageAsset
            {
                Id = "001", NoteId = "note-one", Mime = "image/png", Width = 2, Height = 3,
                ByteLength = imageBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant(),
                CreatedAt = DateTimeOffset.Parse("2026-10-29T00:00:00Z"), OriginalName = "测试.png"
            };
            var image = new LmdbImageWrite(asset, imageBytes);
            var zeroId = CloneAsset(asset);
            zeroId.Id = "000";
            Transfer.ValidateImage(zeroId, imageBytes);
            Check(true, "保留 v3.31 合法历史图片 ID 000");
            var tiffBytes = MakeImage(new TiffBitmapEncoder());
            var tiffAsset = CloneAsset(asset);
            tiffAsset.Mime = "image/tiff";
            tiffAsset.ByteLength = tiffBytes.Length;
            tiffAsset.Sha256 = Convert.ToHexString(SHA256.HashData(tiffBytes));
            Transfer.ValidateImage(tiffAsset, tiffBytes);
            Check(true, "保留并验证桌面端支持的 TIFF 图片");
            var webpBytes = Convert.FromBase64String("UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA");
            var webpAsset = new NoteImageAsset
            {
                Id = "002", NoteId = "note-one", Mime = "image/webp", Width = 1, Height = 1,
                ByteLength = webpBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(webpBytes)).ToLowerInvariant()
            };
            Transfer.ValidateImage(webpAsset, webpBytes);
            Check(true, "无需可选 Windows 编解码器即可校验 WebP 容器尺寸");
            webpAsset.Width = 2;
            Reject(() => Transfer.ValidateImage(webpAsset, webpBytes), "拒绝 WebP 元数据尺寸与容器不符");
            using (var database = LmdbImageDatabase.Open(Path.Combine(source, "note-assets.lmdb")))
                database.AddImages(new[] { image }, 2);
            var originalDatabaseHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "note-assets.lmdb")));
            var zip = Path.Combine(root, "roundtrip.zip");
            var exported = Transfer.Export(source, zip, checkRunning: false);
            Check(exported.PaperCount == 2 && exported.ImageCount == 1, "真实 LMDB 导出纸片及图片");
            Check(exported.MissingImageIds.SequenceEqual(new[] { "998" }), "保留缺失引用，并跳过代码围栏中的伪引用");
            Check(originalDatabaseHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "note-assets.lmdb")))) &&
                data.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "data.json"))), "导出不改动原 JSON 和 LMDB 字节");
            var restored = Path.Combine(root, "restored");
            Transfer.Restore(zip, restored);
            Check(File.ReadAllBytes(Path.Combine(restored, "data.json")).SequenceEqual(data), "JSON 原始字节、未知字段和桌面坐标完整保留");
            Check(File.ReadAllBytes(Path.Combine(restored, "data.backup.json")).SequenceEqual(data), "恢复时建立相同的有效备份");
            using (var database = LmdbImageDatabase.Open(Path.Combine(restored, "note-assets.lmdb")))
            {
                var index = database.ReadIndex();
                Check(index.Assets.Count == 1 && index.CorruptedImageIds.Count == 0 && index.NextImageNumber == 2, "真实 LMDB 恢复元数据和 nextImageNumber");
                Check(database.TryReadBlob("001", out var restoredBytes) && restoredBytes.SequenceEqual(imageBytes), "PNG 原始字节、图片 ID 及 SHA-256 往返保持");
                Check(index.Assets[0].Width == 2 && index.Assets[0].Height == 3 && index.Assets[0].OriginalName == "测试.png", "图片尺寸及中文名称保持");
            }
            var secondZip = Path.Combine(root, "roundtrip-again.zip");
            Transfer.Export(restored, secondZip, checkRunning: false);
            Check(File.Exists(secondZip), "恢复数据可再次导出为手机传输包");

            var existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "sentinel.txt"), "keep");
            Reject(() => Transfer.Restore(zip, existing), "拒绝覆盖已存在的恢复目录");
            Check(File.ReadAllText(Path.Combine(existing, "sentinel.txt")) == "keep", "拒绝覆盖后已有文件保持不变");
            Reject(() => Transfer.Export(source, zip, false), "拒绝覆盖已存在的导出 ZIP");

            void RejectPackage(string name, Action<ZipArchive>? change = null, IReadOnlyList<LmdbImageWrite>? overrideImages = null, byte[]? overrideData = null)
            {
                var path = Path.Combine(root, $"{name}.zip");
                Transfer.WriteArchive(path, overrideData ?? data, overrideImages ?? new[] { image });
                if (change is not null)
                {
                    using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                    change(archive);
                }
                var target = Path.Combine(root, $"rejected-{name}");
                Reject(() => Transfer.Restore(path, target), name);
                Check(!Directory.Exists(target), $"{name} 不产生半成品恢复目录");
            }
            RejectPackage("拒绝路径穿越", archive => archive.CreateEntry("../escape"));
            RejectPackage("拒绝重复 ZIP 条目", archive => archive.CreateEntry("data.json"));
            RejectPackage("拒绝未声明图片", archive => archive.CreateEntry("images/002"));
            RejectPackage("拒绝缺少图片字节", archive => archive.GetEntry("images/001")!.Delete());
            RejectPackage("拒绝畸形 JSON", overrideData: "{broken"u8.ToArray());
            RejectPackage("拒绝重复纸片 ID", overrideData: "{\"papers\":[{\"id\":\"a\",\"type\":\"note\"},{\"id\":\"a\",\"type\":\"todo\"}]}"u8.ToArray());
            var badHash = CloneAsset(asset);
            badHash.Sha256 = new string('0', 64);
            RejectPackage("拒绝图片哈希不匹配", overrideImages: new[] { new LmdbImageWrite(badHash, imageBytes) });
            var badDimensions = CloneAsset(asset);
            badDimensions.Width = 3;
            RejectPackage("拒绝实际图片尺寸不匹配", overrideImages: new[] { new LmdbImageWrite(badDimensions, imageBytes) });
            var badId = CloneAsset(asset);
            badId.Id = "../../x";
            RejectPackage("拒绝非法图片 ID", overrideImages: new[] { new LmdbImageWrite(badId, imageBytes) });
            RejectPackage("拒绝单图解压超限", archive =>
            {
                archive.GetEntry("images/001")!.Delete();
                using var oversized = archive.CreateEntry("images/001").Open();
                oversized.Write(new byte[Transfer.MaxImageBytes + 1]);
            });

            var emptyZip = Path.Combine(root, "empty-images.zip");
            Transfer.WriteArchive(emptyZip, "{\"papers\":[]}"u8.ToArray(), Array.Empty<LmdbImageWrite>());
            var emptyOutput = Path.Combine(root, "empty-images");
            var emptyResult = Transfer.Restore(emptyZip, emptyOutput);
            Check(emptyResult.ImageCount == 0 && emptyResult.PaperCount == 0, "无图片、无纸片的数据可正常恢复");
            using (var database = LmdbImageDatabase.Open(Path.Combine(emptyOutput, "note-assets.lmdb")))
                Check(database.ReadIndex().NextImageNumber == 1, "空图片库初始化正确");
            Console.WriteLine($"全部 {checks} 项自测试通过。测试仅使用临时合成数据。");
            if (fixturePath is not null)
            {
                var output = Path.GetFullPath(fixturePath);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(zip, output, overwrite: false);
                Console.WriteLine($"跨端合成测试包：{output}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NoteImageAsset CloneAsset(NoteImageAsset source) => new()
    {
        Id = source.Id, NoteId = source.NoteId, Mime = source.Mime,
        Width = source.Width, Height = source.Height, Sha256 = source.Sha256,
        ByteLength = source.ByteLength, CreatedAt = source.CreatedAt, OriginalName = source.OriginalName
    };

    private static byte[] MakePng() => MakeImage(new PngBitmapEncoder());

    private static byte[] MakeImage(BitmapEncoder encoder)
    {
        var pixels = Enumerable.Repeat((byte)180, 2 * 3 * 4).ToArray();
        var source = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
