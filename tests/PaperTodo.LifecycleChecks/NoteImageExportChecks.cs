using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class NoteImageExportChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task Run()
    {
        await NoteImageExport.OnStaAsync(() => { CheckRendering(); return true; });
        await CheckEditorSnapshot();
    }

    private static void CheckRendering()
    {
        var token = CancellationToken.None;
        CheckContentCompatibility();
        var sample = NoteImageExport.Compose("把想法，写成自己的样子", """
            # 把想法，写成自己的样子

            有些念头值得留下。随手写下的一句话，也可以成为一篇认真分享的笔记。

            ## 留一点空间，给思考

            好的排版让阅读更轻松：**重要的内容自然突出**，<u>想强调的话也能留下划线</u>。中文与 English、日本語、한국어 都留在同一页。

            > 不必等到想清楚才开始记录。
            > 写下来的过程，本身就是整理。

            ![illustration|100%](i:123)

            ## 从记录，到行动

            - [x] 记下今天最想做的一件事
            - [ ] 留下一个明天可以接着写的开头
            - 一张纸，容纳文字、图片和灵感
              - 也容纳那些尚未成形的想法

            用 `Ctrl + S` 保存片刻，也用 [一张长图](https://example.com) 分享给朋友。

            ```text
            idea = "先写下来"
            next_step = "慢慢把它做好"
            ```

            ---

            **愿每一次记录，都有值得回看的意义。**
            """, id => id == "123" ? Illustration() : null, "图片缺失", token);
        Check(sample.Drawing.IsFrozen && sample.PixelWidth == 1200 && sample.MissingImages == 0,
            "Export artifact must be frozen, fixed-width and complete");
        var words = Text(sample.Drawing);
        Check(words.Contains("愿每一次记录") && words.Contains("PaperToDo") && !words.Contains("i:123"),
            "Body end, image replacement or footer lost");
        Check(words.Split("把想法，写成自己的样子").Length == 2, "Title repeated above identical first H1");
        using var png = new MemoryStream();
        NoteImageExport.Write(png, sample, token);
        var artifacts = Environment.GetEnvironmentVariable("PAPERTODO_IMAGE_EXPORT_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            File.WriteAllBytes(Path.Combine(artifacts, "note-share-sample.png"), png.ToArray());
        }
        png.Position = 0;
        var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.PixelWidth == sample.PixelWidth && frame.PixelHeight == sample.PixelHeight,
            "PNG did not round-trip through the native decoder");

        var jpgPath = Path.Combine(AppContext.BaseDirectory, "note-share-sample.jpg");
        try
        {
            NoteImageExport.Save(jpgPath, sample, token);
            var bytes = File.ReadAllBytes(jpgPath);
            Check(bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8,
                "Default export did not write a JPEG file");
            using var jpg = new MemoryStream(bytes);
            var jpeg = BitmapDecoder.Create(jpg,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(jpeg.PixelWidth == sample.PixelWidth && jpeg.PixelHeight == sample.PixelHeight &&
                  NoteImageJpegWriter.Quality == 90, "Default JPEG quality or image dimensions changed");
            if (!string.IsNullOrWhiteSpace(artifacts))
                File.WriteAllBytes(Path.Combine(artifacts, "note-share-sample.jpg"), bytes);

            // A decodable JPEG with the right dimensions can still contain garbage
            // when native pixel-buffer marshaling is wrong; verify actual colors.
            var expectedJpeg = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            var actualJpeg = new FormatConvertedBitmap(jpeg, PixelFormats.Bgr24, null, 0);
            foreach (var row in new[] { 0, 255, 511, 512, 1023, 1024, sample.PixelHeight - 1 })
            {
                var expectedRow = new byte[sample.PixelWidth * 3];
                var actualRow = new byte[expectedRow.Length];
                var area = new Int32Rect(0, row, sample.PixelWidth, 1);
                expectedJpeg.CopyPixels(area, expectedRow, expectedRow.Length, 0);
                actualJpeg.CopyPixels(area, actualRow, actualRow.Length, 0);
                var error = 0L;
                for (var channel = 0; channel < expectedRow.Length; channel++)
                    error += Math.Abs(expectedRow[channel] - actualRow[channel]);
                Check(error < expectedRow.Length * 20L,
                    "JPEG content differs too far from the preview at row " + row);
            }
        }
        finally { File.Delete(jpgPath); }

        // Compare several rows around stripe seams to the same immutable Drawing rendered
        // in one operation. This is independent of live AvalonEdit repaint timing.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(2, 2)); dc.DrawDrawing(sample.Drawing);
        }
        var reference = new RenderTargetBitmap(sample.PixelWidth, sample.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        reference.Render(visual);
        var expectedRgb = new FormatConvertedBitmap(reference, PixelFormats.Bgr24, null, 0);
        var decoded = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        foreach (var row in new[] { 0, 511, 512, 1023, 1024, sample.PixelHeight - 1 })
        {
            var expected = new byte[sample.PixelWidth * 3]; var actual = new byte[expected.Length];
            var bounds = new Int32Rect(0, row, sample.PixelWidth, 1);
            expectedRgb.CopyPixels(bounds, expected, expected.Length, 0);
            decoded.CopyPixels(bounds, actual, actual.Length, 0);
            if (!expected.SequenceEqual(actual))
            {
                var differences = Enumerable.Range(0, expected.Length).Where(i => expected[i] != actual[i]).ToArray();
                if (!string.IsNullOrWhiteSpace(artifacts))
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(reference));
                    using var file = File.Create(Path.Combine(artifacts, "note-share-reference.png")); encoder.Save(file);
                }
                throw new InvalidOperationException($"PNG stripe seam at {row}: {differences.Length} channels; " +
                    string.Join(", ", differences.Take(8).Select(i => $"{i}: {expected[i]} != {actual[i]}")));
            }
        }

        var missing = NoteImageExport.Compose("Missing image", "before\n\n![](i:999)\n\nafter", _ => null, "missing", token);
        Check(missing.MissingImages == 1 && Text(missing.Drawing).Contains("after") && Text(missing.Drawing).Contains("[missing]"),
            "Missing image silently removed or truncated the note");
        var shortCode = NoteImageExport.Compose("", "```\nabc\n```", _ => null, "missing", token);
        var longCode = NoteImageExport.Compose("", "```\n" + new string('W', 1500) + "\n```", _ => null, "missing", token);
        Check(longCode.Height > shortCode.Height + 100, "Long code was clipped instead of wrapped");

        var longNote = NoteImageExport.Compose("Long note", string.Join("\n\n", Enumerable.Repeat("正文完整保留。Entire note, including the last paragraph.", 400)) + "\n\nEND_OF_NOTE", _ => null, "missing", token);
        Check(longNote.PixelHeight > 32767 && Text(longNote.Drawing).Contains("END_OF_NOTE"), "Long-note fixture did not cross bitmap height limits");
        using var longPng = new MemoryStream();
        NoteImageExport.Write(longPng, longNote, token);
        CheckScanlines(longPng.ToArray(), longNote.PixelWidth, longNote.PixelHeight);
        var longJpgPath = Path.Combine(AppContext.BaseDirectory, "note-share-long.jpg");
        try
        {
            NoteImageExport.Save(longJpgPath, longNote, token);
            using var stream = File.OpenRead(longJpgPath);
            var longJpg = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(longJpg.PixelWidth == longNote.PixelWidth &&
                  longJpg.PixelHeight == longNote.PixelHeight,
                "Long JPEG was truncated or unreadable");
        }
        finally { File.Delete(longJpgPath); }

        var destination = Path.Combine(AppContext.BaseDirectory, "export-cancel.png");
        File.WriteAllText(destination, "previous image");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { NoteImageExport.Save(destination, sample, cancelled.Token, NoteImageFormat.Png); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(File.ReadAllText(destination) == "previous image" &&
              !Directory.EnumerateFiles(AppContext.BaseDirectory, ".export-cancel.png.*.tmp").Any(),
            "Cancelled export replaced destination or left an intermediate file");

        var jpegDestination = Path.Combine(AppContext.BaseDirectory, "export-cancel.jpg");
        File.WriteAllText(jpegDestination, "previous image");
        try { NoteImageExport.Save(jpegDestination, sample, cancelled.Token); throw new Exception("JPEG cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(File.ReadAllText(jpegDestination) == "previous image" &&
              !Directory.EnumerateFiles(AppContext.BaseDirectory, ".export-cancel.jpg.*.tmp").Any(),
            "Cancelled JPEG replaced the destination or left a temporary file");
        File.Delete(jpegDestination);

        Console.WriteLine($"PASS note image: {sample.PixelWidth}x{sample.PixelHeight}; long note {longNote.PixelHeight}px; native decode, stripe seams, complete scanlines and atomic cancellation");
    }

    private static void CheckContentCompatibility()
    {
        NoteImageArtifact Compose(string markdown, NoteImageSource? image = null) =>
            NoteImageExport.Compose("", markdown, _ => image, "missing", CancellationToken.None);

        var entities = Compose("A &amp; B &lt; C &gt; D &#x4E2D;&#25991; &quot;quoted&quot; &#x1F642;");
        Check(Text(entities.Drawing).Contains("A & B < C > D 中文 \"quoted\" 🙂"),
            "Named/numeric entities were dropped or left encoded");
        Check(Text(Compose("`&amp; <u>code</u>`").Drawing).Contains("&amp; <u>code</u>"),
            "Literal inline code was decoded or formatted as HTML");

        foreach (var (html, markdown) in new[]
        {
            ("<strong>bold</strong>", "**bold**"),
            ("<em>italic</em>", "*italic*"),
            ("<b><i>nested</i></b>", "**_nested_**"),
            ("<del>removed</del>", "~~removed~~"),
            ("<code>code</code>", "`code`"),
            ("<a href=\"https://example.com\">link</a>", "[link](https://example.com)"),
            ("**<u>mixed</u>**", "<u><b>mixed</b></u>")
        })
        {
            var actual = Compose(html); var expected = Compose(markdown);
            Check(GlyphStyles(actual.Drawing) == GlyphStyles(expected.Drawing) &&
                  GeometryCount(actual.Drawing) == GeometryCount(expected.Drawing),
                "Supported HTML differs from equivalent formatting: " + html);
        }
        var underlined = Compose("<u>underlined</u>");
        Check(Text(underlined.Drawing).Contains("underlined") && !Text(underlined.Drawing).Contains("<u>") &&
              GeometryCount(underlined.Drawing) > GeometryCount(Compose("underlined").Drawing),
            "HTML underline was not drawn");
        Check(GeometryCount(Compose("<u><s>both</s></u>").Drawing) > GeometryCount(Compose("~~both~~").Drawing),
            "Nested underline/strike lost one decoration");
        foreach (var literal in new[]
        {
            "<span>plain</span>", "<b class=\"x\">plain</b>", "<b><i>crossed</b></i>",
            "<a href=\"javascript:alert(1)\">plain</a>", "<div>plain</div>"
        })
            Check(Text(Compose(literal).Drawing).Contains(literal), "Unsupported HTML was hidden: " + literal);
        Check(Text(Compose("\\<u>escaped\\</u>").Drawing).Contains("<u>escaped</u>"),
            "Escaped tags were treated as formatting");

        NoteImageSource Source(int originalWidth, int decodedWidth)
        {
            var pixels = new byte[decodedWidth * (decodedWidth / 2) * 4];
            var bitmap = BitmapSource.Create(decodedWidth, decodedWidth / 2, 96, 96,
                PixelFormats.Bgr32, null, pixels, decodedWidth * 4);
            bitmap.Freeze();
            return new NoteImageSource(bitmap, originalWidth);
        }
        var small = Source(200, 200); var large = Source(2400, 1200);
        foreach (var (markdown, source, width) in new[]
        {
            ("![image](i:123)", small, 200.0),
            ("![image|100%](i:123)", small, 200.0),
            ("![image|50%](i:123)", small, 100.0),
            ("![image](i:123){width=50%}", small, 252.0),
            ("![image|300x150,50%](i:123)", small, 150.0),
            ("![image|10%](i:123)", large, 240.0),
            ("![image|100%](i:123)", large, 504.0)
        })
        {
            var drawing = Drawings(Compose(markdown, source).Drawing).OfType<ImageDrawing>().Single();
            Check(Math.Abs(drawing.Rect.Width - width) < .01, "Image width changed: " + markdown);
            Check(Math.Abs(drawing.Rect.Width / drawing.Rect.Height - 2) < .01, "Image aspect ratio changed");
        }
        Console.WriteLine("PASS note image compatibility: entities, shared HTML styles and literal boundaries, original-size image scaling");
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Drawings(child)) yield return nested;
    }

    private static int GeometryCount(Drawing drawing) => Drawings(drawing).OfType<GeometryDrawing>().Count();

    private static string GlyphStyles(Drawing drawing) => string.Join("|", Drawings(drawing).OfType<GlyphRunDrawing>()
        .Select(glyph => $"{new string(glyph.GlyphRun.Characters?.ToArray() ?? [])}:" +
            $"{glyph.GlyphRun.GlyphTypeface.FontUri}:{glyph.GlyphRun.GlyphTypeface.Weight}:" +
            $"{glyph.GlyphRun.GlyphTypeface.StyleSimulations}:{glyph.ForegroundBrush}"));

    private static async Task CheckEditorSnapshot()
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = false,
            UseDeepCapsuleMode = false, McpEnabled = false, UsePersistentPowerShellProcess = false,
            Papers = [new() { Id = "export-note", Type = PaperTypes.Note, Content = "old saved text",
                IsVisible = true, X = 100, Y = 100, Width = 350, Height = 300 }]
        };
        var store = new StateStore(); store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var windows = (Dictionary<string, PaperWindow>)Member(controller, "_windows");
        var paper = windows["export-note"];
        controller.OpenExpandedEditorFor(paper);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var box = (MarkdownTextBox)Member(paper, "_noteBox");
        box.Text = "latest unsaved text\n\n末尾仍然存在";
        box.Select(2, 6);
        var selected = box.SelectedText;
        Exception? failure = null;
        var completed = false;
        // ShowDialog runs a nested Dispatcher. Close the real preview after preparation,
        // then assert that the source editor and its selected text are still intact.
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            NoteImageExportWindow? preview = null;
            try
            {
                preview = Application.Current.Windows.OfType<NoteImageExportWindow>().Single();
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (MemberOrNull(preview, "_artifact") == null)
                {
                    if (started.Elapsed.TotalSeconds > 8) throw new TimeoutException("Image preview did not finish");
                    await Task.Delay(10);
                }
                var artifact = (NoteImageArtifact)Member(preview, "_artifact");
                Check(preview.Owner == paper.ExpandedEditorHostWindow, "Preview has hidden paper owner");
                var formatButton = (Button)Member(preview, "_formatToggle");
                var sizeLabel = (TextBlock)Member(preview, "_estimatedSize");
                var saveButton = (Button)Member(preview, "_save");
                var frame = (Border)Member(preview, "_frame");
                Check(preview.WindowStyle == WindowStyle.None && frame.CornerRadius.TopLeft == 12 &&
                      saveButton.Style != null && Equals(formatButton.Content, "JPG") &&
                      sizeLabel.Text == Strings.Get("EditorImageExportEstimatedSize"),
                    "Preview does not use the themed window, rounded save button or size placeholder");
                formatButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(Equals(formatButton.Content, "PNG") &&
                      Equals(Member(preview, "_preferredFormat"), NoteImageFormat.Png),
                    "Preview did not switch its preferred file format to PNG");
                formatButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(Equals(formatButton.Content, "JPG") &&
                      Equals(Member(preview, "_preferredFormat"), NoteImageFormat.Jpeg),
                    "Preview did not switch its preferred file format back to JPG");
                Check(Text(artifact.Drawing).Contains("latest unsaved text"), "Export read the debounced model instead of live editor");
                Check(Text(artifact.Drawing).Contains("末尾仍然存在"), "Export lost final paragraph");
            }
            catch (Exception ex) { failure = ex; }
            finally { completed = true; preview?.Close(); }
        }, DispatcherPriority.Background);
        typeof(PaperWindow).GetMethod("ExportNoteAsImage", Private)!.Invoke(paper, null);
        if (failure != null) throw failure;
        Check(completed && box.Text.Contains("latest unsaved text") && box.SelectedText == selected,
            "Export modified the document or its selection");
    }

    private static void CheckScanlines(byte[] png, int width, int height)
    {
        using var idat = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") idat.Write(png, offset + 8, count);
            offset += count + 12;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var buffer = new byte[64 * 1024]; long total = 0; int read;
        while ((read = zlib.Read(buffer)) > 0) total += read;
        Check(total == (long)(width * 3 + 1) * height, "Long PNG is missing scanlines");
    }

    private static NoteImageSource Illustration()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 233, 220)), null, new Rect(0, 0, 1008, 480));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(238, 207, 156)), null, new Point(760, 120), 68, 68);
            var hills = Geometry.Parse("M 0 340 Q 220 120 460 330 Q 750 120 1008 270 L 1008 480 L 0 480 Z");
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(126, 152, 129)), null, hills);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(81, 115, 101)), null,
                Geometry.Parse("M 0 400 Q 300 230 600 420 Q 850 310 1008 370 L 1008 480 L 0 480 Z"));
        }
        var bitmap = new RenderTargetBitmap(1008, 480, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return new NoteImageSource(bitmap, 1008);
    }

    private static string Text(Drawing drawing) => drawing switch
    {
        GlyphRunDrawing glyph => new string(glyph.GlyphRun.Characters?.ToArray() ?? []),
        DrawingGroup group => string.Concat(group.Children.Select(Text)),
        _ => ""
    };
    private static object? MemberOrNull(object value, string name) =>
        value.GetType().GetField(name, Private)?.GetValue(value) ??
        value.GetType().GetProperty(name, Private)?.GetValue(value);
    private static object Member(object value, string name) => MemberOrNull(value, name) ?? throw new MissingMemberException(name);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
