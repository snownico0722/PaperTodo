namespace PaperTodo;

/// <summary>
/// One math expression in the original Markdown coordinate space. The range includes both
/// delimiters; MarkerLength is the length of either delimiter (1 for $, 2 for $$ / \( / \[).
/// </summary>
internal readonly record struct MarkdownMathRange(
    int Start,
    int Length,
    int MarkerLength,
    bool IsDisplay)
{
    public int End => Start + Length;
    public int ContentStart => Start + MarkerLength;
    public int ContentEnd => End - MarkerLength;
}

internal readonly record struct MarkdownMathBlockedRange(int Start, int End);

/// <summary>
/// Bounded PaperTodo math-delimiter scanner. Markdig remains the owner of ordinary Markdown
/// grammar; this scanner only adds four explicit math delimiter pairs and receives Markdig-owned
/// exclusion ranges so code, images and raw HTML never become formulas by accident.
/// </summary>
internal static class MarkdownMathScanner
{
    internal const int MaximumFormulaContentLength = 16_000;

    private readonly record struct Delimiter(
        string Open,
        string Close,
        bool IsDisplay,
        bool IsDollar);

    private static readonly Delimiter InlineDollar = new("$", "$", false, true);
    private static readonly Delimiter DisplayDollar = new("$$", "$$", true, true);
    private static readonly Delimiter InlineParentheses = new("\\(", "\\)", false, false);
    private static readonly Delimiter DisplayBrackets = new("\\[", "\\]", true, false);

    public static MarkdownMathRange[] Scan(
        string? source,
        IReadOnlyList<MarkdownMathBlockedRange>? blockedRanges = null)
    {
        var text = source ?? string.Empty;
        if (text.Length < 3 ||
            (text.IndexOf('$') < 0 && text.IndexOf('\\') < 0))
        {
            return Array.Empty<MarkdownMathRange>();
        }

        var blocked = NormalizeBlockedRanges(blockedRanges, text.Length);
        var results = new List<MarkdownMathRange>();
        var blockedIndex = 0;
        var offset = 0;

        while (offset < text.Length)
        {
            AdvanceBlockedIndex(blocked, ref blockedIndex, offset);
            if (blockedIndex < blocked.Length &&
                blocked[blockedIndex].Start <= offset &&
                offset < blocked[blockedIndex].End)
            {
                offset = blocked[blockedIndex].End;
                continue;
            }

            if (!TryReadOpeningDelimiter(text, offset, out var delimiter))
            {
                offset++;
                continue;
            }

            var contentStart = offset + delimiter.Open.Length;
            if (TryFindClosingDelimiter(
                    text,
                    contentStart,
                    delimiter,
                    blocked,
                    blockedIndex,
                    out var closeStart))
            {
                var end = closeStart + delimiter.Close.Length;
                results.Add(new MarkdownMathRange(
                    offset,
                    end - offset,
                    delimiter.Open.Length,
                    delimiter.IsDisplay));
                offset = end;
                continue;
            }

            // Keep an unmatched opener as ordinary source. Advancing by one still allows a later
            // valid delimiter in the same punctuation run to be discovered.
            offset++;
        }

        return results.Count == 0 ? Array.Empty<MarkdownMathRange>() : results.ToArray();
    }

    private static bool TryReadOpeningDelimiter(
        string source,
        int offset,
        out Delimiter delimiter)
    {
        delimiter = default;
        if ((uint)offset >= (uint)source.Length || IsEscaped(source, offset))
        {
            return false;
        }

        if (source[offset] == '$')
        {
            // Do not start in the middle of a larger dollar run.
            if (offset > 0 && source[offset - 1] == '$')
            {
                return false;
            }

            var run = CountRun(source, offset, '$');
            if (run == 2)
            {
                delimiter = DisplayDollar;
                return offset + delimiter.Open.Length < source.Length;
            }

            if (run != 1 || offset + 1 >= source.Length)
            {
                return false;
            }

            var next = source[offset + 1];
            if (char.IsWhiteSpace(next) || next == '$')
            {
                return false;
            }

            delimiter = InlineDollar;
            return true;
        }

        if (source[offset] != '\\' || offset + 1 >= source.Length)
        {
            return false;
        }

        if (source[offset + 1] == '(')
        {
            delimiter = InlineParentheses;
            return offset + delimiter.Open.Length < source.Length;
        }

        if (source[offset + 1] == '[')
        {
            delimiter = DisplayBrackets;
            return offset + delimiter.Open.Length < source.Length;
        }

        return false;
    }

    private static bool TryFindClosingDelimiter(
        string source,
        int contentStart,
        Delimiter delimiter,
        MarkdownMathBlockedRange[] blocked,
        int blockedIndex,
        out int closeStart)
    {
        closeStart = -1;
        var offset = contentStart;
        var currentBlocked = blockedIndex;

        while (offset < source.Length)
        {
            if (offset - contentStart > MaximumFormulaContentLength)
            {
                return false;
            }

            AdvanceBlockedIndex(blocked, ref currentBlocked, offset);
            if (currentBlocked < blocked.Length && blocked[currentBlocked].Start <= offset)
            {
                // A formula cannot jump across a Markdown-owned protected domain. Any delimiter
                // after that range will be considered independently by the outer scan.
                return false;
            }

            var current = source[offset];
            if (!delimiter.IsDisplay && current is '\r' or '\n')
            {
                return false;
            }

            if (!MatchesAt(source, offset, delimiter.Close) || IsEscaped(source, offset))
            {
                offset++;
                continue;
            }

            if (delimiter.IsDollar)
            {
                var run = CountRun(source, offset, '$');
                if (run != delimiter.Close.Length)
                {
                    offset += run;
                    continue;
                }

                if (!delimiter.IsDisplay && !ValidInlineDollarClosing(source, contentStart, offset))
                {
                    offset++;
                    continue;
                }
            }

            if (!ContainsNonWhitespace(source, contentStart, offset))
            {
                return false;
            }

            closeStart = offset;
            return true;
        }

        return false;
    }

    private static bool ValidInlineDollarClosing(string source, int contentStart, int closeStart)
    {
        if (closeStart <= contentStart || char.IsWhiteSpace(source[closeStart - 1]))
        {
            return false;
        }

        if (closeStart + 1 >= source.Length)
        {
            return true;
        }

        var next = source[closeStart + 1];
        // Avoid pairing the two currency signs in "$100 and $200" while allowing ordinary Latin
        // or CJK prose immediately after a formula such as "$x$公式".
        return next != '$' && !char.IsDigit(next);
    }

    private static bool ContainsNonWhitespace(string source, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAt(string source, int offset, string value) =>
        offset >= 0 &&
        offset + value.Length <= source.Length &&
        source.AsSpan(offset, value.Length).SequenceEqual(value.AsSpan());

    private static int CountRun(string source, int offset, char value)
    {
        var end = offset;
        while (end < source.Length && source[end] == value)
        {
            end++;
        }

        return end - offset;
    }

    private static bool IsEscaped(string source, int offset)
    {
        var slashCount = 0;
        for (var index = offset - 1; index >= 0 && source[index] == '\\'; index--)
        {
            slashCount++;
        }

        return (slashCount & 1) != 0;
    }

    private static void AdvanceBlockedIndex(
        IReadOnlyList<MarkdownMathBlockedRange> blocked,
        ref int index,
        int offset)
    {
        while (index < blocked.Count && blocked[index].End <= offset)
        {
            index++;
        }
    }

    private static MarkdownMathBlockedRange[] NormalizeBlockedRanges(
        IReadOnlyList<MarkdownMathBlockedRange>? ranges,
        int sourceLength)
    {
        if (ranges == null || ranges.Count == 0 || sourceLength <= 0)
        {
            return Array.Empty<MarkdownMathBlockedRange>();
        }

        var ordered = ranges
            .Select(range => new MarkdownMathBlockedRange(
                Math.Clamp(range.Start, 0, sourceLength),
                Math.Clamp(range.End, 0, sourceLength)))
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Array.Empty<MarkdownMathBlockedRange>();
        }

        var merged = new List<MarkdownMathBlockedRange>(ordered.Length);
        var current = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.Start <= current.End)
            {
                current = new MarkdownMathBlockedRange(
                    current.Start,
                    Math.Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged.ToArray();
    }
}

internal static class MarkdownMathSource
{
    public static bool TryExtract(
        string source,
        MarkdownSemanticSpan span,
        out string formula,
        out bool display)
    {
        formula = string.Empty;
        display = span.Kind == MarkdownSemanticSpanKind.BlockMath;
        if (span.Kind is not (
                MarkdownSemanticSpanKind.InlineMath or
                MarkdownSemanticSpanKind.BlockMath) ||
            span.MarkerLength is < 1 or > 2 ||
            span.Start < 0 ||
            span.End > source.Length ||
            span.Length <= span.MarkerLength * 2)
        {
            return false;
        }

        var contentLength = span.Length - span.MarkerLength * 2;
        if (contentLength > MarkdownMathScanner.MaximumFormulaContentLength)
        {
            return false;
        }

        var open = source.AsSpan(span.Start, span.MarkerLength);
        var close = source.AsSpan(span.End - span.MarkerLength, span.MarkerLength);
        var delimitersMatch = span.Kind switch
        {
            MarkdownSemanticSpanKind.InlineMath when span.MarkerLength == 1 =>
                open.SequenceEqual("$".AsSpan()) && close.SequenceEqual("$".AsSpan()),
            MarkdownSemanticSpanKind.InlineMath =>
                open.SequenceEqual("\\(".AsSpan()) && close.SequenceEqual("\\)".AsSpan()),
            MarkdownSemanticSpanKind.BlockMath =>
                (open.SequenceEqual("$$".AsSpan()) && close.SequenceEqual("$$".AsSpan())) ||
                (open.SequenceEqual("\\[".AsSpan()) && close.SequenceEqual("\\]".AsSpan())),
            _ => false
        };
        if (!delimitersMatch)
        {
            return false;
        }

        formula = source
            .Substring(span.Start + span.MarkerLength, contentLength)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        return formula.Length > 0 && formula.IndexOf('\0') < 0;
    }
}

internal sealed partial class MarkdownSemanticSnapshot
{
    private static void CollectMathSemantics(
        string source,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        if (string.IsNullOrEmpty(source) ||
            (source.IndexOf('$') < 0 && source.IndexOf('\\') < 0))
        {
            return;
        }

        var blocked = new List<MarkdownMathBlockedRange>();
        foreach (var span in spans)
        {
            if (span.End <= span.Start ||
                span.Kind is not (
                    MarkdownSemanticSpanKind.Code or
                    MarkdownSemanticSpanKind.FencedCode or
                    MarkdownSemanticSpanKind.InlineCode or
                    MarkdownSemanticSpanKind.Image or
                    MarkdownSemanticSpanKind.HtmlContainer or
                    MarkdownSemanticSpanKind.HtmlCode))
            {
                continue;
            }

            blocked.Add(new MarkdownMathBlockedRange(span.Start, span.End));
        }

        var scanned = MarkdownMathScanner.Scan(source, blocked);
        if (scanned.Length == 0)
        {
            return;
        }

        var math = scanned
            .Where(range => !links.Any(link =>
                RangesOverlap(range.Start, range.End, link.Start, link.End) &&
                !(link.Start >= range.Start && link.End <= range.End)))
            .ToArray();
        if (math.Length == 0)
        {
            return;
        }

        // A URL or explicit Markdown link wholly inside TeX is formula source, not a clickable
        // Markdown link. A formula candidate that starts inside an outer link was rejected above.
        links.RemoveAll(link => math.Any(range =>
            link.Start >= range.Start && link.End <= range.End));

        // Markdig does not know that a recognized formula body is opaque TeX. Remove semantics
        // completely owned by the formula, including accidental emphasis/list/rule interpretations;
        // outer list, quote and heading containers start before the formula and remain intact.
        spans.RemoveAll(span => math.Any(range =>
            span.Start >= range.Start && span.End <= range.End));

        foreach (var range in math)
        {
            spans.Add(new MarkdownSemanticSpan(
                range.IsDisplay
                    ? MarkdownSemanticSpanKind.BlockMath
                    : MarkdownSemanticSpanKind.InlineMath,
                range.Start,
                range.Length,
                MarkerLength: range.MarkerLength));
        }
    }

    private static bool RangesOverlap(int firstStart, int firstEnd, int secondStart, int secondEnd) =>
        firstStart < secondEnd && secondStart < firstEnd;
}
