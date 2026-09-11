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
            (activeLink?.Inlines ?? target).Add(inline);
        }
    }

    [Flags]
    internal enum InlineStyle { None = 0, Strong = 1, Italic = 2, Strike = 4, Code = 8, Weak = 16, Syntax = 32 }
    internal readonly record struct InlinePiece(string Text, InlineStyle Style, Uri? Link = null);

    // The single bounded inline grammar shared by measurement, TextBlock publication and
    // prepared TextFormatter paragraphs. Syntax stays in values until a renderer consumes it.
    internal static IEnumerable<InlinePiece> InlinePieces(
        string text, string mode, InlineStyle style = InlineStyle.None, Uri? link = null, int depth = 0)
    {
        string Display(string value) => mode == MarkdownRenderModes.Full ? MarkdownInlineSyntax.Unescape(value) : value;
        if (mode == MarkdownRenderModes.Off || depth >= MaximumInlineDepth)
        {
            yield return new InlinePiece(Display(text), style, link);
            yield break;
        }
        var scan = MarkdownInlineSyntax.MaskEscapedPunctuation(text);
        var cursor = 0;
        foreach (System.Text.RegularExpressions.Match match in InlinePattern.Matches(scan))
        {
            if (match.Index > cursor)
                yield return new InlinePiece(Display(text[cursor..match.Index]), style, link);
            var index = Enumerable.Range(1, 12).First(i => match.Groups[i].Success);
            var group = match.Groups[index];
            var value = text.Substring(group.Index, group.Length);
            var syntaxStyle = mode == MarkdownRenderModes.Enhanced ? style | InlineStyle.Syntax : style;
            if (mode != MarkdownRenderModes.Full)
                yield return new InlinePiece(text[match.Index..group.Index], syntaxStyle, link);
            if (index == 1)
            {
                var label = Display(value);
                yield return new InlinePiece(mode == MarkdownRenderModes.Full
                    ? string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}" : label, style | InlineStyle.Weak, link);
            }
            else if (index == 10)
                yield return new InlinePiece(value, style | InlineStyle.Code, link);
            else
            {
                var nestedStyle = style | (index switch
                {
                    5 or 6 => InlineStyle.Strong | InlineStyle.Italic,
                    7 or 8 => InlineStyle.Strong,
                    9 => InlineStyle.Strike,
                    11 or 12 => InlineStyle.Italic,
                    _ => InlineStyle.None
                });
                var nestedLink = link;
                if (index == 3 && Uri.TryCreate(MarkdownInlineSyntax.Unescape(
                    text.Substring(match.Groups[4].Index, match.Groups[4].Length)), UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" or "mailto") nestedLink = uri;
                foreach (var part in InlinePieces(value, mode, nestedStyle, nestedLink, depth + 1)) yield return part;
            }
            cursor = match.Index + match.Length;
            if (mode != MarkdownRenderModes.Full)
                yield return new InlinePiece(text[(group.Index + group.Length)..cursor], syntaxStyle, link);
        }
        if (cursor < text.Length) yield return new InlinePiece(Display(text[cursor..]), style, link);
    }
}
