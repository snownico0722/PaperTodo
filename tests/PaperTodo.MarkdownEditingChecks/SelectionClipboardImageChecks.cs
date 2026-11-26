using System.IO;
using System.Windows;
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
}
