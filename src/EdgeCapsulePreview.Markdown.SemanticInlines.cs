using System.Text;

namespace PaperTodo;

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // Presentation adapter only. The note's Markdig-backed snapshot owns syntax recognition.
    // These per-compilation values contain no WPF objects, fonts or brushes.
    private static IEnumerable<InlinePiece> SemanticInlinePieces(string text, string mode)
    {
        if (text.Length == 0) yield break;
        if (mode == MarkdownRenderModes.Off)
        {
            yield return new InlinePiece(text, InlineStyle.None);
            yield break;
        }
        var snapshot = MarkdownSemanticSnapshot.Parse(text);
        var styles = new InlineStyle[text.Length];
        var hidden = new bool[text.Length];
        var links = new Uri?[text.Length];
        var images = new Dictionary<int, (int End, string Label)>();
        var full = mode == MarkdownRenderModes.Full;
        void Style(int start, int end, InlineStyle style)
        {
            for (var i = Math.Max(0, start); i < Math.Min(text.Length, end); i++) styles[i] |= style;
        }
        void Syntax(int start, int end)
        {
            for (var i = Math.Max(0, start); i < Math.Min(text.Length, end); i++)
            {
                if (full) hidden[i] = true;
                else if (mode == MarkdownRenderModes.Enhanced) styles[i] |= InlineStyle.Syntax;
            }
        }
        foreach (var link in snapshot.Links)
        {
            Syntax(link.Start, link.LabelStart);
            Syntax(link.LabelEnd, link.End);
            if (Uri.TryCreate(MarkdownInlineSyntax.Unescape(link.Url), UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" or "mailto")
            {
                // Uri identity keeps adjacent equal-URL links distinct, while styled fragments
                // of one label share the same interaction target.
                for (var i = Math.Max(0, link.LabelStart); i < Math.Min(text.Length, link.LabelEnd); i++) links[i] = uri;
            }
        }
        foreach (var span in snapshot.Spans)
        {
            var style = span.Kind switch
            {
                MarkdownSemanticSpanKind.Strong or MarkdownSemanticSpanKind.HtmlStrong => InlineStyle.Strong,
                MarkdownSemanticSpanKind.Emphasis or MarkdownSemanticSpanKind.HtmlEmphasis => InlineStyle.Italic,
                MarkdownSemanticSpanKind.Strikethrough or MarkdownSemanticSpanKind.HtmlStrikethrough => InlineStyle.Strike,
                MarkdownSemanticSpanKind.InlineCode or MarkdownSemanticSpanKind.HtmlCode => InlineStyle.Code,
                MarkdownSemanticSpanKind.HtmlUnderline => InlineStyle.Underline,
                _ => InlineStyle.None
            };
            if (span.Kind is MarkdownSemanticSpanKind.Strong or MarkdownSemanticSpanKind.Emphasis or
                MarkdownSemanticSpanKind.Strikethrough or MarkdownSemanticSpanKind.InlineCode)
            {
                var marker = Math.Clamp(span.MarkerLength, 1, Math.Max(1, span.Length / 2));
                var start = Math.Min(span.End, span.Start + marker);
                var end = Math.Max(start, span.End - marker);
                Syntax(span.Start, start);
                Syntax(end, span.End);
                Style(start, end, style);
            }
            else if (style != InlineStyle.None) Style(span.Start, span.End, style);
            else if (span.Kind == MarkdownSemanticSpanKind.HtmlMarker) Syntax(span.Start, span.End);
            else if (span.Kind == MarkdownSemanticSpanKind.EscapeMarker && full) Syntax(span.Start, span.End);
            else if (span.Kind == MarkdownSemanticSpanKind.Image && span.Start >= 0 && span.End <= text.Length &&
                MarkdownImageReferences.TrySplitMarkdownImage(text[span.Start..span.End], out var label, out _, out _))
            {
                var labelStart = span.Start + 2;
                var labelEnd = Math.Min(span.End, labelStart + label.Length);
                if (full)
                {
                    var visible = MarkdownInlineSyntax.Unescape(label);
                    images[span.Start] = (span.End, string.IsNullOrWhiteSpace(visible) ? "▧" : "▧ " + visible);
                }
                else
                {
                    Syntax(span.Start, labelStart);
                    Syntax(labelEnd, span.End);
                    Style(labelStart, labelEnd, InlineStyle.Weak);
                }
            }
        }
        // Coalesce output runs, including across removed syntax. TextFormatter's existing run
        // cache can then reuse the result without retaining semantic ASTs or layout state.
        var builder = new StringBuilder();
        var activeStyle = InlineStyle.None;
        Uri? activeLink = null;
        for (var index = 0; index < text.Length;)
        {
            var style = styles[index];
            var link = links[index];
            string? replacement = null;
            var end = index + 1;
            if (full && images.TryGetValue(index, out var image))
            {
                replacement = image.Label;
                end = image.End;
                style |= InlineStyle.Weak;
            }
            else if (hidden[index]) { index++; continue; }
            if (builder.Length > 0 && (style != activeStyle || !ReferenceEquals(link, activeLink)))
            {
                yield return new InlinePiece(builder.ToString(), activeStyle, activeLink);
                builder.Clear();
            }
            activeStyle = style;
            activeLink = link;
            if (replacement != null) builder.Append(replacement);
            else builder.Append(text[index]);
            index = end;
        }
        if (builder.Length > 0) yield return new InlinePiece(builder.ToString(), activeStyle, activeLink);
    }
}
