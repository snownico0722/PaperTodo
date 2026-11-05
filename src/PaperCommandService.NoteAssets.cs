using System.IO;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal NoteImageInsertionResult InsertNoteImage(
        string paperId, byte[] imageBytes, string? imageName,
        PaperOperationContext context)
    {
        EnsureRunning();
        var paper = RequirePaper(RequiredId(paperId, "paperId"), PaperTypes.Note);
        if (!_controller.CaptureNoteSnapshot(paper).ContentAvailable)
            throw Error("note_content_unavailable", "Only built-in Markdown notes support image insertion.");
        ArgumentNullException.ThrowIfNull(imageBytes);

        // Check against the editor draft before settling pending user text or touching LMDB.
        // Image IDs are at most 8 digits, so this bounds the eventual appended reference.
        var maximumReference = MarkdownImageReferences.CreateReference("99999999");
        EnsureImageReferenceRenders(
            _controller.CurrentMarkdownContentForExternalRead(paper),
            maximumReference);
        PrepareExternalMutation(paper);
        var original = paper.Content ?? "";
        EnsureImageReferenceRenders(original, maximumReference);

        NoteImageAsset imported;
        try
        {
            imported = _controller.ImageStore.ImportImageBytes(
                paper.Id, imageBytes, imageName ?? "mcp-image");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or InvalidOperationException)
        {
            throw Error("image_import_failed", ex.Message);
        }

        var reference = MarkdownImageReferences.CreateReference(imported.Id);
        var newContent = BuildNoteWriteResult(original, reference, NoteWriteMode.Append);
        using (_controller.SuppressPaperPluginEventScans())
        {
            paper.Content = newContent;
            if (!_controller.TryCommitExternalMutation())
            {
                // A failed save may already have replaced data.json. Keep the image bytes:
                // deleting them here could break a durable Markdown reference. Existing
                // startup image GC reclaims blobs that are truly unreferenced.
                paper.Content = original;
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }

            _controller.RunExternalPostCommitUi(
                () => _controller.RefreshExternalNotePaper(paper));
        }

        _controller.PublishExternalPaperOperation(context);
        return new NoteImageInsertionResult(paper.Id, imported.Id, reference);
    }

    private static void EnsureImageReferenceRenders(string current, string reference)
    {
        // The append must form a real image block, not text inside an unclosed
        // Markdown fence. Validate the semantic result before importing an LMDB blob.
        var candidate = BuildNoteWriteResult(current, reference, NoteWriteMode.Append);
        var start = candidate.Length - reference.Length;
        if (!MarkdownImageReferences.Enumerate(candidate)
            .Any(image => image.LineStart == start &&
                          string.Equals(image.ImageId, "99999999", StringComparison.Ordinal)))
        {
            throw Error("image_reference_not_renderable",
                "The note ends in a Markdown context where the image would not render. Close the open code block before inserting an image.");
        }
    }

    internal PaperNoteImage ReadNoteImage(string paperId, string imageId)
    {
        EnsureRunning();
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

internal sealed record NoteImageInsertionResult(string PaperId, string ImageId, string Reference);
