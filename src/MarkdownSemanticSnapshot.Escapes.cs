namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
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
                    MarkdownSemanticSpanKind.InlineMath or
                    MarkdownSemanticSpanKind.BlockMath or
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
