using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal PaperNoteImage ReadNoteImage(string paperId, string imageId)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(RequiredId(paperId, "paperId"), PaperTypes.Note);
        if (!_controller.CaptureNoteSnapshot(paper).ContentAvailable)
            throw Error("note_content_unavailable", "Only built-in Markdown assets are available.");
        var id = RequiredId(imageId, "imageId");
        if (!_controller.ImageStore.TryReadOwnedImage(paper.Id, id,
                IPaperNoteAssetsApi.MaximumImageBytes, out var mime, out var bytes, out var tooLarge))
            throw Error(tooLarge ? "asset_too_large" : "asset_not_found",
                tooLarge ? "The encoded image exceeds the plugin read limit." : "The note image is unavailable.");
        return new PaperNoteImage(id, mime, bytes);
    }
}
