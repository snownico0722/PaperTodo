using Markdig.Syntax;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    /// <summary>
    /// 递归收集块级语义。quoteDepth 为「当前容器内的引用嵌套深度」（0=文档根）；遇 QuoteBlock
    /// 子块时其自身层级 = quoteDepth+1，其内容继续 +1。引用 span 的 Level 即引用层级，供
    /// ApplySpanToLines 推导每行 QuoteLevel——惰性续行/嵌套行都不依赖物理 `&gt;`。
    /// </summary>
    private static void CollectBlocks(
        ContainerBlock container,
        string source,
        int[] lineStarts,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links,
        int quoteDepth = 0)
    {
        foreach (var block in container)
        {
            var childQuoteDepth = block is QuoteBlock ? quoteDepth + 1 : quoteDepth;
            CollectBlock(block, source, lineStarts, spans, childQuoteDepth);
            if (block is LeafBlock { Inline: { } inlineRoot })
            {
                CollectInlines(inlineRoot, source, spans, links);
            }
            if (block is ContainerBlock nested)
            {
                CollectBlocks(nested, source, lineStarts, spans, links, childQuoteDepth);
            }
        }
    }

    private static void CollectBlock(
        Block block,
        string source,
        int[] lineStarts,
        List<MarkdownSemanticSpan> spans,
        int quoteDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                CollectHeading(heading, source, lineStarts, spans);
                break;

            case QuoteBlock quote:
                AddSpan(spans, MarkdownSemanticSpanKind.Quote, quote, source.Length, quoteDepth);
                break;

            case ListBlock list:
                AddSpan(
                    spans,
                    list.IsOrdered
                        ? MarkdownSemanticSpanKind.OrderedList
                        : MarkdownSemanticSpanKind.UnorderedList,
                    list,
                    source.Length);
                break;

            case ListItemBlock item:
                CollectListItemMarker(item, source, spans);
                break;

            case FencedCodeBlock fenced:
                CollectFencedCode(fenced, source, lineStarts, spans);
                break;

            case CodeBlock code:
                AddSpan(spans, MarkdownSemanticSpanKind.Code, code, source.Length);
                break;

            case ThematicBreakBlock rule:
                AddSpan(spans, MarkdownSemanticSpanKind.HorizontalRule, rule, source.Length);
                break;
        }
    }

    private static void CollectListItemMarker(
        ListItemBlock item,
        string source,
        List<MarkdownSemanticSpan> spans)
    {
        if (item.Parent is not ListBlock list ||
            item.Span.IsEmpty ||
            item.Span.Start < 0 ||
            item.Span.Start >= source.Length)
        {
            return;
        }

        var start = Math.Clamp(item.Span.Start, 0, source.Length - 1);
        var end = start;
        var kind = list.IsOrdered
            ? MarkdownSemanticSpanKind.OrderedListMarker
            : MarkdownSemanticSpanKind.UnorderedListMarker;

        if (list.IsOrdered)
        {
            while (end < source.Length && char.IsDigit(source[end]))
            {
                end++;
            }

            if (end == start ||
                end >= source.Length ||
                source[end] != list.OrderedDelimiter)
            {
                return;
            }
            end++;
        }
        else
        {
            if (source[start] != list.BulletType)
            {
                return;
            }
            end = start + 1;
        }

        spans.Add(new MarkdownSemanticSpan(kind, start, end - start));
    }

    private static void CollectFencedCode(
        FencedCodeBlock fenced,
        string source,
        int[] lineStarts,
        List<MarkdownSemanticSpan> spans)
    {
        if (fenced.Span.IsEmpty || fenced.Span.Start < 0 || lineStarts.Length == 0)
        {
            return;
        }

        var openingLine = FindLineIndex(source, lineStarts, fenced.Span.Start);
        var lastSemanticLine = openingLine;

        // For an unclosed fence Markdig intentionally leaves Block.Span at the opening line, but
        // FencedCodeBlock.Lines still retain each actual content line with its original Position.
        // Use those source positions as the authoritative content extent instead of guessing from
        // raw backticks or assuming the block span always reaches EOF.
        for (var index = 0; index < fenced.Lines.Count; index++)
        {
            var sourcePosition = fenced.Lines.Lines[index].Position;
            if (sourcePosition < 0 || sourcePosition > source.Length)
            {
                continue;
            }

            lastSemanticLine = Math.Max(
                lastSemanticLine,
                FindLineIndex(source, lineStarts, sourcePosition));
        }

        var closingLine = -1;
        if (fenced.ClosingFencedCharCount > 0 && fenced.Span.End >= fenced.Span.Start)
        {
            closingLine = FindLineIndex(
                source,
                lineStarts,
                Math.Clamp(fenced.Span.End, 0, source.Length));
            if (closingLine >= openingLine)
            {
                lastSemanticLine = Math.Max(lastSemanticLine, closingLine);
            }
        }

        AddLineRangeSpan(
            spans,
            MarkdownSemanticSpanKind.FencedCode,
            source,
            lineStarts,
            openingLine,
            lastSemanticLine);
        AddWholeLineSpan(
            spans,
            MarkdownSemanticSpanKind.FencedCodeOpening,
            source,
            lineStarts,
            openingLine);

        if (closingLine >= openingLine)
        {
            AddWholeLineSpan(
                spans,
                MarkdownSemanticSpanKind.FencedCodeClosing,
                source,
                lineStarts,
                closingLine);
        }
    }

    private static void AddLineRangeSpan(
        List<MarkdownSemanticSpan> spans,
        MarkdownSemanticSpanKind kind,
        string source,
        int[] lineStarts,
        int firstLine,
        int lastLine)
    {
        if (firstLine < 0 ||
            lastLine < firstLine ||
            firstLine >= lineStarts.Length)
        {
            return;
        }

        lastLine = Math.Min(lastLine, lineStarts.Length - 1);
        var start = lineStarts[firstLine];
        var nextStart = lastLine + 1 < lineStarts.Length
            ? lineStarts[lastLine + 1]
            : source.Length;
        var end = TrimLineDelimiterEnd(source, nextStart);
        if (end > start)
        {
            spans.Add(new MarkdownSemanticSpan(kind, start, end - start));
        }
    }

    private static void AddWholeLineSpan(
        List<MarkdownSemanticSpan> spans,
        MarkdownSemanticSpanKind kind,
        string source,
        int[] lineStarts,
        int lineIndex)
    {
        AddLineRangeSpan(spans, kind, source, lineStarts, lineIndex, lineIndex);
    }

    private static void CollectHeading(
        HeadingBlock heading,
        string source,
        int[] lineStarts,
        List<MarkdownSemanticSpan> spans)
    {
        if (!heading.IsSetext)
        {
            AddSpan(
                spans,
                MarkdownSemanticSpanKind.Heading,
                heading,
                source.Length,
                heading.Level);
            return;
        }

        // For Setext headings Markdig's Line points at the underline row while Span.Start still
        // points at the content. Derive both rows from the exact source span instead of Line.
        var spanStart = Math.Clamp(heading.Span.Start, 0, source.Length);
        var spanEnd = Math.Clamp(heading.Span.End + 1, spanStart, source.Length);
        if (spanEnd <= spanStart)
        {
            return;
        }

        var firstLine = FindLineIndex(source, lineStarts, spanStart);
        var markerLine = FindLineIndex(source, lineStarts, spanEnd - 1);
        if (markerLine <= firstLine)
        {
            AddSpan(
                spans,
                MarkdownSemanticSpanKind.Heading,
                heading,
                source.Length,
                heading.Level);
            return;
        }

        var headingStart = lineStarts[firstLine];
        var markerStart = lineStarts[markerLine];
        var contentEnd = TrimLineDelimiterEnd(source, markerStart);
        if (contentEnd > headingStart)
        {
            spans.Add(new MarkdownSemanticSpan(
                MarkdownSemanticSpanKind.SetextHeading,
                headingStart,
                contentEnd - headingStart,
                heading.Level));
        }

        if (spanEnd > markerStart)
        {
            spans.Add(new MarkdownSemanticSpan(
                MarkdownSemanticSpanKind.SetextMarker,
                markerStart,
                spanEnd - markerStart,
                heading.Level));
        }
    }

    private static void AddSpan(
        List<MarkdownSemanticSpan> spans,
        MarkdownSemanticSpanKind kind,
        Block block,
        int sourceLength,
        int level = 0)
    {
        var sourceSpan = block.Span;
        if (sourceSpan.IsEmpty || sourceSpan.Start < 0 || sourceSpan.End < sourceSpan.Start)
        {
            return;
        }

        var start = Math.Clamp(sourceSpan.Start, 0, sourceLength);
        var end = Math.Clamp(sourceSpan.End + 1, start, sourceLength);
        if (end > start)
        {
            spans.Add(new MarkdownSemanticSpan(kind, start, end - start, level));
        }
    }

    private static void ApplySpanToLines(
        string source,
        int[] lineStarts,
        MarkdownSemanticLine[] lines,
        MarkdownSemanticSpan span)
    {
        if (span.Length <= 0 || lines.Length == 0)
        {
            return;
        }

        var trait = span.Kind switch
        {
            MarkdownSemanticSpanKind.SetextHeading => MarkdownSemanticLineTraits.SetextHeading,
            MarkdownSemanticSpanKind.Quote => MarkdownSemanticLineTraits.Quote,
            MarkdownSemanticSpanKind.UnorderedList => MarkdownSemanticLineTraits.UnorderedList,
            MarkdownSemanticSpanKind.OrderedList => MarkdownSemanticLineTraits.OrderedList,
            MarkdownSemanticSpanKind.Code => MarkdownSemanticLineTraits.Code,
            MarkdownSemanticSpanKind.FencedCode =>
                MarkdownSemanticLineTraits.Code | MarkdownSemanticLineTraits.FencedCode,
            MarkdownSemanticSpanKind.FencedCodeOpening =>
                MarkdownSemanticLineTraits.FencedCodeOpening,
            MarkdownSemanticSpanKind.FencedCodeClosing =>
                MarkdownSemanticLineTraits.FencedCodeClosing,
            MarkdownSemanticSpanKind.HorizontalRule => MarkdownSemanticLineTraits.HorizontalRule,
            MarkdownSemanticSpanKind.SetextMarker => MarkdownSemanticLineTraits.SetextMarker,
            _ => MarkdownSemanticLineTraits.None
        };
        var isHeading = span.Kind is
            MarkdownSemanticSpanKind.Heading or MarkdownSemanticSpanKind.SetextHeading;
        var isQuote = span.Kind == MarkdownSemanticSpanKind.Quote;
        if (trait == MarkdownSemanticLineTraits.None && !isHeading)
        {
            return;
        }

        var firstLine = FindLineIndex(source, lineStarts, span.Start);
        var lastOffset = Math.Max(span.Start, span.End - 1);
        var lastLine = FindLineIndex(source, lineStarts, lastOffset);
        for (var line = firstLine; line <= lastLine && line < lines.Length; line++)
        {
            var current = lines[line];
            var headingLevel = isHeading
                ? Math.Clamp(span.Level, 1, 6)
                : current.HeadingLevel;
            // 引用层级取「覆盖该行的最深引用 span」：外层 span 先遍历设 1，内层随后 max 到 2。
            // Heading/其它 span 只写各自的 Level，不触碰 QuoteLevel。
            var quoteLevel = isQuote
                ? Math.Max(current.QuoteLevel, span.Level)
                : current.QuoteLevel;
            lines[line] = new MarkdownSemanticLine(
                current.Traits | trait,
                headingLevel,
                quoteLevel);
        }
    }

    internal static int[] BuildLineStarts(string source)
    {
        var starts = new List<int>(Math.Max(1, source.Length / 32)) { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
                starts.Add(index + 1);
            }
            else if (source[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }

    private static int FindLineIndex(string source, int[] lineStarts, int offset)
    {
        if (lineStarts.Length == 0)
        {
            return 0;
        }

        var normalized = Math.Clamp(offset, 0, source.Length);
        var index = Array.BinarySearch(lineStarts, normalized);
        return index >= 0
            ? index
            : Math.Max(0, ~index - 1);
    }

    private static int TrimLineDelimiterEnd(string source, int nextLineStart)
    {
        var end = Math.Clamp(nextLineStart, 0, source.Length);
        if (end > 0 && source[end - 1] == '\n')
        {
            end--;
        }
        if (end > 0 && source[end - 1] == '\r')
        {
            end--;
        }
        return end;
    }
}
