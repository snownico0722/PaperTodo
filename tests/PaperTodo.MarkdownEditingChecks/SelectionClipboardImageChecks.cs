using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class ClipboardImageChecks
{
    static partial void CheckSelectionCopy()
    {
        using var directory = new FixtureDirectory();
        using var store = new NoteImageStore(Path.Combine(directory.Path, "note-assets.lmdb"));
        store.Load();
        var asset = store.ImportBitmapSource("selection-copy-note", CreateBitmap(MixedPixels));
        var markdown = MarkdownImageReferences.CreateReference(asset.Id);
        var box = new MarkdownTextBox { Text = markdown };
        using var semantics = new MarkdownSemanticDocument(box.Document);
        box.SetSemanticDocument(semantics);
        box.ConfigureNoteImages("selection-copy-note", store);
        box.Select(0, markdown.Length);
        box.Copy();

        var data = Clipboard.GetDataObject()
            ?? throw new InvalidOperationException("Selection copy left the clipboard empty.");
        Require(data.GetData(DataFormats.UnicodeText) as string == markdown,
            "Selection copy must preserve the Markdown text.");
        var html = data.GetData(DataFormats.Html, autoConvert: false) as string;
        Require(html?.Contains("data:image/png;base64,", StringComparison.Ordinal) == true,
            "Selection copy must retain an HTML image.");
        var rtf = data.GetData(DataFormats.Rtf, autoConvert: false) as string;
        Require(rtf?.Contains(@"\pngblip", StringComparison.Ordinal) == true,
            "Selection copy must retain an RTF image.");
        AssertSystemClipboard();

        using var destination = new NoteImageStore(Path.Combine(directory.Path, "other.lmdb"));
        destination.Load();
        var otherImage = destination.ImportBitmapSource("other-note", CreateBitmap(MakeOpaque(MixedPixels)));
        var target = new MarkdownTextBox();
        target.ConfigureNoteImages("paste-note", destination);
        target.RaiseEvent(new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.UnicodeText));
        var pasted = MarkdownImageReferences.Enumerate(target.Text).Single();
        Require(pasted.ImageId != otherImage.Id, "the system clipboard lost source image data");
        AssertStoredPixels(destination, pasted.ImageId, MixedPixels);

        box.SetSemanticDocument(null);
        store.SetViewportProtectedBitmapIds("selection-copy-note", Array.Empty<string>());
        Console.WriteLine("PASS selected image copy preserves Markdown, HTML, RTF and cross-workspace PNG pixels");
    }

    private static void CheckSystemCutBudgets()
    {
        using var directory = new FixtureDirectory();
        using var source = new NoteImageStore(Path.Combine(directory.Path, "source.lmdb"));
        using var destination = new NoteImageStore(Path.Combine(directory.Path, "destination.lmdb"));
        source.Load();
        destination.Load();

        var pixels = new byte[1024 * 1024 * 4];
        new Random(483).NextBytes(pixels);
        var bitmap = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 4096);
        var original = source.ImportBitmapSource("source", bitmap);
        Require(source.TryGetEncodedImageBytes(original.Id, out _, out var bytes), "source image unreadable");
        var count = 24 * 1024 * 1024 / bytes.Length + 1;
        var box = new MarkdownTextBox();
        box.ConfigureNoteImages("source", source);
        var target = new MarkdownTextBox();
        target.ConfigureNoteImages("target", destination);
        var window = new Window { Content = box, Width = 400, Height = 200, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            var reference = MarkdownImageReferences.CreateReference(original.Id);
            string SelectRows(IEnumerable<string> imageLines)
            {
                var markdown = string.Join("\r\n", imageLines.Append("end"));
                box.Text = markdown;
                box.Select(0, markdown.Length);
                return markdown;
            }

            var repeated = SelectRows(Enumerable.Repeat(reference, count));
            box.Copy();
            Require(!Clipboard.GetDataObject()!.GetDataPresent(DataFormats.Html, autoConvert: false),
                "rich copy exceeded its per-occurrence HTML budget");
            box.Cut();
            Require(box.Text.Length == 0, "a repeated, transferable image blocked the cut");
            window.Content = target;
            window.UpdateLayout();
            target.Paste();
            var pasted = MarkdownImageReferences.Enumerate(target.Text).ToArray();
            Require(pasted.Length == count && pasted.All(image => image.ImageId == pasted[0].ImageId) &&
                destination.TryGetEncodedImageBytes(pasted[0].ImageId, out _, out var imported) &&
                imported.SequenceEqual(bytes), "cut/paste did not transfer the repeated image exactly once");

            window.Content = box;
            window.UpdateLayout();
            box.Undo();
            Require(box.Text == repeated, "one undo did not restore the cut source");
            var ids = new List<string> { reference };
            while (ids.Count < count)
                ids.Add(MarkdownImageReferences.CreateReference(source.ImportImageBytes("source", bytes, "test.png").Id));
            var oversized = SelectRows(ids);
            Clipboard.SetText("previous clipboard");
            box.Cut();
            Require(box.Text == oversized && Clipboard.GetText() == "previous clipboard",
                "an oversized cut changed the source or replaced the clipboard");
            Console.WriteLine("PASS repeated images transfer once and an oversized cut preserves source and clipboard");
        }
        finally { window.Close(); }
    }
}
