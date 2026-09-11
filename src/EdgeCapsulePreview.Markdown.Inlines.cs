using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

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

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal,
        string renderMode = MarkdownRenderModes.Full,
        PreviewInlineCache? cache = null)
    {
        var prepared = cache?.Get(text, renderMode) ?? PrepareInlineText(text, renderMode);
        Hyperlink? activeLink = null;
        Uri? activeUri = null;
        foreach (var piece in prepared.Pieces)
        {
            // One link label may contain several styles. Preserve one Hyperlink for that label,
            // but do not merge adjacent distinct links just because their URL values are equal.
            if (!ReferenceEquals(activeUri, piece.Link))
            {
                activeUri = piece.Link;
                activeLink = null;
                if (activeUri != null)
                {
                    activeLink = new Hyperlink { NavigateUri = activeUri, Cursor = Cursors.Hand };
                    activeLink.SetResourceReference(TextElement.ForegroundProperty, "LinkBrushKey");
                    EdgeCapsulePreviewInteraction.SetConsumesPointer(activeLink, true);
                    activeLink.RequestNavigate += (_, e) =>
                    {
                        openExternal(e.Uri.AbsoluteUri);
                        e.Handled = true;
                    };
                    target.Add(activeLink);
                }
            }

            bool Has(InlineStyle flag) => (piece.Style & flag) != 0;
            var run = new Run(piece.Text);
            if (Has(InlineStyle.Syntax)) run.Foreground = Theme.SyntaxFadeBrush;
            else if (Has(InlineStyle.Weak)) run.SetResourceReference(TextElement.ForegroundProperty, "WeakTextBrushKey");
            Inline inline = run;
            if (Has(InlineStyle.Code))
            {
                var code = new Span(inline)
                {
                    FontFamily = NoteTypography.CodeFontFamily,
                    FontSize = NoteTypography.CodeFontSize
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "HoverBrushKey");
                inline = code;
            }
            if (Has(InlineStyle.Strong))
            {
                var bold = new Bold(inline);
                ApplyStrongTypography(bold);
                inline = bold;
            }
            if (Has(InlineStyle.Italic)) inline = new Italic(inline);
            if (Has(InlineStyle.Strike)) inline = new Span(inline) { TextDecorations = TextDecorations.Strikethrough };
            if (Has(InlineStyle.Underline)) inline = new Span(inline) { TextDecorations = TextDecorations.Underline };
            (activeLink?.Inlines ?? target).Add(inline);
        }
    }

    [Flags]
    internal enum InlineStyle { None = 0, Strong = 1, Italic = 2, Strike = 4, Code = 8, Weak = 16, Syntax = 32, Underline = 64 }
    internal readonly record struct InlinePiece(string Text, InlineStyle Style, Uri? Link = null);

    // The note's shared semantic recognizer feeds measurement, TextBlock publication and
    // prepared TextFormatter paragraphs. Syntax stays in values until a renderer consumes it.
    internal static IEnumerable<InlinePiece> InlinePieces(
        string text, string mode, InlineStyle style = InlineStyle.None, Uri? link = null, int depth = 0)
    {
        foreach (var piece in SemanticInlinePieces(text, mode))
            yield return piece with { Style = piece.Style | style, Link = piece.Link ?? link };
    }
}
