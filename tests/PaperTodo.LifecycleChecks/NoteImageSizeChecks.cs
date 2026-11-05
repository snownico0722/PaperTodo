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
        CheckStatisticsCost();
        var cases = new List<(string Name, NoteImageArtifact Artifact)>();
        var errors = new List<string>();
        foreach (var (name, text) in new[]
        {
            ("short-chinese", "# 记录\n\n给自己一点时间，把今天的想法写下来。\n\n- [ ] 明天继续\n\n> 保持好奇心。"),
            ("long-chinese", string.Join("\n\n", Enumerable.Range(0, 350).Select(i =>
                $"第{i}段：记录生活中的细节，窗外树叶摇动，远处传来脚步声。每一个普通的日子，都有值得认真观察的地方。"))),
            ("varied-cjk", string.Join("\n\n", Enumerable.Range(0, 200).Select(i =>
                string.Concat(Enumerable.Range(0, 60).Select(j => (char)(0x4e00 + (i * 137 + j * 53) % 5000)))))),
            ("long-code", "```csharp\n" + string.Join("\n", Enumerable.Range(0, 420).Select(i =>
                $"var value_{i} = Calculate(index + {i}); // retain the current selection")) + "\n```"),
            ("english-heldout", string.Join("\n\n", Enumerable.Range(0, 90).Select(i =>
                $"Record {i}. A useful note keeps the original wording, follows the current selection, and leaves room for the next idea. Share the complete result when it is ready."))),
            ("multilingual-heldout", string.Concat(Enumerable.Repeat("## 日常の記録\n\n今日は新しいアイデアを書き留めました。明日も少しずつ進めます。\n\n작은 생각을 기록하고 내일 다시 읽어 봅니다.\n\n", 80))),
            ("formatted", string.Concat(Enumerable.Repeat("## Heading 中文\n\nText with **strong**, *emphasis* and `code`.\n\n> 引用里的内容保持原样。\n\n- [x] Done\n- [ ] Next\n\n", 45)))
        }) cases.Add((name, NoteImageExport.Compose("测试", text, _ => null, "缺图", CancellationToken.None)));
        foreach (var kind in new[] { "flat", "gradient", "texture", "noise" })
        {
            var source = Source(kind);
            var text = string.Concat(Enumerable.Repeat("## 图文测试\n\n一段文字和一张图片。\n\n![image|100%](i:1)\n\n", 6));
            cases.Add((kind, NoteImageExport.Compose("测试", text, _ => source, "缺图", CancellationToken.None)));
        }
        foreach (var kind in new[] { "gradient", "texture", "noise" })
        {
            var source = Source(kind, jpeg: true);
            foreach (var percent in new[] { 50, 100 })
            {
                var text = string.Concat(Enumerable.Repeat($"## 图片记录\n\n![image|{percent}%](i:1)\n\n保留图片和文字。\n\n", 4));
                cases.Add(($"jpeg-{kind}-{percent}-heldout", NoteImageExport.Compose("测试", text, _ => source, "缺图", CancellationToken.None)));
            }
        }
        foreach (var (name, artifact) in cases)
        {
            var estimate = artifact.SizeEstimate;
            var watch = Stopwatch.StartNew();
            using var png = new MemoryStream();
            NoteImageExport.Write(png, artifact, CancellationToken.None);
            var pngMs = watch.Elapsed.TotalMilliseconds;
            CheckEstimate(name + " PNG", estimate.PngBytes, png.Length, errors);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            long jpegBytes = 0;
            watch.Restart();
            try
            {
                if (artifact.PixelHeight <= ushort.MaxValue)
                {
                    NoteImageExport.WriteJpeg(path, artifact, CancellationToken.None);
                    jpegBytes = new FileInfo(path).Length;
                    CheckEstimate(name + " JPG", estimate.JpegBytes, jpegBytes, errors);
                }
                Console.WriteLine("SIZE_ESTIMATE " + JsonSerializer.Serialize(new
                {
                    name, width = artifact.PixelWidth, height = artifact.PixelHeight,
                    pngBytes = png.Length, jpegBytes,
                    pngMs, jpegMs = watch.Elapsed.TotalMilliseconds, estimatedPng = estimate.PngBytes, estimatedJpeg = estimate.JpegBytes
                }));
            }
            finally { File.Delete(path); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
    }

    private static void CheckStatisticsCost()
    {
        var text = string.Concat(Enumerable.Repeat("记录今天的想法，保留文字与图片。A useful note keeps the original wording. ", 1000));
        double RunOnce()
        {
            var watch = Stopwatch.StartNew();
            var estimate = new NoteImageSizeEstimate();
            estimate.AddText(text, 18);
            for (var i = 0; i < 100; i++) estimate.AddImage(250000, "image/jpeg", 1200, 800, 1008, 672);
            var result = estimate.Finish(1200, 60000);
            if (result.JpegBytes <= 0 || result.PngBytes <= 0) throw new InvalidOperationException("Missing size hints");
            return watch.Elapsed.TotalMilliseconds;
        }
        RunOnce(); // JIT warm-up, never reported as a steady-state estimate.
        var times = Enumerable.Range(0, 9).Select(_ => RunOnce()).Order().ToArray();
        Console.WriteLine("SIZE_METADATA_COST " + JsonSerializer.Serialize(new { characters = text.Length, images = 100, medianMs = times[4], maxMs = times[^1] }));
        if (times[4] > 100) throw new InvalidOperationException("Metadata estimate exceeded its 100ms regression budget");
    }

    private static void CheckEstimate(string name, long estimate, long actual, List<string> errors)
    {
        // Coarse UI: a half-MiB absolute allowance for small images, 50% for larger
        // ones. Report raw sizes as well; this fixture envelope is not a universal bound.
        var allowance = Math.Max(524288, actual * .50);
        if (estimate <= 0 || Math.Abs(estimate - actual) > allowance)
            errors.Add($"{name}: estimated {estimate}, actual {actual}, relative error {Math.Abs(estimate / (double)actual - 1):P1}");
    }

    private static NoteImageSource Source(string kind, bool jpeg = false)
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
        // Fixture setup encodes once to represent a stored asset. Prediction never encodes.
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 82 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        var decoded = BitmapFrame.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        decoded.Freeze();
        return new NoteImageSource(decoded, width, encoded.Length, jpeg ? "image/jpeg" : "image/png", height);
    }
}
