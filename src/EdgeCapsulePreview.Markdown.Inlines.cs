namespace PaperTodo;

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // One cache per captured excerpt, not per editor or process. These values contain no WPF
    // objects, fonts or brushes: current theme/DPI/zoom are still resolved when publishing.
    internal sealed class PreviewInlineCache
    {
        private readonly Dictionary<(string Text, string Mode), PreparedInlineText> _entries = new();
        internal int Count => _entries.Count;

        internal PreparedInlineText Get(string text, string mode)
        {
            var key = (text, mode);
            if (!_entries.TryGetValue(key, out var prepared))
            {
                prepared = PrepareInlineText(text, mode);
                _entries.Add(key, prepared);
            }
            return prepared;
        }
    }

    internal sealed record PreparedInlineText(IReadOnlyList<InlinePiece> Pieces, string VisibleText);

    private static PreparedInlineText PrepareInlineText(string text, string mode)
    {
        var pieces = InlinePieces(text, mode).Where(piece => piece.Text.Length > 0).ToArray();
        return new PreparedInlineText(Array.AsReadOnly(pieces), string.Concat(pieces.Select(piece => piece.Text)));
    }

    [Flags]
    internal enum InlineStyle { None = 0, Strong = 1, Italic = 2, Strike = 4, Code = 8, Weak = 16, Syntax = 32, Underline = 64 }
    internal readonly record struct InlinePiece(string Text, InlineStyle Style, Uri? Link = null);

    internal static IEnumerable<InlinePiece> InlinePieces(string text, string mode) =>
        SemanticInlinePieces(text, mode);
}
