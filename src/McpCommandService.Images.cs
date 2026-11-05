using System.Text.Json;

namespace PaperTodo;

internal sealed partial class McpCommandService
{
    // The named pipe allows up to 8 million JSON characters. Cap Base64 at 4 MiB decoded
    // to stay within that envelope without introducing a file-path read capability.
    internal const int MaxBase64ImageBytes = 4 * 1024 * 1024;
    internal const int MaxBase64ImageCharacters = ((MaxBase64ImageBytes + 2) / 3) * 4;

    // Internal named-pipe operation used by --import-note-image; not a public MCP tool.
    private object InsertNoteImage(JsonElement parameters)
    {
        RequireAdditiveWrites();
        var paperId = RequiredString(parameters, "paper_id", 64);
        if (parameters.TryGetProperty("image_path", out _))
            throw new McpApiException("invalid_params", "image_path is no longer supported; supply image_base64.");
        var encoded = RequiredString(parameters, "image_base64", MaxBase64ImageCharacters);
        var imageName = OptionalString(parameters, "image_name", 200);

        byte[] imageBytes;
        try { imageBytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            throw new McpApiException("invalid_params", "image_base64 must contain valid Base64 image data.");
        }
        if (imageBytes.Length > MaxBase64ImageBytes)
            throw new McpApiException("invalid_params", "image_base64 cannot exceed 4 MiB decoded.");

        var result = _commands.InsertNoteImage(
            paperId, imageBytes, imageName, PaperOperationContext.Mcp());
        return new
        {
            paper_id = result.PaperId,
            image_id = result.ImageId,
            markdown_reference = result.Reference
        };
    }

    private object ReadNoteImage(JsonElement parameters)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var imageId = RequiredString(parameters, "image_id", 64);

        // A deleted image reference may leave its LMDB blob behind for undo/recovery.
        // MCP reads only images still present as renderable references in the *current*
        // note draft; do not change the broader plugin asset API's recovery semantics.
        var paper = _controller.State.Papers.FirstOrDefault(candidate =>
            candidate.Id == paperId &&
            candidate.Type == PaperTypes.Note &&
            candidate.BodyProviderId == PaperBodyProviderIds.Markdown);
        if (paper != null &&
            !MarkdownImageReferences.Enumerate(
                _controller.CurrentMarkdownContentForExternalRead(paper))
                .Any(reference => string.Equals(reference.ImageId, imageId, StringComparison.Ordinal)))
        {
            throw new McpApiException("asset_not_found", "The image is not referenced by this note.");
        }

        var image = _commands.ReadNoteImage(paperId, imageId);
        return new
        {
            paper_id = paperId,
            image_id = image.ImageId,
            mime = image.Mime,
            image_base64 = Convert.ToBase64String(image.Bytes)
        };
    }
}
