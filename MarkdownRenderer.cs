using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;

namespace PaperTodo;

public static class MarkdownRenderer
{
    private static readonly DependencyProperty SourceStartProperty =
        DependencyProperty.RegisterAttached(
            "SourceStart",
            typeof(int),
            typeof(MarkdownRenderer),
            new PropertyMetadata(-1));

    private static readonly DependencyProperty SourceLengthProperty =
        DependencyProperty.RegisterAttached(
            "SourceLength",
            typeof(int),
            typeof(MarkdownRenderer),
            new PropertyMetadata(0));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UsePreciseSourceLocation()
        .Build();

    private static Brush TextBrush => Theme.TextBrush;
    private static Brush WeakBrush => Theme.WeakTextBrush;
    private static Brush CodeBrush => Theme.CodeBrush;
    private static Brush QuoteBorderBrush => Theme.QuoteBorderBrush;
    private static Brush LinkBrush => Theme.LinkBrush;

    public static FlowDocument Render(string? markdown)
    {
        var source = markdown ?? string.Empty;
        var document = CreateDocument();
        var parsed = Markdown.Parse(source, Pipeline);
        var previousBlockEnd = -1;

        foreach (var block in parsed)
        {
            AddBlankLines(document.Blocks, source, previousBlockEnd, block.Span.Start);
            AddBlock(document.Blocks, block, source);
            previousBlockEnd = block.Span.End;
        }

        AddBlankLines(document.Blocks, source, previousBlockEnd, source.Length);

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(CreateEmptyParagraph());
        }

        return document;
    }

    private readonly struct SourceLine
    {
        public SourceLine(int start, int contentEnd, int end)
        {
            Start = start;
            ContentEnd = contentEnd;
            End = end;
        }

        public int Start { get; }
        public int ContentEnd { get; }
        public int End { get; }
    }

    private readonly struct LazyContinuationSplit
    {
        public LazyContinuationSplit(
            int firstLineStart,
            int firstLineLength,
            int continuationStart,
            int continuationLength,
            int markerNumber)
        {
            FirstLineStart = firstLineStart;
            FirstLineLength = firstLineLength;
            ContinuationStart = continuationStart;
            ContinuationLength = continuationLength;
            MarkerNumber = markerNumber;
        }

        public int FirstLineStart { get; }
        public int FirstLineLength { get; }
        public int ContinuationStart { get; }
        public int ContinuationLength { get; }
        public int MarkerNumber { get; }
        public int FirstLineEndExclusive => FirstLineStart + FirstLineLength;
    }

    public static bool TryGetSourceSpan(TextElement element, out int start, out int length)
    {
        start = (int)element.GetValue(SourceStartProperty);
        length = (int)element.GetValue(SourceLengthProperty);
        return start >= 0 && length >= 0;
    }

    private static void SetSourceSpan(TextElement element, int start, int length)
    {
        if (start < 0)
        {
            return;
        }

        element.SetValue(SourceStartProperty, start);
        element.SetValue(SourceLengthProperty, Math.Max(0, length));
    }

    private static int SourceLength(MdBlock block)
    {
        return SourceLength(block.Span.Start, block.Span.End);
    }

    private static int SourceLength(int start, int end)
    {
        return start >= 0 && end >= start ? end - start + 1 : 0;
    }

    private static void AddBlankLines(BlockCollection blocks, string source, int previousBlockEnd, int nextBlockStart)
    {
        foreach (var line in EnumerateLines(source))
        {
            if (line.Start <= previousBlockEnd || line.ContentEnd > nextBlockStart)
            {
                continue;
            }

            if (line.ContentEnd > line.Start &&
                !string.IsNullOrWhiteSpace(source.Substring(line.Start, line.ContentEnd - line.Start)))
            {
                continue;
            }

            AddBlankLine(blocks, line.Start, Math.Max(0, line.End - line.Start));
        }
    }

    private static IEnumerable<SourceLine> EnumerateLines(string source)
    {
        var start = 0;
        while (start <= source.Length)
        {
            var index = start;
            while (index < source.Length && source[index] != '\r' && source[index] != '\n')
            {
                index++;
            }

            var contentEnd = index;
            if (index < source.Length)
            {
                if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index += 2;
                }
                else
                {
                    index++;
                }
            }

            yield return new SourceLine(start, contentEnd, index);

            if (index >= source.Length)
            {
                if (source.Length > 0 &&
                    (source[^1] == '\r' || source[^1] == '\n'))
                {
                    yield return new SourceLine(source.Length, source.Length, source.Length);
                }
                yield break;
            }

            start = index;
        }
    }

    private static void AddBlankLine(BlockCollection blocks, int sourceStart, int sourceLength)
    {
        var paragraph = new Paragraph
        {
            Margin = NoteTypography.ParagraphMargin,
            LineHeight = NoteTypography.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        SetSourceSpan(paragraph, sourceStart, sourceLength);

        var marker = new Run("\u200B")
        {
            Foreground = Brushes.Transparent
        };
        SetSourceSpan(marker, sourceStart, sourceLength);
        paragraph.Inlines.Add(marker);
        blocks.Add(paragraph);
    }

    private static FlowDocument CreateDocument()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = NoteTypography.FontFamily,
            FontSize = NoteTypography.FontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Foreground = TextBrush,
            Background = Brushes.Transparent,
            LineHeight = NoteTypography.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Language = NoteTypography.Language
        };
        NoteTypography.ApplyTextRendering(document);
        return document;
    }

    private static void AddBlock(BlockCollection blocks, MdBlock block, string source)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddHeading(blocks, heading, source);
                break;

            case ParagraphBlock paragraph:
                AddParagraph(blocks, paragraph, NoteTypography.ParagraphMargin, source);
                break;

            case QuoteBlock quote:
                AddQuote(blocks, quote, source);
                break;

            case Markdig.Syntax.ListBlock list:
                AddList(blocks, list, source);
                break;

            case FencedCodeBlock fenced:
                AddCodeBlock(blocks, fenced.Lines.ToString(), SourceStartForText(source, fenced.Span.Start, fenced.Span.End, fenced.Lines.ToString().TrimEnd('\r', '\n')), fenced.Span.Start, SourceLength(fenced));
                break;

            case CodeBlock code:
                AddIndentedCodeBlock(blocks, code, source);
                break;

            case ThematicBreakBlock thematicBreak:
                AddThematicBreak(blocks, thematicBreak.Span.Start, SourceLength(thematicBreak));
                break;

            case HtmlBlock:
                // PaperTodo intentionally does not support embedded HTML.
                break;

            default:
                if (block is ContainerBlock container)
                {
                    foreach (var child in container)
                    {
                        AddBlock(blocks, child, source);
                    }
                }
                break;
        }
    }

    private static void AddHeading(BlockCollection blocks, HeadingBlock heading, string source)
    {
        var size = heading.Level switch
        {
            1 => 18,
            2 => 16,
            3 => 15,
            _ => NoteTypography.FontSize
        };

        var paragraph = CreateParagraph(heading.Inline, NoteTypography.HeadingMargin, source, heading.Span.Start, SourceLength(heading));
        paragraph.FontSize = size;
        paragraph.FontWeight = NoteTypography.HeadingFontWeight;
        blocks.Add(paragraph);
    }

    private static void AddParagraph(BlockCollection blocks, ParagraphBlock paragraphBlock, Thickness margin, string source)
    {
        if (IsPlainTextParagraph(paragraphBlock.Inline))
        {
            var sourceStart = SourceStartIncludingPlainIndent(source, paragraphBlock.Span.Start);
            var sourceEnd = Math.Min(source.Length, paragraphBlock.Span.End + 1);
            AddExactTextParagraphs(blocks, source, sourceStart, Math.Max(0, sourceEnd - sourceStart), margin);
            return;
        }

        blocks.Add(CreateParagraph(paragraphBlock.Inline, margin, source, paragraphBlock.Span.Start, SourceLength(paragraphBlock)));
    }

    private static Paragraph CreateParagraph(ContainerInline? inline, Thickness margin, string source, int sourceStart, int sourceLength)
    {
        var paragraph = new Paragraph
        {
            Margin = margin,
            LineHeight = NoteTypography.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        SetSourceSpan(paragraph, sourceStart, sourceLength);
        AddInlines(paragraph.Inlines, inline, source);
        return paragraph;
    }

    private static void AddExactTextParagraphs(BlockCollection blocks, string source, int sourceStart, int sourceLength, Thickness margin)
    {
        var sourceEnd = Math.Min(source.Length, sourceStart + sourceLength);
        var index = Math.Clamp(sourceStart, 0, source.Length);
        var spaceWidth = MeasureNormalSpaceWidth();

        while (index < sourceEnd)
        {
            var lineEnd = FindLineContentEnd(source, index, sourceEnd);
            var nextLineStart = FindNextLineStart(source, lineEnd, sourceEnd);
            var lineSourceLength = Math.Max(lineEnd - index, nextLineStart - index);
            blocks.Add(CreateExactTextLineParagraph(source, index, lineEnd - index, lineSourceLength, margin, spaceWidth));

            if (nextLineStart <= lineEnd)
            {
                break;
            }
            index = nextLineStart;
        }
    }

    private static Paragraph CreateExactTextLineParagraph(
        string source,
        int lineStart,
        int lineTextLength,
        int lineSourceLength,
        Thickness margin,
        double spaceWidth)
    {
        var sourceEnd = Math.Min(source.Length, lineStart + Math.Max(0, lineTextLength));
        var indentLength = CountLeadingIndent(source, lineStart, sourceEnd);
        var indentWidth = IndentWidth(source, lineStart, indentLength, spaceWidth);
        var paragraph = new Paragraph
        {
            Margin = new Thickness(margin.Left + indentWidth, margin.Top, margin.Right, margin.Bottom),
            LineHeight = NoteTypography.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        SetSourceSpan(paragraph, lineStart, lineSourceLength);

        var textStart = lineStart + indentLength;
        var textLength = Math.Max(0, lineTextLength - indentLength);
        if (textLength > 0)
        {
            var text = source.Substring(textStart, textLength);
            var run = new Run(text);
            SetSourceSpan(run, textStart, text.Length);
            paragraph.Inlines.Add(run);
        }
        else
        {
            var marker = new Run("\u200B")
            {
                Foreground = Brushes.Transparent
            };
            SetSourceSpan(marker, lineStart, lineSourceLength);
            paragraph.Inlines.Add(marker);
        }

        return paragraph;
    }

    private static int CountLeadingIndent(string source, int start, int endExclusive)
    {
        var index = Math.Clamp(start, 0, source.Length);
        var end = Math.Clamp(endExclusive, index, source.Length);
        while (index < end && (source[index] == ' ' || source[index] == '\t'))
        {
            index++;
        }

        return index - start;
    }

    private static double IndentWidth(string source, int start, int length, double spaceWidth)
    {
        var width = 0.0;
        var end = Math.Min(source.Length, start + Math.Max(0, length));
        for (var index = Math.Clamp(start, 0, source.Length); index < end; index++)
        {
            width += source[index] == '\t' ? spaceWidth * 4 : spaceWidth;
        }

        return width;
    }

    private static double MeasureNormalSpaceWidth()
    {
        var formatted = new FormattedText(
            " ",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                NoteTypography.FontFamily,
                NoteTypography.FontStyle,
                NoteTypography.FontWeight,
                NoteTypography.FontStretch),
            NoteTypography.FontSize,
            Brushes.Black,
            1);

        return Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
    }

    private static bool IsPlainTextParagraph(ContainerInline? container)
    {
        if (container == null)
        {
            return true;
        }

        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline:
                case LineBreakInline:
                    break;
                case ContainerInline nested when IsPlainTextParagraph(nested):
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static int SourceStartIncludingPlainIndent(string source, int sourceStart)
    {
        if (sourceStart <= 0 || sourceStart > source.Length)
        {
            return Math.Clamp(sourceStart, 0, source.Length);
        }

        var lineStart = sourceStart;
        while (lineStart > 0 && source[lineStart - 1] != '\r' && source[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        for (var i = lineStart; i < sourceStart; i++)
        {
            if (source[i] != ' ' && source[i] != '\t')
            {
                return sourceStart;
            }
        }

        return lineStart;
    }

    private static Paragraph CreateEmptyParagraph()
    {
        return new Paragraph
        {
            Margin = NoteTypography.ParagraphMargin,
            LineHeight = NoteTypography.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
    }

    private static void AddQuote(BlockCollection blocks, QuoteBlock quote, string source)
    {
        var section = new Section
        {
            Margin = NoteTypography.QuoteMargin,
            Padding = NoteTypography.QuotePadding,
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = QuoteBorderBrush,
            Foreground = WeakBrush
        };
        SetSourceSpan(section, quote.Span.Start, SourceLength(quote));

        foreach (var child in quote)
        {
            AddBlock(section.Blocks, child, source);
        }

        blocks.Add(section);
    }

    private static void AddList(BlockCollection blocks, Markdig.Syntax.ListBlock list, string source)
    {
        if (AddListWithSplitLazyContinuations(blocks, list, source))
        {
            return;
        }

        var wpfList = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = NoteTypography.ListMargin,
            Padding = NoteTypography.ListPadding
        };

        if (list.IsOrdered &&
            int.TryParse(list.OrderedStart, out var orderedStart) &&
            orderedStart > 0)
        {
            wpfList.StartIndex = orderedStart;
        }

        SetSourceSpan(wpfList, list.Span.Start, SourceLength(list));

        foreach (var rawItem in list)
        {
            if (rawItem is not ListItemBlock item)
            {
                continue;
            }

            var wpfItem = new WpfListItem();
            SetSourceSpan(wpfItem, item.Span.Start, SourceLength(item));

            foreach (var child in item)
            {
                AddBlock(wpfItem.Blocks, child, source);
            }

            EnsureBlocksNotEmpty(wpfItem.Blocks);

            wpfList.ListItems.Add(wpfItem);
        }

        blocks.Add(wpfList);
    }

    private static bool AddListWithSplitLazyContinuations(BlockCollection blocks, Markdig.Syntax.ListBlock list, string source)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        if (!items.Any(item => TrySplitLazyContinuation(item, source, list.IsOrdered, out _)))
        {
            return false;
        }

        var fallbackStart = TryParseOrderedStart(list.OrderedStart, out var parsedStart) ? parsedStart : 1;
        WpfList? currentList = null;
        var currentListStart = -1;
        var currentListEnd = -1;

        void EnsureCurrentList(int startIndex, int sourceStart)
        {
            if (currentList != null)
            {
                return;
            }

            currentList = CreateWpfList(list, startIndex);
            currentListStart = sourceStart;
            currentListEnd = sourceStart;
        }

        void FlushCurrentList(bool compactBottom)
        {
            if (currentList == null)
            {
                return;
            }

            if (compactBottom)
            {
                var margin = currentList.Margin;
                currentList.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
            }

            SetSourceSpan(currentList, currentListStart, SourceLength(currentListStart, currentListEnd));
            blocks.Add(currentList);
            currentList = null;
            currentListStart = -1;
            currentListEnd = -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var startIndex = TryGetListMarker(source, item.Span.Start, list.IsOrdered, out var marker)
                ? marker
                : fallbackStart + index;

            var wpfItem = new WpfListItem();
            SetSourceSpan(wpfItem, item.Span.Start, SourceLength(item));

            if (TrySplitLazyContinuation(item, source, list.IsOrdered, out var split))
            {
                EnsureCurrentList(startIndex, item.Span.Start);
                AddFragmentBlocks(wpfItem.Blocks, source, split.FirstLineStart, split.FirstLineLength);
                EnsureBlocksNotEmpty(wpfItem.Blocks);
                currentList!.ListItems.Add(wpfItem);
                currentListEnd = Math.Max(currentListEnd, Math.Max(item.Span.Start, split.FirstLineEndExclusive - 1));
                FlushCurrentList(compactBottom: true);
                AddFragmentBlocks(blocks, source, split.ContinuationStart, split.ContinuationLength);
            }
            else
            {
                EnsureCurrentList(startIndex, item.Span.Start);
                AddItemBlocks(wpfItem.Blocks, item, source);
                EnsureBlocksNotEmpty(wpfItem.Blocks);
                currentList!.ListItems.Add(wpfItem);
                currentListEnd = Math.Max(currentListEnd, item.Span.End);
            }
        }

        FlushCurrentList(compactBottom: false);
        return true;
    }

    private static WpfList CreateWpfList(Markdig.Syntax.ListBlock list, int startIndex)
    {
        var wpfList = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = NoteTypography.ListMargin,
            Padding = NoteTypography.ListPadding
        };

        if (list.IsOrdered && startIndex > 0)
        {
            wpfList.StartIndex = startIndex;
        }

        return wpfList;
    }

    private static void AddItemBlocks(BlockCollection blocks, ListItemBlock item, string source)
    {
        foreach (var child in item)
        {
            AddBlock(blocks, child, source);
        }
    }

    private static void EnsureBlocksNotEmpty(BlockCollection blocks)
    {
        if (blocks.Count == 0)
        {
            blocks.Add(CreateEmptyParagraph());
        }
    }

    private static void AddFragmentBlocks(BlockCollection blocks, string source, int fragmentStart, int fragmentLength)
    {
        var sourceStart = Math.Clamp(fragmentStart, 0, source.Length);
        var sourceEnd = Math.Clamp(fragmentStart + Math.Max(0, fragmentLength), sourceStart, source.Length);
        if (sourceEnd <= sourceStart)
        {
            return;
        }

        var fragment = source.Substring(sourceStart, sourceEnd - sourceStart);
        var parsed = Markdown.Parse(fragment, Pipeline);
        OffsetSpans(parsed, sourceStart);

        var previousBlockEnd = sourceStart - 1;
        foreach (var block in parsed)
        {
            AddBlankLines(blocks, source, previousBlockEnd, block.Span.Start);
            AddBlock(blocks, block, source);
            previousBlockEnd = block.Span.End;
        }

        AddBlankLines(blocks, source, previousBlockEnd, sourceEnd);
    }

    private static void OffsetSpans(MarkdownObject markdownObject, int offset)
    {
        if (offset != 0 && markdownObject.Span.Start >= 0)
        {
            var span = markdownObject.Span;
            span.Start += offset;
            span.End += offset;
            markdownObject.Span = span;
        }

        switch (markdownObject)
        {
            case ContainerBlock container:
                foreach (var child in container)
                {
                    OffsetSpans(child, offset);
                }
                break;

            case LeafBlock { Inline: { } inline }:
                OffsetSpans(inline, offset);
                break;

            case ContainerInline containerInline:
                for (var child = containerInline.FirstChild; child != null; child = child.NextSibling)
                {
                    OffsetSpans(child, offset);
                }
                break;
        }
    }

    private static bool TrySplitLazyContinuation(
        ListItemBlock item,
        string source,
        bool ordered,
        out LazyContinuationSplit split)
    {
        split = default;

        if (item.Span.Start < 0 || item.Span.Start >= source.Length)
        {
            return false;
        }

        var itemStart = item.Span.Start;
        var itemEndExclusive = Math.Min(source.Length, item.Span.End + 1);
        var lineEnd = FindLineContentEnd(source, itemStart, itemEndExclusive);
        var nextLineStart = FindNextLineStart(source, lineEnd, itemEndExclusive);
        if (nextLineStart >= itemEndExclusive)
        {
            return false;
        }

        if (!TryGetListContentStart(source, itemStart, lineEnd, ordered, out var contentStart, out var markerNumber))
        {
            return false;
        }

        var contentColumn = Math.Max(0, contentStart - itemStart);
        if (!StartsLazyContinuation(source, nextLineStart, itemEndExclusive, contentColumn))
        {
            return false;
        }

        var continuationLength = Math.Max(0, itemEndExclusive - nextLineStart);
        if (continuationLength == 0 ||
            string.IsNullOrWhiteSpace(source.Substring(nextLineStart, continuationLength)))
        {
            return false;
        }

        split = new LazyContinuationSplit(
            contentStart,
            Math.Max(0, lineEnd - contentStart),
            nextLineStart,
            continuationLength,
            markerNumber);
        return true;
    }

    private static bool StartsLazyContinuation(string source, int lineStart, int endExclusive, int contentColumn)
    {
        var lineEnd = FindLineContentEnd(source, lineStart, endExclusive);
        var i = lineStart;
        var spaces = 0;
        while (i < lineEnd && source[i] == ' ')
        {
            i++;
            spaces++;
        }

        if (i >= lineEnd || spaces >= contentColumn)
        {
            return false;
        }

        return !LooksLikeListMarker(source, i, lineEnd);
    }

    private static bool LooksLikeListMarker(string source, int start, int lineEnd)
    {
        if (start + 1 < lineEnd &&
            (source[start] == '-' || source[start] == '*' || source[start] == '+') &&
            char.IsWhiteSpace(source[start + 1]))
        {
            return true;
        }

        var i = start;
        while (i < lineEnd && char.IsDigit(source[i]))
        {
            i++;
        }

        return i > start &&
               i < lineEnd &&
               (source[i] == '.' || source[i] == ')') &&
               i + 1 < lineEnd &&
               char.IsWhiteSpace(source[i + 1]);
    }

    private static bool TryGetListMarker(string source, int lineStart, bool ordered, out int markerNumber)
    {
        markerNumber = 1;
        if (lineStart < 0 || lineStart >= source.Length)
        {
            return false;
        }

        var lineEnd = FindLineContentEnd(source, lineStart, source.Length);
        return TryGetListContentStart(source, lineStart, lineEnd, ordered, out _, out markerNumber);
    }

    private static bool TryGetListContentStart(
        string source,
        int lineStart,
        int lineEnd,
        bool ordered,
        out int contentStart,
        out int markerNumber)
    {
        contentStart = lineStart;
        markerNumber = 1;

        var i = lineStart;
        var spaces = 0;
        while (i < lineEnd && source[i] == ' ' && spaces < 4)
        {
            i++;
            spaces++;
        }

        if (ordered)
        {
            var numberStart = i;
            while (i < lineEnd && char.IsDigit(source[i]))
            {
                i++;
            }

            if (i == numberStart ||
                i >= lineEnd ||
                (source[i] != '.' && source[i] != ')') ||
                i + 1 >= lineEnd ||
                !char.IsWhiteSpace(source[i + 1]) ||
                !int.TryParse(source.Substring(numberStart, i - numberStart), out markerNumber))
            {
                return false;
            }

            i += 2;
        }
        else
        {
            if (i + 1 >= lineEnd ||
                (source[i] != '-' && source[i] != '*' && source[i] != '+') ||
                !char.IsWhiteSpace(source[i + 1]))
            {
                return false;
            }

            i += 2;
        }

        while (i < lineEnd && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        contentStart = i;
        return true;
    }

    private static bool TryParseOrderedStart(string? orderedStart, out int start)
    {
        return int.TryParse(orderedStart, out start) && start > 0;
    }

    private static int FindLineContentEnd(string source, int lineStart, int endExclusive)
    {
        var i = Math.Clamp(lineStart, 0, source.Length);
        var end = Math.Clamp(endExclusive, 0, source.Length);
        while (i < end && source[i] != '\r' && source[i] != '\n')
        {
            i++;
        }
        return i;
    }

    private static int FindNextLineStart(string source, int lineEnd, int endExclusive)
    {
        var i = Math.Clamp(lineEnd, 0, source.Length);
        var end = Math.Clamp(endExclusive, 0, source.Length);
        if (i < end && source[i] == '\r')
        {
            i++;
            if (i < end && source[i] == '\n')
            {
                i++;
            }
        }
        else if (i < end && source[i] == '\n')
        {
            i++;
        }
        return i;
    }

    private static void AddCodeBlock(BlockCollection blocks, string code, int sourceStart, int blockSourceStart, int blockSourceLength)
    {
        var renderedCode = code.TrimEnd('\r', '\n');
        var paragraph = new Paragraph
        {
            FontFamily = NoteTypography.CodeFontFamily,
            FontSize = NoteTypography.CodeFontSize,
            Background = CodeBrush,
            Padding = NoteTypography.CodeBlockPadding,
            Margin = NoteTypography.CodeBlockMargin
        };
        SetSourceSpan(paragraph, blockSourceStart, blockSourceLength);

        var run = new Run(renderedCode);
        SetSourceSpan(run, sourceStart, renderedCode.Length);
        paragraph.Inlines.Add(run);
        blocks.Add(paragraph);
    }

    private static void AddIndentedCodeBlock(BlockCollection blocks, CodeBlock code, string source)
    {
        var sourceStart = SourceStartIncludingPlainIndent(source, code.Span.Start);
        var sourceEnd = Math.Min(source.Length, code.Span.End + 1);
        AddExactTextParagraphs(
            blocks,
            source,
            sourceStart,
            Math.Max(0, sourceEnd - sourceStart),
            NoteTypography.ParagraphMargin);
    }

    private static void AddThematicBreak(BlockCollection blocks, int sourceStart, int sourceLength)
    {
        var paragraph = new Paragraph
        {
            Margin = NoteTypography.ThematicBreakMargin,
            Foreground = WeakBrush
        };
        SetSourceSpan(paragraph, sourceStart, sourceLength);
        paragraph.Inlines.Add(new Run("────────────────"));
        blocks.Add(paragraph);
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container, string source)
    {
        if (container == null)
        {
            return;
        }

        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            AddInline(target, inline, source);
        }
    }

    private static void AddInline(InlineCollection target, MdInline inline, string source)
    {
        switch (inline)
        {
            case LiteralInline literal:
                {
                    var text = literal.Content.ToString();
                    var run = new Run(text);
                    SetSourceSpan(run, literal.Span.Start, text.Length);
                    target.Add(run);
                }
                break;

            case CodeInline code:
                {
                    var run = new Run(code.Content)
                    {
                        FontFamily = NoteTypography.CodeFontFamily,
                        FontSize = NoteTypography.CodeFontSize,
                        Background = CodeBrush
                    };
                    SetSourceSpan(run, SourceStartForText(source, code.Span.Start, code.Span.End, code.Content), code.Content.Length);
                    target.Add(run);
                }
                break;

            case EmphasisInline emphasis:
                target.Add(RenderEmphasis(emphasis, source));
                break;

            case LinkInline link:
                AddLink(target, link, source);
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case HtmlInline:
                // PaperTodo intentionally does not support embedded HTML.
                break;

            case ContainerInline container:
                AddInlines(target, container, source);
                break;

            default:
                if (inline is LeafInline leaf)
                {
                    var text = leaf.ToString() ?? string.Empty;
                    var run = new Run(text);
                    SetSourceSpan(run, leaf.Span.Start, text.Length);
                    target.Add(run);
                }
                break;
        }
    }

    private static WpfInline RenderEmphasis(EmphasisInline emphasis, string source)
    {
        var span = new Span();
        AddInlines(span.Inlines, emphasis, source);

        if (emphasis.DelimiterChar == '~')
        {
            span.TextDecorations = TextDecorations.Strikethrough;
            span.Foreground = WeakBrush;
            return span;
        }

        if (emphasis.DelimiterCount >= 2)
        {
            return new Bold(span);
        }

        return new Italic(span);
    }

    private static void AddLink(InlineCollection target, LinkInline link, string source)
    {
        if (link.IsImage)
        {
            // Images are intentionally unsupported. Render the alt text only.
            var alt = new Span { Foreground = WeakBrush };
            AddInlines(alt.Inlines, link, source);
            target.Add(alt);
            return;
        }

        var label = new Span();
        AddInlines(label.Inlines, link, source);

        if (label.Inlines.Count == 0)
        {
            label.Inlines.Add(new Run(link.Url ?? Strings.Get("MarkdownDefaultLinkLabel")));
        }

        var hyperlink = new Hyperlink(label)
        {
            Foreground = LinkBrush
        };

        if (!string.IsNullOrEmpty(link.Url) && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += OpenLink;
            hyperlink.Cursor = System.Windows.Input.Cursors.Hand;
        }

        target.Add(hyperlink);
    }

    private static int SourceStartForText(string source, int spanStart, int spanEnd, string text)
    {
        if (spanStart < 0)
        {
            return -1;
        }

        if (string.IsNullOrEmpty(text) || spanStart >= source.Length)
        {
            return spanStart;
        }

        var endExclusive = spanEnd >= spanStart
            ? Math.Min(source.Length, spanEnd + 1)
            : source.Length;
        if (endExclusive <= spanStart)
        {
            return spanStart;
        }

        var spanText = source.Substring(spanStart, endExclusive - spanStart);
        var index = spanText.IndexOf(text, StringComparison.Ordinal);
        return index >= 0 ? spanStart + index : spanStart;
    }
    private static void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Keep preview quiet if Windows cannot open the link.
        }
    }
}
