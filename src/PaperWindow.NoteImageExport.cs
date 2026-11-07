namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void ExportNoteAsImage()
    {
        if (!_isExpandedEditorMode || !IsCurrentBodyProviderMarkdown || _noteBox == null) return;
        // Capture the live editor, not the debounced PaperData. Export never changes its
        // selection, scroll position, source, undo history or persisted note content.
        var markdown = _noteBox.Text;
        var title = _controller.PaperTitleText(_paper);
        var noteId = _paper.Id;
        var images = _controller.ImageStore;
        var missing = Strings.Get("NoteExportMissingImageText");
        var preview = new NoteImageExportWindow(InteractionWindow, title, cancellation =>
        {
            var cache = new NoteImageExport.ImageSourceCache(images, noteId);
            return NoteImageExport.Compose(title, markdown, cache.Resolve, missing, cancellation);
        });
        preview.ShowDialog();
    }
}
