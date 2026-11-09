using Markdig.Extensions.TaskLists;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private static void CollectInlines(
        ContainerInline container,
        string source,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        // Raw HTML is still a deliberately small PaperTodo compatibility surface, but Markdig owns
        // token boundaries. Pair the HtmlInline tokens before applying the ordinary Markdown spans.
        CollectHtmlSemantics(container, source, spans, links);

        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case EmphasisInline emphasis:
                    var kind = EmphasisKind(emphasis);
                    if (kind.HasValue)
                    {
                        AddInlineSpan(
                            spans,
                            kind.Value,
                            emphasis,
                            source.Length,
                            Math.Max(1, emphasis.DelimiterCount));
                    }
                    break;

                case CodeInline code:
                    AddInlineSpan(
                        spans,
                        MarkdownSemanticSpanKind.InlineCode,
                        code,
                        source.Length,
                        Math.Max(1, code.DelimiterCount));
                    break;

                case TaskList task:
                    AddInlineSpan(
                        spans,
                        MarkdownSemanticSpanKind.TaskListMarker,
                        task,
                        source.Length,
                        markerLength: 0,
                        isChecked: task.Checked);
                    break;

                case LinkInline image when image.IsImage:
                    AddInlineSpan(
                        spans,
                        MarkdownSemanticSpanKind.Image,
                        image,
                        source.Length,
                        markerLength: 0);
                    break;

                case LinkInline link:
                    AddLink(links, link, source);
                    break;

                case AutolinkInline autoLink:
                    AddAutolink(links, autoLink, source);
                    break;
            }

            if (inline is ContainerInline nested)
            {
                CollectInlines(nested, source, spans, links);
            }
        }
    }

    private static MarkdownSemanticSpanKind? EmphasisKind(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
        {
            return MarkdownSemanticSpanKind.Strikethrough;
        }

        if (emphasis.DelimiterChar is not ('*' or '_'))
        {
            return null;
        }

        return emphasis.DelimiterCount >= 2
            ? MarkdownSemanticSpanKind.Strong
            : MarkdownSemanticSpanKind.Emphasis;
    }

    private static void AddInlineSpan(
        List<MarkdownSemanticSpan> spans,
        MarkdownSemanticSpanKind kind,
        Inline inline,
        int sourceLength,
        int markerLength,
        bool isChecked = false)
    {
        var sourceSpan = inline.Span;
        if (sourceSpan.IsEmpty || sourceSpan.Start < 0 || sourceSpan.End < sourceSpan.Start)
        {
            return;
        }

        var start = Math.Clamp(sourceSpan.Start, 0, sourceLength);
        var end = Math.Clamp(sourceSpan.End + 1, start, sourceLength);
        if (end <= start)
        {
            return;
        }

        spans.Add(new MarkdownSemanticSpan(
            kind,
            start,
            end - start,
            MarkerLength: markerLength,
            Checked: isChecked));
    }

    private static void CollectEscapeMarkers(
        string source,
        List<MarkdownSemanticSpan> spans)
    {
        if (string.IsNullOrEmpty(source) || source.IndexOf('\\') < 0)
        {
            return;
        }

        var protectedRanges = BuildProtectedEscapeRanges(spans);
        var protectedRangeIndex = 0;
        var slashRun = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != '\\')
            {
                slashRun = 0;
                continue;
            }

            while (protectedRangeIndex < protectedRanges.Length &&
                   protectedRanges[protectedRangeIndex].End <= index)
            {
                protectedRangeIndex++;
            }

            var isProtected = protectedRangeIndex < protectedRanges.Length &&
                protectedRanges[protectedRangeIndex].Start <= index;
            var escapedSlash = (slashRun & 1) != 0;
            if (!escapedSlash &&
                !isProtected &&
                index + 1 < source.Length &&
                IsSemanticEscapable(source[index + 1]))
            {
                spans.Add(new MarkdownSemanticSpan(
                    MarkdownSemanticSpanKind.EscapeMarker,
                    index,
                    1));
            }

            slashRun++;
        }
    }

    private static SourceInterval[] BuildProtectedEscapeRanges(
        IReadOnlyList<MarkdownSemanticSpan> spans)
    {
        var ranges = new List<SourceInterval>();
        foreach (var span in spans)
        {
            if (span.Kind is not (
                    MarkdownSemanticSpanKind.Code or
                    MarkdownSemanticSpanKind.FencedCode or
                    MarkdownSemanticSpanKind.InlineCode or
                    MarkdownSemanticSpanKind.HtmlContainer) ||
                span.End <= span.Start)
            {
                continue;
            }

            ranges.Add(new SourceInterval(span.Start, span.End));
        }

        return SortAndMergeIntervals(ranges);
    }

    private static bool IsSemanticEscapable(char value) =>
        value is >= '!' and <= '/' or
        >= ':' and <= '@' or
        >= '[' and <= '`' or
        >= '{' and <= '~';
}
