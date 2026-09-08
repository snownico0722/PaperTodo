using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    /// <summary>
    /// Full 编辑态在引用内容行按 Enter 时续写当前最内层引用容器；若引用外还有更内层列表，返回 false
    /// 让列表续行接管。空引用行同样返回 false，交既有默认/列表退出逻辑处理。
    /// </summary>
    private bool TryContinueQuoteOnEnter(DocumentLine line, string text)
    {
        if (!RenderModeIsFull || !TryGetCurrentSemanticSnapshot(out var snapshot))
        {
            return false;
        }

        var level = snapshot.GetLine(Math.Max(0, line.LineNumber - 1)).QuoteLevel;
        if (level <= 0 ||
            !TryBuildQuoteContinuationPrefix(
                text,
                level,
                snapshot,
                line.Offset,
                line.EndOffset,
                out var prefix,
                out var contentStart))
        {
            return false;
        }

        var caret = Math.Clamp(CaretOffset, 0, Document!.TextLength);
        var indexInLine = Math.Clamp(caret - line.Offset, 0, text.Length);
        if (indexInLine < contentStart)
        {
            // 光标还停在容器前缀里：不主动续行，走既有默认处理。
            return false;
        }

        if (IsQuoteLineEmpty(text, contentStart))
        {
            // 空引用行 Enter 不主动续 quote；由列表/默认换行决定如何退出当前容器。
            return false;
        }

        var insertion = NewLineTextFor(line) + prefix;
        if (MaxLength > 0 && Text.Length + insertion.Length > MaxLength)
        {
            return false;
        }

        Document.BeginUpdate();
        try
        {
            Document.Insert(caret, insertion);
            CaretOffset = caret + insertion.Length;
            Select(CaretOffset, 0);
        }
        finally
        {
            Document.EndUpdate();
        }

        return true;
    }

    /// <summary>
    /// 生成引用续行前缀。列表 marker 若位于待续引用之外，会被替换成等长空白，使新行保持在同一个
    /// list item content 内；`>` 本身按语义 QuoteLevel 续写。若当前物理行最后一个语义 list marker
    /// 位于最后一个显式 quote marker 之后，说明最内层容器其实是列表（例如 `> - item`），此处让路。
    /// </summary>
    private static bool TryBuildQuoteContinuationPrefix(
        string text,
        int quoteLevel,
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd,
        out string prefix,
        out int contentStart)
    {
        prefix = string.Empty;
        contentStart = 0;
        if (quoteLevel <= 0)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length + quoteLevel * 2);
        var index = 0;
        var explicitQuoteLevels = 0;
        var lastQuoteStart = -1;

        while (index < text.Length && explicitQuoteLevels < quoteLevel)
        {
            var whitespaceStart = index;
            while (index < text.Length && text[index] is ' ' or '\t')
            {
                index++;
            }
            builder.Append(text, whitespaceStart, index - whitespaceStart);

            if (index < text.Length && text[index] == '>')
            {
                lastQuoteStart = index;
                explicitQuoteLevels++;
                index++;
                if (index < text.Length && text[index] is ' ' or '\t')
                {
                    index++;
                }

                // 用户主动续行时统一产生标准 `> `；原有物理源码本身保持不变。
                builder.Append("> ");
                continue;
            }

            var listMarkerStart = index;
            if (TryConsumeQuoteOuterListMarker(text, ref index))
            {
                // 外层 list item 不应在 Enter 后变成一个新的 sibling marker。用等长空白替代 marker，
                // 同时保留其后的原始空白/tab，使引用继续位于原 list content column。
                for (var sourceIndex = listMarkerStart; sourceIndex < index; sourceIndex++)
                {
                    builder.Append(char.IsWhiteSpace(text[sourceIndex])
                        ? text[sourceIndex]
                        : ' ');
                }
                continue;
            }

            break;
        }

        var missingQuoteLevels = quoteLevel - explicitQuoteLevels;
        if (missingQuoteLevels > 0)
        {
            // 惰性续行没有物理 `>`；语义 QuoteLevel 仍是 authority，新行恢复显式 marker。
            builder.Append(MarkdownQuoteMarkers.RepeatMarkerPrefix(missingQuoteLevels));
        }

        contentStart = index;
        var lastListMarkerStart = FindLastListMarkerStartOnLine(
            snapshot,
            absoluteLineStart,
            absoluteLineEnd);
        if (missingQuoteLevels == 0 && lastListMarkerStart > lastQuoteStart)
        {
            // `> - item` / `- > - item` 等情况下，最后一个 list marker 比最后一个 quote 更内层；
            // 保持既有列表续行语义，不让 quote 抢占 Enter。
            return false;
        }

        prefix = builder.ToString();
        return prefix.Length > 0;
    }

    private static int FindLastListMarkerStartOnLine(
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd)
    {
        var last = -1;
        foreach (var span in snapshot.Spans)
        {
            if (span.Start >= absoluteLineEnd)
            {
                break;
            }
            if (span.Start < absoluteLineStart ||
                span.End > absoluteLineEnd ||
                span.Kind is not (
                    MarkdownSemanticSpanKind.UnorderedListMarker or
                    MarkdownSemanticSpanKind.OrderedListMarker))
            {
                continue;
            }

            last = Math.Max(last, span.Start - absoluteLineStart);
        }

        return last;
    }

    private static bool TryConsumeQuoteOuterListMarker(string text, ref int index)
    {
        var markerStart = index;
        if (markerStart >= text.Length)
        {
            return false;
        }

        var markerEnd = markerStart;
        if (text[markerStart] is '-' or '+' or '*')
        {
            markerEnd++;
        }
        else if (char.IsAsciiDigit(text[markerStart]))
        {
            var digits = 0;
            while (markerEnd < text.Length &&
                   digits < 9 &&
                   char.IsAsciiDigit(text[markerEnd]))
            {
                markerEnd++;
                digits++;
            }

            if (markerEnd < text.Length && char.IsAsciiDigit(text[markerEnd]))
            {
                return false;
            }
            if (markerEnd >= text.Length || text[markerEnd] is not ('.' or ')'))
            {
                return false;
            }
            markerEnd++;
        }
        else
        {
            return false;
        }

        if (markerEnd < text.Length && text[markerEnd] is not (' ' or '\t'))
        {
            return false;
        }

        index = markerEnd;
        while (index < text.Length && text[index] is ' ' or '\t')
        {
            index++;
        }
        return true;
    }

    private static bool IsQuoteLineEmpty(string text, int contentStart)
    {
        for (var index = contentStart; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
