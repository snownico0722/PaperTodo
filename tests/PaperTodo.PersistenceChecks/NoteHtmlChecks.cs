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
        var repeat = NotePortableExport.Export(mdPath, [source],
            NoteExportFormat.Markdown, all: false, images, "Missing image");
        Require(repeat.Exported == 1 &&
                File.Exists(Path.Combine(temp.Path, "Note-images (2)", asset.Id + ".png")) &&
                File.ReadAllBytes(Path.Combine(temp.Path, "Note-images (2)", asset.Id + ".png"))
                    .SequenceEqual(firstImageBytes), "repeated Markdown export overwrote images");

        var foreign = images.ImportBitmapSource("other-note", bitmap);
        var badMarkdown = "Before\n\n![missing](i:99999999)\n\n![foreign](i:" +
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
                    Read(zip.GetEntry("Note (2).md")!).Contains("[Missing image: 99999999]") &&
                    !Read(zip.GetEntry("Note (2).md")!).Contains("(i:99999999)"),
                "Markdown archive contains safe note paths and placeholders");
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
                    Read(zip.GetEntry("Note.html")!).Contains("data:image/") &&
                    Read(zip.GetEntry("Note (2).html")!).Contains("Missing image:"),
                "HTML archive and missing-image placeholders");
        }
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
