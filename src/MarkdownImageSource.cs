using System.Windows.Media.Imaging;

namespace PaperTodo;

// The editor owns rendering and insertion; each host owns where image bytes live.
// This adapter never changes the normal paper image store's lifetime or write protocol.
internal interface IMarkdownImageSource
{
    bool UsesFilePaths { get; }
    bool TryParseReference(string line, out MarkdownImageReference reference);
    string CreateReference(string imageId);
    bool TryGetAsset(string imageId, out NoteImageAsset asset);
    bool IsImageCorrupted(string imageId);
    BitmapSource? GetBitmapSource(string imageId, double targetPixelWidth = 0,
        bool allowDecodeUpgrade = true, bool protectInViewport = false);
    BitmapSource? GetBitmapSourceForClipboard(string imageId);
    bool TryGetEncodedImageBytes(string imageId, out NoteImageAsset asset, out byte[] bytes);
    void SetViewportProtectedBitmapIds(string ownerKey, IReadOnlyCollection<string> imageIds);
    IReadOnlyList<NoteImageAsset> ImportImageFiles(string noteId, IEnumerable<string> paths);
    NoteImageAsset ImportBitmapSource(string noteId, BitmapSource source, bool repairMissingAlpha);
    string CloneForeignImageReferencesForNote(string noteId, string markdown);
}

internal sealed class NoteMarkdownImageSource(NoteImageStore store) : IMarkdownImageSource
{
    public bool UsesFilePaths => false;
    public bool TryParseReference(string line, out MarkdownImageReference reference) =>
        MarkdownImageReferences.TryParseReferenceLine(line, out reference);
    public string CreateReference(string imageId) => MarkdownImageReferences.CreateReference(imageId);
    public bool TryGetAsset(string id, out NoteImageAsset asset) => store.TryGetAsset(id, out asset);
    public bool IsImageCorrupted(string id) => store.IsImageCorrupted(id);
    public BitmapSource? GetBitmapSource(string id, double targetPixelWidth = 0,
        bool allowDecodeUpgrade = true, bool protectInViewport = false) =>
        store.GetBitmapSource(id, targetPixelWidth, allowDecodeUpgrade, protectInViewport);
    public BitmapSource? GetBitmapSourceForClipboard(string id) => store.GetBitmapSourceForClipboard(id);
    public bool TryGetEncodedImageBytes(string id, out NoteImageAsset asset, out byte[] bytes) =>
        store.TryGetEncodedImageBytes(id, out asset, out bytes);
    public void SetViewportProtectedBitmapIds(string ownerKey, IReadOnlyCollection<string> ids) =>
        store.SetViewportProtectedBitmapIds(ownerKey, ids);
    public IReadOnlyList<NoteImageAsset> ImportImageFiles(string noteId, IEnumerable<string> paths) =>
        store.ImportImageFiles(noteId, paths);
    public NoteImageAsset ImportBitmapSource(string noteId, BitmapSource source, bool repairMissingAlpha) =>
        store.ImportBitmapSource(noteId, source, repairMissingAlpha);
    public string CloneForeignImageReferencesForNote(string noteId, string markdown) =>
        store.CloneForeignImageReferencesForNote(noteId, markdown);
}
