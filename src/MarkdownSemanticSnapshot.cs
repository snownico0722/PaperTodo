using System.Buffers;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;

namespace PaperTodo;

[Flags]
internal enum MarkdownSemanticLineTraits
{
    None = 0,
    Quote = 1 << 0,
    UnorderedList = 1 << 1,
    OrderedList = 1 << 2,
    Code = 1 << 3,
    FencedCode = 1 << 4,
    HorizontalRule = 1 << 5,
    SetextMarker = 1 << 6,
    SetextHeading = 1 << 7,
    FencedCodeOpening = 1 << 8,
    FencedCodeClosing = 1 << 9
}

internal enum MarkdownSemanticSpanKind
{
    Heading,
    SetextHeading,
    Quote,
    UnorderedList,
    OrderedList,
    UnorderedListMarker,
    OrderedListMarker,
    Code,
    FencedCode,
    FencedCodeOpening,
    FencedCodeClosing,
    HorizontalRule,
    SetextMarker,
    Emphasis,
    Strong,
    Strikethrough,
    InlineCode,
    InlineMath,
    BlockMath,
    Image,
    TaskListMarker,
    HtmlContainer,
    HtmlMarker,
    HtmlStrong,
    HtmlEmphasis,
    HtmlStrikethrough,
    HtmlUnderline,
    HtmlCode,
    EscapeMarker
}

internal readonly record struct MarkdownSemanticSpan(
    MarkdownSemanticSpanKind Kind,
    int Start,
    int Length,
    int Level = 0,
    int MarkerLength = 0,
    bool Checked = false)
{
    public int End => Start + Length;
}

internal readonly record struct MarkdownSemanticLink(
    int Start,
    int Length,
    int LabelStart,
    int LabelLength,
    int DestinationStart,
    int DestinationLength,
    string Url,
    bool IsAuto)
{
    public int End => Start + Length;
    public int LabelEnd => LabelStart + LabelLength;
    public int DestinationEnd => DestinationStart + DestinationLength;

    /// <summary>
    /// 链接 label 之外是否带有可见语法控制符（显式链接的 `[`/`](url)`、autolink 的两侧
    /// `&lt;`/`&gt;`、HTML &lt;a&gt; 的开闭标签）。裸链（label 即全文）为 false：无语法可塌缩。
    /// </summary>
    public bool HasVisibleSyntax => Start < LabelStart || LabelEnd < End;
}

internal readonly record struct MarkdownSemanticLine(
    MarkdownSemanticLineTraits Traits,
    int HeadingLevel,
    int QuoteLevel)
{
    /// <summary>
    /// 是否属于某个引用块（含惰性续行：物理行可无 <c>&gt;</c>，只要被 Markdig 判为引用段内容）。
    /// QuoteLevel 与 Traits.Quote 由同一批引用 span 推导，二者一致（见 Blocks.ApplySpanToLines）。
    /// </summary>
    public bool IsQuoted =>
        (Traits & MarkdownSemanticLineTraits.Quote) != 0 || QuoteLevel > 0;

    public bool IsCode =>
        (Traits & MarkdownSemanticLineTraits.Code) != 0;

    public bool IsFencedCode =>
        (Traits & MarkdownSemanticLineTraits.FencedCode) != 0;

    public bool IsFencedCodeOpening =>
        (Traits & MarkdownSemanticLineTraits.FencedCodeOpening) != 0;

    public bool IsFencedCodeClosing =>
        (Traits & MarkdownSemanticLineTraits.FencedCodeClosing) != 0;

    public bool IsFencedCodeMarker =>
        IsFencedCodeOpening || IsFencedCodeClosing;

    public bool IsHorizontalRule =>
        (Traits & MarkdownSemanticLineTraits.HorizontalRule) != 0;

    public bool IsSetextMarker =>
        (Traits & MarkdownSemanticLineTraits.SetextMarker) != 0;

    public bool IsSetextHeading =>
        (Traits & MarkdownSemanticLineTraits.SetextHeading) != 0;
}

/// <summary>
/// Immutable Markdig-derived Markdown semantics for one exact source version. Source offsets stay
/// in the original Markdown coordinate space; PaperTodo never creates a second rendered document.
/// Markdig AST objects are flattened immediately into PaperTodo-owned records.
/// </summary>
internal sealed partial class MarkdownSemanticSnapshot
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .Build();

    private readonly record struct MarkdownLineIndexRange(int Start, int Count);

    private readonly struct MarkdownLineIndex<T>
    {
        public static MarkdownLineIndex<T> Empty { get; } = new(
            Array.Empty<T>(),
            Array.Empty<MarkdownLineIndexRange>());

        private readonly T[] _items;
        private readonly MarkdownLineIndexRange[] _ranges;

        public MarkdownLineIndex(T[] items, MarkdownLineIndexRange[] ranges)
        {
            _items = items;
            _ranges = ranges;
        }

        public ReadOnlySpan<T> ForLine(int zeroBasedLine)
        {
            if ((uint)zeroBasedLine >= (uint)_ranges.Length)
            {
                return ReadOnlySpan<T>.Empty;
            }

            var range = _ranges[zeroBasedLine];
            return _items.AsSpan(range.Start, range.Count);
        }
    }

    public static MarkdownSemanticSnapshot Empty { get; } = new(
        Array.Empty<int>(),
        Array.Empty<MarkdownSemanticLine>(),
        Array.Empty<MarkdownSemanticSpan>(),
        Array.Empty<MarkdownSemanticLink>(),
        MarkdownLineIndex<MarkdownSemanticSpan>.Empty,
        MarkdownLineIndex<MarkdownSemanticLink>.Empty);

    private readonly int[] _lineStarts;
    private readonly MarkdownSemanticLine[] _lines;
    private readonly MarkdownSemanticSpan[] _spans;
    private readonly MarkdownSemanticLink[] _links;
    private readonly MarkdownLineIndex<MarkdownSemanticSpan> _spansByLine;
    private readonly MarkdownLineIndex<MarkdownSemanticLink> _linksByLine;
    private readonly bool _hasReferenceDefinitions;

    /// <summary>该快照源版本的行起点表（下标=零基行号）。解析/增量路径都已算过，直接携带供折叠等复用，避免再次逐字符扫行。</summary>
    internal int[] LineStarts => _lineStarts;

    private MarkdownSemanticSnapshot(
        int[] lineStarts,
        MarkdownSemanticLine[] lines,
        MarkdownSemanticSpan[] spans,
        MarkdownSemanticLink[] links,
        MarkdownLineIndex<MarkdownSemanticSpan> spansByLine,
        MarkdownLineIndex<MarkdownSemanticLink> linksByLine,
        bool hasReferenceDefinitions = false)
    {
        _lineStarts = lineStarts;
        _lines = lines;
        _spans = spans;
        _links = links;
        _spansByLine = spansByLine;
        _linksByLine = linksByLine;
        _hasReferenceDefinitions = hasReferenceDefinitions;
    }

    public IReadOnlyList<MarkdownSemanticSpan> Spans => _spans;
    public IReadOnlyList<MarkdownSemanticLink> Links => _links;
    public int LineCount => _lines.Length;

    public MarkdownSemanticLine GetLine(int zeroBasedLine)
    {
        if (zeroBasedLine < 0 || zeroBasedLine >= _lines.Length)
        {
            return default;
        }

        return _lines[zeroBasedLine];
    }

    public ReadOnlySpan<MarkdownSemanticSpan> SpansForLine(int zeroBasedLine) =>
        _spansByLine.ForLine(zeroBasedLine);

    public ReadOnlySpan<MarkdownSemanticLink> LinksForLine(int zeroBasedLine) =>
        _linksByLine.ForLine(zeroBasedLine);

    public bool TryGetLinkAtOffset(int offset, out MarkdownSemanticLink link)
    {
        var low = 0;
        var high = _links.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = _links[middle];
            if (offset < candidate.Start)
            {
                high = middle - 1;
                continue;
            }
            if (offset >= candidate.End)
            {
                low = middle + 1;
                continue;
            }

            link = candidate;
            return true;
        }

        link = default;
        return false;
    }

    public static MarkdownSemanticSnapshot Parse(string? markdown)
    {
        var source = markdown ?? string.Empty;
        var lineStarts = BuildLineStarts(source);
        var spans = new List<MarkdownSemanticSpan>();
        var links = new List<MarkdownSemanticLink>();
        var document = Markdown.Parse(source, Pipeline);
        var hasReferenceDefinitions =
            document.GetLinkReferenceDefinitions(false).Links.Count != 0;

        CollectBlocks(document, source, lineStarts, spans, links);
        ApplyLegacyCompatibilityBoundaries(source, spans, links);
        CollectBareHttpLinks(source, spans, links);
        CollectMathSemantics(source, spans, links);
        CollectEscapeMarkers(source, spans);
        spans.Sort(CompareSemanticSpans);
        links.Sort(CompareSemanticLinks);

        var lines = new MarkdownSemanticLine[lineStarts.Length];
        foreach (var span in spans)
        {
            ApplySpanToLines(source, lineStarts, lines, span);
        }

        var spanArray = spans.ToArray();
        var linkArray = links.ToArray();
        return new MarkdownSemanticSnapshot(
            lineStarts,
            lines,
            spanArray,
            linkArray,
            BuildSpanLineIndex(source, lineStarts, spanArray),
            BuildLinkLineIndex(source, lineStarts, linkArray),
            hasReferenceDefinitions);
    }

    /// <summary>
    /// Canonical snapshot ordering must not depend on List.Sort's unstable treatment of equal keys.
    /// Equal source ranges are common (for example an empty list item can make the list container and
    /// its marker occupy the same character). Full and local parses can contain different list sizes,
    /// so a complete tie-breaker is required for byte-for-byte-equivalent incremental snapshots.
    /// </summary>
    private static int CompareSemanticSpans(MarkdownSemanticSpan left, MarkdownSemanticSpan right)
    {
        var comparison = left.Start.CompareTo(right.Start);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.End.CompareTo(right.End);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.Level.CompareTo(right.Level);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.MarkerLength.CompareTo(right.MarkerLength);
        return comparison != 0
            ? comparison
            : left.Checked.CompareTo(right.Checked);
    }

    private static int CompareSemanticLinks(MarkdownSemanticLink left, MarkdownSemanticLink right)
    {
        var comparison = left.Start.CompareTo(right.Start);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.End.CompareTo(right.End);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.LabelStart.CompareTo(right.LabelStart);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.LabelLength.CompareTo(right.LabelLength);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.DestinationStart.CompareTo(right.DestinationStart);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.DestinationLength.CompareTo(right.DestinationLength);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.IsAuto.CompareTo(right.IsAuto);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Url, right.Url);
    }

    private static MarkdownLineIndex<MarkdownSemanticSpan> BuildSpanLineIndex(
        string source,
        int[] lineStarts,
        IReadOnlyList<MarkdownSemanticSpan> spans)
    {
        var ranges = new MarkdownLineIndexRange[lineStarts.Length];
        if (lineStarts.Length == 0 || spans.Count == 0)
        {
            return new MarkdownLineIndex<MarkdownSemanticSpan>(
                Array.Empty<MarkdownSemanticSpan>(),
                ranges);
        }

        var positions = ArrayPool<int>.Shared.Rent(lineStarts.Length);
        var lineBounds = ArrayPool<int>.Shared.Rent(spans.Count * 2);
        try
        {
            Array.Clear(positions, 0, lineStarts.Length);
            var itemCount = 0;
            for (var index = 0; index < spans.Count; index++)
            {
                var span = spans[index];
                var boundIndex = index * 2;
                if (span.Length <= 0)
                {
                    lineBounds[boundIndex] = -1;
                    continue;
                }

                var first = FindLineIndex(source, lineStarts, span.Start);
                var last = FindLineIndex(source, lineStarts, Math.Max(span.Start, span.End - 1));
                lineBounds[boundIndex] = first;
                lineBounds[boundIndex + 1] = last;
                for (var line = first; line <= last && line < lineStarts.Length; line++)
                {
                    positions[line]++;
                    itemCount++;
                }
            }

            var offset = 0;
            for (var line = 0; line < ranges.Length; line++)
            {
                var count = positions[line];
                ranges[line] = new MarkdownLineIndexRange(offset, count);
                positions[line] = offset;
                offset += count;
            }

            var items = new MarkdownSemanticSpan[itemCount];
            for (var index = 0; index < spans.Count; index++)
            {
                var boundIndex = index * 2;
                var first = lineBounds[boundIndex];
                if (first < 0)
                {
                    continue;
                }

                var last = lineBounds[boundIndex + 1];
                var span = spans[index];
                for (var line = first; line <= last && line < lineStarts.Length; line++)
                {
                    items[positions[line]++] = span;
                }
            }

            return new MarkdownLineIndex<MarkdownSemanticSpan>(items, ranges);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lineBounds);
            ArrayPool<int>.Shared.Return(positions);
        }
    }

    private static MarkdownLineIndex<MarkdownSemanticLink> BuildLinkLineIndex(
        string source,
        int[] lineStarts,
        IReadOnlyList<MarkdownSemanticLink> links)
    {
        var ranges = new MarkdownLineIndexRange[lineStarts.Length];
        if (lineStarts.Length == 0 || links.Count == 0)
        {
            return new MarkdownLineIndex<MarkdownSemanticLink>(
                Array.Empty<MarkdownSemanticLink>(),
                ranges);
        }

        var positions = ArrayPool<int>.Shared.Rent(lineStarts.Length);
        var lineBounds = ArrayPool<int>.Shared.Rent(links.Count * 2);
        try
        {
            Array.Clear(positions, 0, lineStarts.Length);
            var itemCount = 0;
            for (var index = 0; index < links.Count; index++)
            {
                var link = links[index];
                var boundIndex = index * 2;
                if (link.Length <= 0)
                {
                    lineBounds[boundIndex] = -1;
                    continue;
                }

                var first = FindLineIndex(source, lineStarts, link.Start);
                var last = FindLineIndex(source, lineStarts, Math.Max(link.Start, link.End - 1));
                lineBounds[boundIndex] = first;
                lineBounds[boundIndex + 1] = last;
                for (var line = first; line <= last && line < lineStarts.Length; line++)
                {
                    positions[line]++;
                    itemCount++;
                }
            }

            var offset = 0;
            for (var line = 0; line < ranges.Length; line++)
            {
                var count = positions[line];
                ranges[line] = new MarkdownLineIndexRange(offset, count);
                positions[line] = offset;
                offset += count;
            }

            var items = new MarkdownSemanticLink[itemCount];
            for (var index = 0; index < links.Count; index++)
            {
                var boundIndex = index * 2;
                var first = lineBounds[boundIndex];
                if (first < 0)
                {
                    continue;
                }

                var last = lineBounds[boundIndex + 1];
                var link = links[index];
                for (var line = first; line <= last && line < lineStarts.Length; line++)
                {
                    items[positions[line]++] = link;
                }
            }

            return new MarkdownLineIndex<MarkdownSemanticLink>(items, ranges);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lineBounds);
            ArrayPool<int>.Shared.Return(positions);
        }
    }
}
