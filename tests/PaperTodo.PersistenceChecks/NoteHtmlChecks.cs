using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class NoteHtmlChecks
{
    internal static void Run()
    {
        using var temp = new TempDirectory();
        using var images = new NoteImageStore(Path.Combine(temp.Path, "note-assets.lmdb"));
        images.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] {0,0,255,255,0,255,0,255,255,0,0,255,0,0,0,255}, 8);
        bitmap.Freeze();
        var asset = images.ImportBitmapSource("test-note", bitmap);
        var missing = new HashSet<string>();
        var markdown = "# Heading\n\n**bold** and ~~deleted~~\n\n- [x] done\n\n" +
            "![image|50%](i:" + asset.Id + ")\n\n" +
            "![image](i:" + asset.Id + "){width=75px}\n\n" +
            "[safe](https://example.com) [unsafe](javascript:alert(1))\n\n" +
            "<script>alert(1)</script>";
        var html = NoteHtmlExport.Render("test-note", "<title>", markdown, images, "Missing image", missing);
        Require(missing.Count == 0 && html.Contains("data:image/") &&
                html.Contains("<strong>bold</strong>") && html.Contains("<del>deleted</del>") &&
                html.Contains("type=\"checkbox\"") && html.Contains("width:1px") &&
                html.Contains("width:75px") && !html.Contains("{width=75px}") &&
                !html.Contains("href=\"javascript:") && !html.Contains("<script>") &&
                html.Contains("&lt;title&gt;"), "HTML portable rendering");

        // More than one Base64 chunk: the streaming renderer must not introduce
        // padding or alter the original compressed image between chunks.
        var pixels = new byte[320 * 320 * 4];
        new Random(42).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var largeBitmap = BitmapSource.Create(320, 320, 96, 96, PixelFormats.Bgra32,
            null, pixels, 320 * 4);
        largeBitmap.Freeze();
        var larger = images.ImportBitmapSource("test-note", largeBitmap);
        Require(images.TryGetEncodedImageBytes(larger.Id, out _, out var expectedBytes) &&
                expectedBytes.Length > 24 * 1024, "large image did not cross a Base64 chunk");
        var largePath = Path.Combine(temp.Path, "large.html");
        var largeExport = NotePortableExport.Export(largePath,
            [new NoteExportDocument("test-note", "Large", "![large](i:" + larger.Id + ")")],
            NoteExportFormat.Html, all: false, images, "Missing image");
        var largeHtml = File.ReadAllText(largePath);
        var prefix = largeHtml.IndexOf(";base64,", StringComparison.Ordinal);
        Require(largeExport.MissingImages == 0 && prefix >= 0, "large image export");
        var first = prefix + ";base64,".Length;
        var last = largeHtml.IndexOf('"', first);
        Require(last > first &&
                Convert.FromBase64String(largeHtml[first..last]).SequenceEqual(expectedBytes),
            "streamed Base64 image bytes changed");

        var htmlPath = Path.Combine(temp.Path, "note.html");
        File.WriteAllText(htmlPath + ".tmp", "existing user file");
        NoteHtmlExport.Save(htmlPath, html);
        Require(File.ReadAllText(htmlPath) == html &&
                File.ReadAllText(htmlPath + ".tmp") == "existing user file",
            "HTML export changed an unrelated temporary file");

        var source = new NoteExportDocument("test-note", "Note", markdown);
        var mdPath = Path.Combine(temp.Path, "Note.md");
        var mdResult = NotePortableExport.Export(mdPath, [source],
            NoteExportFormat.Markdown, all: false, images, "Missing image");
        var exportedMd = File.ReadAllText(mdPath);
        var imageDir = Path.Combine(temp.Path, "Note-images");
        Require(mdResult.Exported == 1 && mdResult.MissingImages == 0 &&
                exportedMd.Contains("./Note-images/" + asset.Id + ".png") &&
                exportedMd.Contains("{width=75px}") &&
                File.Exists(Path.Combine(imageDir, asset.Id + ".png")),
            "Markdown export and relative image paths");
        var firstImageBytes = File.ReadAllBytes(Path.Combine(imageDir, asset.Id + ".png"));
        Require(Directory.GetFiles(imageDir).Length == 1,
            "repeated Markdown image references wrote duplicate files");
        var htmlStreamPath = Path.Combine(temp.Path, "streamed.html");
        var singleHtml = NotePortableExport.Export(htmlStreamPath, [source],
            NoteExportFormat.Html, all: false, images, "Missing image");
        var streamedHtml = File.ReadAllText(htmlStreamPath);
        Require(singleHtml.Exported == 1 &&
                streamedHtml == NoteHtmlExport.Render("test-note", "Note", markdown,
                    images, "Missing image", new HashSet<string>()),
            "streamed single HTML does not match Markdig output");
        var repeat = NotePortableExport.Export(mdPath, [source],
            NoteExportFormat.Markdown, all: false, images, "Missing image");
        Require(repeat.Exported == 1 &&
                File.Exists(Path.Combine(temp.Path, "Note-images (2)", asset.Id + ".png")) &&
                File.ReadAllBytes(Path.Combine(temp.Path, "Note-images (2)", asset.Id + ".png"))
                    .SequenceEqual(firstImageBytes), "repeated Markdown export overwrote images");

        var foreign = images.ImportBitmapSource("other-note", bitmap);
        var badMarkdown = "- Before\n\n  ![missing](i:99999999)\n\n![foreign](i:" +
            foreign.Id + ")\n\nAfter";
        var badNote = new NoteExportDocument("test-note", "Note", badMarkdown);
        var htmlWithMissing = NoteHtmlExport.Render("test-note", "Note", badMarkdown,
            images, "Missing image", missing);
        Require(missing.SetEquals(["99999999", foreign.Id]) &&
                htmlWithMissing.Contains("[Missing image: 99999999]") &&
                htmlWithMissing.Contains("[Missing image: " + foreign.Id + "]") &&
                htmlWithMissing.Contains("After"), "missing images are visible in HTML");

        var zipMd = Path.Combine(temp.Path, "markdown.zip");
        var batch = NotePortableExport.Export(zipMd,
            [source, new NoteExportDocument("test-note", "Note", badMarkdown)],
            NoteExportFormat.Markdown, all: true, images, "Missing image");
        Require(batch.Exported == 2 && batch.MissingImages == 2 &&
                batch.NotesWithMissingImages == 1, "Markdown zip missing-image summary");
        using (var zip = ZipFile.OpenRead(zipMd))
        {
            Require(zip.GetEntry("Note.md") != null && zip.GetEntry("Note (2).md") != null &&
                    zip.GetEntry("Note-images/" + asset.Id + ".png") != null &&
                    Read(zip.GetEntry("Note (2).md")!).Contains("\n  [Missing image: 99999999]") &&
                    !Read(zip.GetEntry("Note (2).md")!).Contains("(i:99999999)"),
                "Markdown archive contains safe note paths and placeholders");
        }

        foreach (var cancelAfter in new[] { 1, 2 })
        {
            using var cancellation = new CancellationTokenSource();
            var cancelledZip = Path.Combine(temp.Path, "cancelled.zip");
            File.WriteAllText(cancelledZip, "previous export");
            var progress = new CallbackProgress(n => { if (n == cancelAfter) cancellation.Cancel(); });
            try
            {
                NotePortableExport.Export(cancelledZip, [source, badNote], NoteExportFormat.Markdown,
                    all: true, images, "Missing image", progress, cancellation.Token);
                throw new Exception("cancelled ZIP export reported success");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Require(File.ReadAllText(cancelledZip) == "previous export" &&
                    !Directory.EnumerateFiles(temp.Path, ".cancelled.zip.*.tmp").Any(),
                "cancelled ZIP replaced the destination or left a temporary file");
        }

        var spacedZip = Path.Combine(temp.Path, "spaced.zip");
        NotePortableExport.Export(spacedZip,
            [new NoteExportDocument("test-note", "My Note", "![image](i:" + asset.Id + ")")],
            NoteExportFormat.Markdown, all: true, images, "Missing image");
        using (var zip = ZipFile.OpenRead(spacedZip))
        {
            Require(zip.GetEntry("My Note-images/" + asset.Id + ".png") != null &&
                    Read(zip.GetEntry("My Note.md")!).Contains(
                        "./My%20Note-images/" + asset.Id + ".png"),
                "Markdown ZIP image links with spaces");
        }

        var zipHtml = Path.Combine(temp.Path, "html.zip");
        var htmlBatch = NotePortableExport.Export(zipHtml, [source, badNote],
            NoteExportFormat.Html, all: true, images, "Missing image");
        using (var zip = ZipFile.OpenRead(zipHtml))
        {
            Require(htmlBatch.Exported == 2 && htmlBatch.MissingImages == 2 &&
                    Read(zip.GetEntry("Note.html")!) == streamedHtml &&
                    Read(zip.GetEntry("Note (2).html")!).Contains("Missing image:"),
                "HTML archive and missing-image placeholders");
        }

        // Todo papers use the same portable Markdown/HTML writer as notes.
        // Ordering is by the displayed Order field, not by backing-list position.
        var todoPaper = new PaperData
        {
            Id = "todo-export",
            Type = PaperTypes.Todo,
            Items =
            [
                new PaperItem { Id = "placeholder", Order = 0 },
                new PaperItem { Id = "done", Order = 20, Text = "finished **verbatim**", Done = true },
                new PaperItem { Id = "first", Order = 10, Text = "first\r\n# continuation" },
                new PaperItem { Id = "empty-done", Order = 30, Done = true }
            ]
        };
        // Capture a detached snapshot before the user changes the live Todo.
        var capturedItems = TodoRules.CloneAll(todoPaper.Items);
        todoPaper.Items[1].Text = "changed after snapshot";
        var todoDocument = Task.Run(() =>
            NotePortableExport.FromTodoItems(todoPaper.Id, "Note", capturedItems))
            .GetAwaiter().GetResult();
        var expectedTodo = "- [ ] first" + Environment.NewLine +
            "    \\# continuation" + Environment.NewLine +
            "- [x] finished \\*\\*verbatim\\*\\*" + Environment.NewLine +
            "- [x] ";
        Require(todoDocument.Content == expectedTodo,
            "Todo export must preserve order, status, multiline text, literal syntax and the captured text");

        var todoHtml = Path.Combine(temp.Path, "todo.html");
        var todoHtmlResult = NotePortableExport.Export(todoHtml, [todoDocument],
            NoteExportFormat.Html, all: false, images, "Missing image");
        var exportedTodoHtml = File.ReadAllText(todoHtml);
        Require(todoHtmlResult.Exported == 1 &&
                exportedTodoHtml.Contains("type=\"checkbox\"") &&
                exportedTodoHtml.Contains("finished **verbatim**") &&
                exportedTodoHtml.Contains("# continuation") &&
                !exportedTodoHtml.Contains("<strong>verbatim</strong>"),
            "single Todo HTML export must show checkboxes without interpreting literal Todo text as Markdown");
    }

    private sealed class CallbackProgress(Action<int> callback) : IProgress<int>
    {
        public void Report(int completed) => callback(completed);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }
}
