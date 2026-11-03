using System.IO;
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
        var markdown = "# Heading\n\n**bold** and ~~deleted~~\n\n- [x] done\n\n" +
            "![image|50%](i:" + asset.Id + ")\n\n" +
            "![image](i:" + asset.Id + "){width=75px}\n\n" +
            "[safe](https://example.com) [unsafe](javascript:alert(1))\n\n" +
            "<script>alert(1)</script>";
        var html = NoteHtmlExport.Render("test-note", "<title>", markdown, images);
        if (!html.Contains("data:image/") || !html.Contains("<strong>bold</strong>") ||
            !html.Contains("<del>deleted</del>") || !html.Contains("type=\"checkbox\"") ||
            !html.Contains("width:1px") || !html.Contains("width:75px") ||
            html.Contains("{width=75px}") || html.Contains("href=\"javascript:") ||
            html.Contains("<script>") || !html.Contains("&lt;title&gt;"))
            throw new Exception("portable markdown rendering");
        var path = Path.Combine(temp.Path, "note.html");
        File.WriteAllText(path + ".tmp", "existing user file");
        NoteHtmlExport.Save(path, html);
        if (File.ReadAllText(path) != html || File.ReadAllText(path + ".tmp") != "existing user file")
            throw new Exception("export contents or unrelated temporary file");
        NoteHtmlExport.Save(path, "<p>updated</p>");
        if (File.ReadAllText(path) != "<p>updated</p>")
            throw new Exception("export overwrite");
        try { NoteHtmlExport.Render("test-note", "missing", "![image](i:missing)", images); throw new Exception("missing image accepted"); }
        catch (InvalidDataException) { }
        var foreign = images.ImportBitmapSource("other-note", bitmap);
        try { NoteHtmlExport.Render("test-note", "foreign", "![image](i:" + foreign.Id + ")", images); throw new Exception("foreign image accepted"); }
        catch (InvalidDataException) { }
        if (File.ReadAllText(path) != "<p>updated</p>")
            throw new Exception("failed render changed existing export");
    }
}
