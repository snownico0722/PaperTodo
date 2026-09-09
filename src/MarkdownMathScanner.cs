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
/// Bounded PaperTodo math-delimiter scanner. Markdig still owns ordinary Markdown grammar; this
/// scanner only adds the four explicit math delimiter pairs and is given Markdig-owned exclusion
/// ranges so code, links, images and raw HTML never become formulas by accident.
/// </summary>
internal static class MarkdownMathScanner
{
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

        var blocked = NormalizeBlockedRanges(blockedRanges);
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

            // An unmatched opener remains ordinary Markdown source. Move by one character rather
            // than the whole delimiter so a later valid opener in the same run can still be found.
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
            var run = CountRun(source, offset, '$');
            if (run == 2)
            {
                delimiter = DisplayDollar;
                return offset + 2 < source.Length;
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

        delimiter = source[offset + 1] switch
        {
            '(' => InlineParentheses,
            '[' => DisplayBrackets,
            _ => default
        };
        return delimiter.Open != null && offset + delimiter.Open.Length < source.Length;
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
            AdvanceBlockedIndex(blocked, ref currentBlocked, offset);
            if (currentBlocked < blocked.Length && blocked[currentBlocked].Start <= offset)
            {
                // Do not allow a formula to jump across a Markdown-owned protected range. A
                // delimiter after code/link/HTML starts a separate candidate instead.
                return false;
            }

            var current = source[offset];
            if (!delimiter.IsDisplay && (current == '\r' || current == '\n'))
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
        // This prevents common currency prose such as "$100 and $200" from pairing the two
        // currency signs, while still allowing CJK or Latin prose immediately after $x$.
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

    private static bool MatchesAt(string source, int offset, string value)
    {
        return offset >= 0 &&
            offset + value.Length <= source.Length &&
            source.AsSpan(offset, value.Length).SequenceEqual(value.AsSpan());
    }

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
        IReadOnlyList<MarkdownMathBlockedRange>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
        {
            return Array.Empty<MarkdownMathBlockedRange>();
        }

        var ordered = ranges
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

        foreach (var link in links)
        {
            if (link.End > link.Start)
            {
                blocked.Add(new MarkdownMathBlockedRange(link.Start, link.End));
            }
        }

        var math = MarkdownMathScanner.Scan(source, blocked);
        if (math.Length == 0)
        {
            return;
        }

        // Ordinary Markdown parsers do not know that the formula body is an opaque TeX domain.
        // Remove only inline presentation semantics that landed completely inside a recognized
        // formula; block/list/quote/heading containers remain valid around the formula.
        spans.RemoveAll(span =>
            IsMathOwnedInlineSemantic(span.Kind) &&
            math.Any(range => span.Start >= range.Start && span.End <= range.End));
        links.RemoveAll(link =>
            math.Any(range => link.Start < range.End && range.Start < link.End));

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

    private static bool IsMathOwnedInlineSemantic(MarkdownSemanticSpanKind kind) =>
        kind is MarkdownSemanticSpanKind.Emphasis or
            MarkdownSemanticSpanKind.Strong or
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode or
            MarkdownSemanticSpanKind.Image or
            MarkdownSemanticSpanKind.HtmlContainer or
            MarkdownSemanticSpanKind.HtmlMarker or
            MarkdownSemanticSpanKind.HtmlStrong or
            MarkdownSemanticSpanKind.HtmlEmphasis or
            MarkdownSemanticSpanKind.HtmlStrikethrough or
            MarkdownSemanticSpanKind.HtmlUnderline or
            MarkdownSemanticSpanKind.HtmlCode or
            MarkdownSemanticSpanKind.EscapeMarker;
}
