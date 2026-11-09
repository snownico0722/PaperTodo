using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private static readonly string[] BareHttpPrefixes = ["https://", "http://"];

    private readonly record struct SourceInterval(int Start, int End);

    private static void AddLink(
        List<MarkdownSemanticLink> links,
        LinkInline link,
        string source)
    {
        if (!TryNormalizeSourceSpan(link.Span, source.Length, out var start, out var end))
        {
            return;
        }

        int labelStart;
        int labelEnd;
        int destinationStart;
        int destinationLength;

        if (link.IsAutoLink)
        {
            // Markdig's AutoLinks extension also recognizes ftp:, mailto:, tel: and www. PaperTodo's
            // existing bare-link contract is intentionally narrower: only literal http/https URLs.
            // CommonMark angle autolinks arrive through AutolinkInline and are handled separately.
            var token = source.AsSpan(start, end - start);
            if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // AutoLinks LinkInline does not populate LabelSpan like [label](destination).
            // The entire source token is both the visible label and its destination source.
            labelStart = start;
            labelEnd = end;
            destinationStart = start;
            destinationLength = end - start;
        }
        else
        {
            labelStart = NormalizeOffset(link.LabelSpan.Start, source.Length, start);
            labelEnd = NormalizeInclusiveSpanEnd(link.LabelSpan.End, source.Length, labelStart);
            if (link.LabelSpan.IsEmpty || labelEnd <= labelStart)
            {
                labelStart = start;
                labelEnd = end;
            }

            destinationStart = -1;
            destinationLength = 0;
            if (!link.UrlSpan.IsEmpty &&
                link.UrlSpan.Start >= 0 &&
                link.UrlSpan.End >= link.UrlSpan.Start)
            {
                destinationStart = Math.Clamp(link.UrlSpan.Start, 0, source.Length);
                var destinationEnd = Math.Clamp(
                    link.UrlSpan.End + 1,
                    destinationStart,
                    source.Length);
                destinationLength = destinationEnd - destinationStart;
            }
        }

        links.Add(new MarkdownSemanticLink(
            start,
            end - start,
            labelStart,
            Math.Max(0, labelEnd - labelStart),
            destinationStart,
            destinationLength,
            link.Url ?? string.Empty,
            link.IsAutoLink));
    }

    private static void AddAutolink(
        List<MarkdownSemanticLink> links,
        AutolinkInline link,
        string source)
    {
        if (!TryNormalizeSourceSpan(link.Span, source.Length, out var start, out var end))
        {
            return;
        }

        var labelStart = start;
        var labelEnd = end;
        if (labelEnd - labelStart >= 2 &&
            source[labelStart] == '<' &&
            source[labelEnd - 1] == '>')
        {
            labelStart++;
            labelEnd--;
        }

        links.Add(new MarkdownSemanticLink(
            start,
            end - start,
            labelStart,
            Math.Max(0, labelEnd - labelStart),
            -1,
            0,
            link.IsEmail ? "mailto:" + link.Url : link.Url,
            true));
    }

    private static void CollectBareHttpLinks(
        string source,
        IReadOnlyList<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        // Bare URL candidates are already discovered in source order for each protocol. Build the
        // expensive exclusion domains once, merge them into sorted non-overlapping intervals, then
        // answer each overlap query by binary search instead of rescanning every semantic span/link.
        // Prefix order intentionally stays https -> http to preserve the existing overlap winner.
        var occupiedLinks = BuildLinkIntervals(links);
        var blockedSpans = BuildBareUrlBlockedIntervals(spans);

        foreach (var prefix in BareHttpPrefixes)
        {
            var acceptedForPrefix = new List<SourceInterval>();
            var searchFrom = 0;
            while (searchFrom < source.Length)
            {
                var start = source.IndexOf(
                    prefix,
                    searchFrom,
                    StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    break;
                }

                var end = start + prefix.Length;
                while (end < source.Length && !char.IsWhiteSpace(source[end]))
                {
                    end++;
                }
                end = TrimBareHttpEnd(source, start, end);

                if (end > start + prefix.Length &&
                    !OverlapsIntervals(occupiedLinks, start, end) &&
                    !OverlapsIntervals(blockedSpans, start, end))
                {
                    var candidate = source[start..end];
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    {
                        links.Add(new MarkdownSemanticLink(
                            start,
                            end - start,
                            start,
                            end - start,
                            start,
                            end - start,
                            candidate,
                            true));
                        acceptedForPrefix.Add(new SourceInterval(start, end));
                    }
                }

                searchFrom = Math.Max(start + prefix.Length, end);
            }

            if (acceptedForPrefix.Count > 0)
            {
                occupiedLinks = MergeSortedIntervals(occupiedLinks, acceptedForPrefix);
            }
        }
    }

    private static SourceInterval[] BuildLinkIntervals(
        IReadOnlyList<MarkdownSemanticLink> links)
    {
        if (links.Count == 0)
        {
            return Array.Empty<SourceInterval>();
        }

        var intervals = new List<SourceInterval>(links.Count);
        foreach (var link in links)
        {
            if (link.End > link.Start)
            {
                intervals.Add(new SourceInterval(link.Start, link.End));
            }
        }

        return SortAndMergeIntervals(intervals);
    }

    private static SourceInterval[] BuildBareUrlBlockedIntervals(
        IReadOnlyList<MarkdownSemanticSpan> spans)
    {
        var intervals = new List<SourceInterval>();
        foreach (var span in spans)
        {
            if (span.Kind is not (
                    MarkdownSemanticSpanKind.Code or
                    MarkdownSemanticSpanKind.FencedCode or
                    MarkdownSemanticSpanKind.InlineCode or
                    MarkdownSemanticSpanKind.Image or
                    MarkdownSemanticSpanKind.HtmlCode) ||
                span.End <= span.Start)
            {
                continue;
            }

            intervals.Add(new SourceInterval(span.Start, span.End));
        }

        return SortAndMergeIntervals(intervals);
    }

    private static SourceInterval[] SortAndMergeIntervals(List<SourceInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return Array.Empty<SourceInterval>();
        }

        intervals.Sort(static (left, right) =>
        {
            var start = left.Start.CompareTo(right.Start);
            return start != 0 ? start : left.End.CompareTo(right.End);
        });

        var merged = new List<SourceInterval>(intervals.Count);
        var current = intervals[0];
        for (var index = 1; index < intervals.Count; index++)
        {
            var next = intervals[index];
            if (next.Start <= current.End)
            {
                current = new SourceInterval(current.Start, Math.Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged.ToArray();
    }

    private static SourceInterval[] MergeSortedIntervals(
        IReadOnlyList<SourceInterval> left,
        IReadOnlyList<SourceInterval> right)
    {
        if (left.Count == 0)
        {
            return right.ToArray();
        }
        if (right.Count == 0)
        {
            return left.ToArray();
        }

        var mergedInput = new List<SourceInterval>(left.Count + right.Count);
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count || rightIndex < right.Count)
        {
            if (rightIndex >= right.Count ||
                (leftIndex < left.Count && left[leftIndex].Start <= right[rightIndex].Start))
            {
                mergedInput.Add(left[leftIndex++]);
            }
            else
            {
                mergedInput.Add(right[rightIndex++]);
            }
        }

        var merged = new List<SourceInterval>(mergedInput.Count);
        var current = mergedInput[0];
        for (var index = 1; index < mergedInput.Count; index++)
        {
            var next = mergedInput[index];
            if (next.Start <= current.End)
            {
                current = new SourceInterval(current.Start, Math.Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged.ToArray();
    }

    private static bool OverlapsIntervals(
        IReadOnlyList<SourceInterval> intervals,
        int start,
        int end)
    {
        var low = 0;
        var high = intervals.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (intervals[middle].End <= start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < intervals.Count && intervals[low].Start < end;
    }

    private static int TrimBareHttpEnd(string text, int start, int end)
    {
        var changed = true;
        while (changed && end > start)
        {
            changed = false;
            while (end > start)
            {
                var last = text[end - 1];
                if (last is '.' or ',' or '!' or ';' or ':' or '"' or '\'' or
                    '，' or '。' or '！' or '？' or '；' or '：' or '、' or
                    '”' or '’' or '》' or '）' or '】' or '>' ||
                    (last == ')' && HasUnmatchedClosingDelimiter(text, start, end, '(', ')')) ||
                    (last == ']' && HasUnmatchedClosingDelimiter(text, start, end, '[', ']')) ||
                    (last == '}' && HasUnmatchedClosingDelimiter(text, start, end, '{', '}')))
                {
                    end--;
                    changed = true;
                    continue;
                }

                break;
            }

            var delimiterLength = PairedMarkdownDelimiterLength(text, start, end);
            if (delimiterLength > 0)
            {
                end -= delimiterLength;
                changed = true;
            }
        }

        return end;
    }

    private static int PairedMarkdownDelimiterLength(string text, int start, int end)
    {
        if (start <= 0 || end <= start)
        {
            return 0;
        }

        var marker = text[start - 1];
        if (marker is not ('*' or '_' or '~'))
        {
            return 0;
        }

        var openingStart = start - 1;
        while (openingStart > 0 && text[openingStart - 1] == marker)
        {
            openingStart--;
        }

        if (openingStart > 0 &&
            !char.IsWhiteSpace(text[openingStart - 1]) &&
            !char.IsPunctuation(text[openingStart - 1]))
        {
            return 0;
        }

        var closingStart = end;
        while (closingStart > start && text[closingStart - 1] == marker)
        {
            closingStart--;
        }

        var openingLength = start - openingStart;
        var closingLength = end - closingStart;
        var pairedLength = Math.Min(openingLength, closingLength);
        if (pairedLength == 0 || (marker == '~' && pairedLength < 2))
        {
            return 0;
        }

        if (end < text.Length &&
            !char.IsWhiteSpace(text[end]) &&
            !char.IsPunctuation(text[end]))
        {
            return 0;
        }

        return pairedLength;
    }

    private static bool HasUnmatchedClosingDelimiter(
        string text,
        int start,
        int end,
        char open,
        char close)
    {
        var balance = 0;
        for (var index = start; index < end; index++)
        {
            if (text[index] == open)
            {
                balance++;
            }
            else if (text[index] == close)
            {
                balance--;
            }
        }
        return balance < 0;
    }

    private static bool TryNormalizeSourceSpan(
        SourceSpan span,
        int sourceLength,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        if (span.IsEmpty || span.Start < 0 || span.End < span.Start)
        {
            return false;
        }

        start = Math.Clamp(span.Start, 0, sourceLength);
        end = Math.Clamp(span.End + 1, start, sourceLength);
        return end > start;
    }

    private static int NormalizeOffset(int offset, int sourceLength, int fallback) =>
        offset < 0 ? fallback : Math.Clamp(offset, 0, sourceLength);

    private static int NormalizeInclusiveSpanEnd(int end, int sourceLength, int start) =>
        end < start ? start : Math.Clamp(end + 1, start, sourceLength);
}

/// <summary>
/// 链接隐藏区间与转义反斜杠的唯一归属判断。位于 label 外侧链接语法格中的反斜杠由整条链接
/// 负责显隐；label 内的反斜杠仍作为独立控制符处理。
/// </summary>
internal static class MarkdownLinkEscapeOwnership
{
    internal static bool TryGetOwningLink(
        MarkdownSemanticSpan escape,
        MarkdownSemanticSnapshot snapshot,
        string source,
        out MarkdownSemanticLink link)
    {
        link = default;
        if (escape.Kind != MarkdownSemanticSpanKind.EscapeMarker ||
            escape.Length <= 0 ||
            !snapshot.TryGetLinkAtOffset(escape.Start, out var candidate) ||
            !candidate.HasVisibleSyntax)
        {
            return false;
        }

        var openingOwned =
            IsSingleLineCell(snapshot.LineStarts, source.Length, candidate.Start, candidate.LabelStart) &&
            escape.Start >= candidate.Start &&
            escape.End <= candidate.LabelStart;
        var closingOwned =
            IsSingleLineCell(snapshot.LineStarts, source.Length, candidate.LabelEnd, candidate.End) &&
            escape.Start >= candidate.LabelEnd &&
            escape.End <= candidate.End;
        if (!openingOwned && !closingOwned)
        {
            return false;
        }

        link = candidate;
        return true;
    }

    private static bool IsSingleLineCell(
        int[] lineStarts,
        int sourceLength,
        int start,
        int end)
    {
        if (end <= start)
        {
            return true;
        }
        if (lineStarts.Length == 0 || sourceLength <= 0)
        {
            return false;
        }

        return FindLine(lineStarts, start) ==
            FindLine(lineStarts, Math.Min(end - 1, sourceLength - 1));
    }

    private static int FindLine(int[] lineStarts, int offset)
    {
        var normalized = Math.Clamp(offset, 0, lineStarts[^1]);
        var index = Array.BinarySearch(lineStarts, normalized);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
