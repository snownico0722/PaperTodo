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
        CheckHeaderHints();
        CheckPngHeaderHints();
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
        const string calibrationText = "晨光照进窗边，今天写下几句简单的记录。The quiet morning leaves room for a useful idea.";
        foreach (var (name, line) in new[]
        {
            ("text-japanese-calibration", "今日は新しいアイデアを書き留めました。静かな朝、窓を開けて空を見上げます。少しずつ前に進んでいきましょう。"),
            ("text-korean-calibration", "오늘의 작은 생각을 기록합니다. 창문을 열고 맑은 하늘을 바라보며 새로운 계획을 세웁니다. 천천히 한 걸음씩 나아갑니다."),
            ("text-plain-calibration", calibrationText),
            ("text-quote-calibration", "> " + calibrationText),
            ("text-bold-calibration", "**" + calibrationText + "**"),
            ("text-inlinecode-calibration", "`" + calibrationText + "`"),
            ("text-list-calibration", "- [x] " + calibrationText),
            ("text-english-calibration", "A short note records what changed, why it matters, and the next step. Keep the details clear and leave enough time to review the result.")
        }) cases.Add((name, NoteImageExport.Compose("校准", string.Join("\n\n", Enumerable.Repeat(line, 80)), _ => null, "缺图", CancellationToken.None)));
        foreach (var (name, text) in new[]
        {
            ("new-japanese-heldout", string.Concat(Enumerable.Repeat("## 旅のメモ\n\n駅を出ると、小さな商店街が続いていました。帰り道には温かいお茶を買って、今日の出来事を友達に話しました。\n\n", 65))),
            ("new-korean-heldout", string.Concat(Enumerable.Repeat("## 여행 기록\n\n기차에서 내려 작은 골목을 걸었습니다. 오래된 서점에서 책을 읽고 따뜻한 차를 마시며 하루를 마무리했습니다.\n\n", 65))),
            ("new-english-heldout", string.Join("\n\n", Enumerable.Range(0, 95).Select(i => $"Entry {i}: The project review covered open questions, user feedback, and the release schedule. We agreed to simplify the next version and verify its behavior with fresh examples."))),
            ("new-rich-heldout", string.Concat(Enumerable.Repeat("## 项目记录\n\n今天完成了 **界面检查**，接下来整理 `export_size` 的结果。\n\n> 每次调整都留下依据，再用新的例子复核。\n\n- [x] 检查正文\n- [ ] 对照图文\n\n```csharp\nvar result = Measure(document);\nreturn result.Size;\n```\n\n", 50)))
        }) cases.Add((name, NoteImageExport.Compose("新笔记", text, _ => null, "缺图", CancellationToken.None)));
        foreach (var kind in new[] { "flat", "gradient", "texture", "noise" })
        {
            var source = Source(kind);
            var text = string.Concat(Enumerable.Repeat("## 图文测试\n\n一段文字和一张图片。\n\n![image|100%](i:123)\n\n", 6));
            cases.Add((kind, NoteImageExport.Compose("测试", text, _ => source, "缺图", CancellationToken.None)));
        }
        foreach (var kind in new[] { "gradient", "texture", "noise" })
        {
            var source = Source(kind, jpeg: true);
            var previousHeight = 0;
            foreach (var percent in new[] { 50, 100 })
            {
                var text = string.Concat(Enumerable.Repeat($"## 图片记录\n\n![image](i:123){{width={percent}%}}\n\n保留图片和文字。\n\n", 4));
                var artifact = NoteImageExport.Compose("测试", text, _ => source, "缺图", CancellationToken.None);
                if (artifact.PixelHeight <= previousHeight) throw new InvalidOperationException("Image scale fixture did not change the output size");
                previousHeight = artifact.PixelHeight;
                cases.Add(($"jpeg-{kind}-{percent}-validation", artifact));
            }
        }
        foreach (var kind in new[] { "gradient", "texture" })
        {
            var source = Source(kind, jpeg: true, quality: 95);
            var text = string.Concat(Enumerable.Repeat("## 分享\n\n![image](i:123){width=25%}\n\n随手记下今天的事情。\n\n", 5));
            cases.Add(($"jpeg95-{kind}-25-heldout", NoteImageExport.Compose("记录", text, _ => source, "缺图", CancellationToken.None)));
        }
        foreach (var kind in new[] { "gradient", "texture", "noise" })
        {
            var source = Source(kind, jpeg: true, quality: 65);
            var text = string.Concat(Enumerable.Repeat("## 低画质原图\n\n![image](i:123){width=75%}\n\n新的缩放与压缩参数。\n\n", 4));
            cases.Add(($"jpeg65-{kind}-75-heldout", NoteImageExport.Compose("图片笔记", text, _ => source, "缺图", CancellationToken.None)));
        }
        foreach (var mode in new[] { "gray", "palette", "rgba", "bilevel" })
        {
            var source = Source("texture", pngMode: mode);
            foreach (var percent in new[] { 50, 100 })
            {
                var text = string.Concat(Enumerable.Repeat($"## PNG 格式验证\n\n![image](i:123){{width={percent}%}}\n\n保留图片和颜色。\n\n", 4));
                cases.Add(($"png-{mode}-{percent}-heldout", NoteImageExport.Compose("图片记录", text, _ => source, "缺图", CancellationToken.None)));
            }
        }
        foreach (var (name, artifact) in cases)
        {
            var estimate = artifact.SizeEstimate;
            var watch = Stopwatch.StartNew();
            using var png = new MemoryStream();
            NoteImageExport.Write(png, artifact, CancellationToken.None);
            var pngMs = watch.Elapsed.TotalMilliseconds;
            if (!name.EndsWith("-calibration", StringComparison.Ordinal)) CheckEstimate(name + " PNG", estimate.PngBytes, png.Length, errors);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            long jpegBytes = 0;
            watch.Restart();
            try
            {
                if (artifact.PixelHeight <= ushort.MaxValue)
                {
                    NoteImageExport.WriteJpeg(path, artifact, CancellationToken.None);
                    jpegBytes = new FileInfo(path).Length;
                    if (!name.EndsWith("-calibration", StringComparison.Ordinal)) CheckEstimate(name + " JPG", estimate.JpegBytes, jpegBytes, errors);
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

    private static byte[] HeaderFixture()
    {
        // A non-zero table ID and an APP segment before the tables exercise marker parsing.
        var bytes = new List<byte> { 0xff, 0xd8, 0xff, 0xe1, 0, 6, 1, 2, 3, 4, 0xff, 0xdb, 0, 67, 2 };
        bytes.AddRange(Enumerable.Repeat((byte)20, 64));
        bytes.AddRange(new byte[] { 0xff, 0xc0, 0, 17, 8, 3, 32, 4, 176, 3,
            1, 0x22, 2, 2, 0x11, 2, 3, 0x11, 2, 0xff, 0xda, 0, 12,
            3, 1, 0, 2, 0, 3, 0, 0, 63, 0 });
        bytes.AddRange(new byte[250000]);
        return bytes.ToArray();
    }

    private static void CheckHeaderHints()
    {
        var bytes = HeaderFixture();
        var info = NoteImageJpegInfo.Read(bytes);
        if (info.Quantization != 20 || info.Sampling != 4 || info.PayloadBytes != 250012)
            throw new InvalidOperationException("JPEG header hints did not follow the frame's quantization table");
        // Corrupt or incomplete metadata must never turn a size hint into an export failure.
        for (var n = 0; n < 100; n++) _ = NoteImageJpegInfo.Read(bytes.AsSpan(0, n));
        var invalid = (byte[])bytes.Clone();
        invalid[14] = 0xf2;
        if (NoteImageJpegInfo.Read(invalid) != default)
            throw new InvalidOperationException("Invalid quantization precision was accepted");
        invalid = (byte[])bytes.Clone();
        invalid[4] = 0;
        invalid[5] = 1;
        if (NoteImageJpegInfo.Read(invalid) != default)
            throw new InvalidOperationException("Invalid JPEG segment length was accepted");
        var withMetadata = new List<byte>(bytes.Take(2));
        withMetadata.AddRange(new byte[] { 0xff, 0xe2, 0xff, 0xff });
        withMetadata.AddRange(new byte[65533]);
        withMetadata.AddRange(bytes.Skip(2));
        if (NoteImageJpegInfo.Read(withMetadata.ToArray()) != info)
            throw new InvalidOperationException("APP metadata changed the predicted image payload");
        var original = new NoteImageSizeEstimate();
        var padded = new NoteImageSizeEstimate();
        original.AddImage(bytes.Length, "image/jpeg", 1200, 800, 1008, 672, info);
        padded.AddImage(withMetadata.Count, "image/jpeg", 1200, 800, 1008, 672, info);
        if (original.Finish(1200, 1000) != padded.Finish(1200, 1000))
            throw new InvalidOperationException("APP metadata changed the file size hint");
    }

    private static byte[] PngHeaderFixture(bool metadata = false, bool transparency = true)
    {
        var bytes = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        void Chunk(string kind, byte[] data)
        {
            var size = data.Length;
            bytes.AddRange(new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size });
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(kind));
            bytes.AddRange(data);
            bytes.AddRange(new byte[4]); // The bitmap decoder owns CRC validation, not this hint reader.
        }
        Chunk("IHDR", new byte[] { 0, 0, 3, 240, 0, 0, 2, 128, 4, 3, 0, 0, 0 });
        Chunk("PLTE", new byte[48]);
        if (transparency) Chunk("tRNS", new byte[] { 0, 255 });
        if (metadata) Chunk("tEXt", new byte[100000]);
        Chunk("IDAT", new byte[25000]);
        Chunk("IDAT", new byte[25000]);
        Chunk("IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void CheckPngHeaderHints()
    {
        var bytes = PngHeaderFixture();
        var info = NoteImagePngInfo.Read(bytes);
        if (info.PayloadBytes != 50000 || info.BitDepth != 4 || info.ColorType != 3 || info.PaletteColors != 16 || !info.HasAlpha)
            throw new InvalidOperationException("PNG chunk metadata was not read correctly");
        var padded = PngHeaderFixture(metadata: true);
        if (NoteImagePngInfo.Read(padded) != info)
            throw new InvalidOperationException("PNG text metadata changed the payload hint");
        var first = new NoteImageSizeEstimate();
        var second = new NoteImageSizeEstimate();
        first.AddImage(bytes.Length, "image/png", 1008, 640, 1008, 640, pngInfo: info);
        second.AddImage(padded.Length, "image/png", 1008, 640, 1008, 640, pngInfo: info);
        if (first.Finish(1200, 1000) != second.Finish(1200, 1000))
            throw new InvalidOperationException("PNG text metadata changed the prediction");
        for (var n = 0; n < 100; n++) _ = NoteImagePngInfo.Read(bytes.AsSpan(0, n));
        var invalid = (byte[])bytes.Clone();
        invalid[8] = 255;
        if (NoteImagePngInfo.Read(invalid) != default) throw new InvalidOperationException("Invalid PNG chunk length was accepted");
    }

    private static void CheckStatisticsCost()
    {
        var text = string.Concat(Enumerable.Repeat("记录今天的想法，保留文字与图片。A useful note keeps the original wording. ", 1000));
        var header = HeaderFixture();
        var pngHeader = PngHeaderFixture(transparency: false);
        double RunOnce()
        {
            var watch = Stopwatch.StartNew();
            var estimate = new NoteImageSizeEstimate();
            estimate.AddText(text, 18);
            for (var i = 0; i < 100; i++)
            {
                if (i % 2 == 0) estimate.AddImage(header.Length, "image/jpeg", 1200, 800, 1008, 672, NoteImageJpegInfo.Read(header));
                else estimate.AddImage(pngHeader.Length, "image/png", 1008, 640, 1008, 640, pngInfo: NoteImagePngInfo.Read(pngHeader));
            }
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

    private static NoteImageSource Source(string kind, bool jpeg = false, int quality = 82, string pngMode = "rgb")
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
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bytes, width * 3);
        if (pngMode == "gray") bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        else if (pngMode == "bilevel") bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.BlackWhite, null, 0);
        else if (pngMode == "palette")
        {
            var colors = Enumerable.Range(0, 16).Select(i => Color.FromRgb((byte)((i % 4) * 85), (byte)((i / 4) * 85), (byte)(255 - i * 17))).ToList();
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Indexed4, new BitmapPalette(colors), 0);
        }
        else if (pngMode == "rgba")
        {
            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = y * width + x;
                    for (var c = 0; c < 3; c++) rgba[pixel * 4 + c] = bytes[pixel * 3 + c];
                    var radius = Math.Pow((x - width * .45) / (width * .55), 2) + Math.Pow((y - height * .45) / (height * .6), 2);
                    rgba[pixel * 4 + 3] = (byte)(255 * Math.Clamp(1 - radius, 0, 1));
                }
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
        }
        // The conversion chain stays on this STA; only the decoded asset below is frozen.
        // Fixture setup encodes once to represent a stored asset. Prediction never encodes.
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = quality } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        var decoded = BitmapFrame.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        decoded.Freeze();
        return new NoteImageSource(decoded, width, encoded.Length, jpeg ? "image/jpeg" : "image/png", height,
            jpeg ? NoteImageJpegInfo.Read(encoded.GetBuffer().AsSpan(0, (int)encoded.Length)) : default,
            !jpeg ? NoteImagePngInfo.Read(encoded.GetBuffer().AsSpan(0, (int)encoded.Length)) : default);
    }
}
