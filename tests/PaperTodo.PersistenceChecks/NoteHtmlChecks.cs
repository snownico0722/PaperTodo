using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class NoteHtmlChecks
{
    internal static void Run()
    {
        using var temp = new TempDirectory();
        using var images = new NoteImageStore();
        typeof(NoteImageStore).GetField("<FilePath>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(images, Path.Combine(temp.Path, "note-assets.lmdb"));
        images.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] {0,0,255,255,0,255,0,255,255,0,0,255,0,0,0,255}, 8);
        bitmap.Freeze();
        var asset = images.ImportBitmapSource("test-note", bitmap);
        var html = NoteHtmlExport.Render("<title>", "# Heading\n\n**bold**\n\n![image](i:" + asset.Id + ")\n\n<script>alert(1)</script>", images);
        if (!html.Contains("data:image/") || !html.Contains("<strong>bold</strong>") ||
            html.Contains("<script>") || !html.Contains("&lt;title&gt;")) throw new Exception("portable rendering");
        var path = Path.Combine(temp.Path, "note.html");
        NoteHtmlExport.Save(path, html);
        if (File.ReadAllText(path) != html) throw new Exception("export contents");
        try { NoteHtmlExport.Render("missing", "![image](i:missing)", images); throw new Exception("missing image accepted"); }
        catch (InvalidDataException) { }
        if (File.ReadAllText(path) != html) throw new Exception("failed render changed existing export");
    }
}
