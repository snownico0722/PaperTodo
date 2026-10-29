namespace PaperTodo;

// Same metadata wire shape as PaperTodo v3.31. The original Markdown and data.json
// are copied verbatim rather than round-tripped through desktop AppState.
public sealed class NoteImageAsset
{
    public string Id { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Mime { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = "";
    public int ByteLength { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? OriginalName { get; set; }
}
