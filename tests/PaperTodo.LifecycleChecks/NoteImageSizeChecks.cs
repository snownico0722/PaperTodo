using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class NoteImageSizeChecks
{
    internal static void Run()
    {
        var cases = new List<(string Name, NoteImageArtifact Artifact)>();
        foreach (var (name, text) in new[]
        {
            ("short-chinese", "# 记录\n\n给自己一点时间，把今天的想法写下来。\n\n- [ ] 明天继续\n\n> 保持好奇心。"),
            ("long-chinese", string.Join("\n\n", Enumerable.Range(0, 350).Select(i =>
                $"第{i}段：记录生活中的细节，窗外树叶摇动，远处传来脚步声。每一个普通的日子，都有值得认真观察的地方。"))),
            ("varied-cjk", string.Join("\n\n", Enumerable.Range(0, 200).Select(i =>
                string.Concat(Enumerable.Range(0, 60).Select(j => (char)(0x4e00 + (i * 137 + j * 53) % 5000)))))),
            ("long-code", "```csharp\n" + string.Join("\n", Enumerable.Range(0, 420).Select(i =>
                $"var value_{i} = Calculate(index + {i}); // retain the current selection")) + "\n```"),
            ("formatted", string.Concat(Enumerable.Repeat("## Heading 中文\n\nText with **strong**, *emphasis* and `code`.\n\n> 引用里的内容保持原样。\n\n- [x] Done\n- [ ] Next\n\n", 45)))
        }) cases.Add((name, NoteImageExport.Compose("测试", text, _ => null, "缺图", CancellationToken.None)));
        foreach (var kind in new[] { "flat", "gradient", "texture", "noise" })
        {
            var source = Source(kind);
            var text = string.Concat(Enumerable.Repeat("## 图文测试\n\n一段文字和一张图片。\n\n![image|100%](i:1)\n\n", 6));
            cases.Add((kind, NoteImageExport.Compose("测试", text, _ => source, "缺图", CancellationToken.None)));
        }
        foreach (var (name, artifact) in cases)
        {
            var watch = Stopwatch.StartNew();
            var estimate = NoteImageExport.MeasureSize(artifact, CancellationToken.None);
            var scanMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using var png = new MemoryStream();
            NoteImageExport.Write(png, artifact, CancellationToken.None);
            var pngMs = watch.Elapsed.TotalMilliseconds;
            CheckEstimate(name + " PNG", estimate.PngBytes, png.Length);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            long jpegBytes = 0;
            watch.Restart();
            try
            {
                if (artifact.PixelHeight <= ushort.MaxValue)
                {
                    NoteImageExport.WriteJpeg(path, artifact, CancellationToken.None);
                    jpegBytes = new FileInfo(path).Length;
                    CheckEstimate(name + " JPG", estimate.JpegBytes, jpegBytes);
                }
                Console.WriteLine("SIZE_ESTIMATE " + JsonSerializer.Serialize(new
                {
                    name, width = artifact.PixelWidth, height = artifact.PixelHeight,
                    pngBytes = png.Length, jpegBytes,
                    scanMs, pngMs, jpegMs = watch.Elapsed.TotalMilliseconds, estimatedPng = estimate.PngBytes, estimatedJpeg = estimate.JpegBytes
                }));
            }
            finally { File.Delete(path); }
        }
    }

    private static void CheckEstimate(string name, long estimate, long actual)
    {
        // Small notes: within 1 MiB, INCLUDING integer rounding. Larger images allow
        // proportional error; this is a regression envelope for these fixtures, not a guarantee.
        var displayBytes = estimate < 1048576 ? 524288d : Math.Round(estimate / 1048576d, MidpointRounding.AwayFromZero) * 1048576;
        var allowance = Math.Max(1048576, actual * .20);
        if (estimate <= 0 || Math.Abs(displayBytes - actual) > allowance)
            throw new InvalidOperationException($"{name}: estimated {estimate}, actual {actual}, rounded {displayBytes}");
    }

    private static NoteImageSource Source(string kind)
    {
        const int width = 1008, height = 640;
        var bytes = new byte[width * height * 3];
        var random = new Random(9147);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var channel = 0; channel < 3; channel++)
                {
                    var smooth = 100 + 50 * Math.Sin(x / 75.0 + channel) + 40 * Math.Cos(y / 92.0 - channel);
                    bytes[(y * width + x) * 3 + channel] = kind switch
                    {
                        "flat" => (byte)180,
                        "noise" => (byte)random.Next(256),
                        "texture" => (byte)Math.Clamp(smooth + random.Next(-14, 15), 0, 255),
                        _ => (byte)Math.Clamp(smooth, 0, 255)
                    };
                }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bytes, width * 3);
        bitmap.Freeze();
        return new NoteImageSource(bitmap, width);
    }
}
