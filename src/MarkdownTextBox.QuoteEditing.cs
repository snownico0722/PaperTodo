using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    /// <summary>
    /// Full 编辑态在引用内容行按 Enter 时续写当前最内层引用容器；若最内层真实容器是列表，返回
    /// false 让列表续行接管。空引用行同样交既有退出逻辑处理。
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
        if (indexInLine < contentStart || IsQuoteLineEmpty(text, contentStart))
        {
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
    /// 生成引用续行前缀。外层列表 marker 变为等宽空白，外层引用保留，最内层引用继续；若真实
    /// 最内层容器是列表，则让列表逻辑处理。惰性续行缺少的引用层级按 QuoteLevel 补到新行。
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

        var container = MarkdownContainerPrefix.Parse(
            text,
            quoteLevel,
            snapshot,
            absoluteLineStart,
            absoluteLineEnd);
        contentStart = container.ContentStart;

        if (container.MissingQuoteLevels == 0 &&
            container.InnermostTokenIndex >= 0 &&
            container.Tokens[container.InnermostTokenIndex].IsList)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length + quoteLevel * 2);
        var cursor = 0;
        foreach (var token in container.Tokens)
        {
            AppendRange(builder, text, cursor, token.MarkerStart);
            if (token.IsQuote)
            {
                builder.Append("> ");
            }
            else
            {
                AppendListContentIndent(builder, text, token.MarkerStart, token.ContentStart);
            }

            cursor = token.ContentStart;
        }

        AppendRange(builder, text, cursor, container.ContentStart);
        if (container.MissingQuoteLevels > 0)
        {
            builder.Append(MarkdownQuoteMarkers.RepeatMarkerPrefix(container.MissingQuoteLevels));
        }

        prefix = builder.ToString();
        return prefix.Length > 0;
    }

    internal static void AppendListContentIndent(
        StringBuilder builder,
        string text,
        int start,
        int end)
    {
        for (var index = Math.Clamp(start, 0, text.Length);
             index < Math.Clamp(end, 0, text.Length);
             index++)
        {
            builder.Append(char.IsWhiteSpace(text[index]) ? text[index] : ' ');
        }
    }

    internal static void AppendRange(
        StringBuilder builder,
        string text,
        int start,
        int end)
    {
        var normalizedStart = Math.Clamp(start, 0, text.Length);
        var normalizedEnd = Math.Clamp(end, normalizedStart, text.Length);
        if (normalizedEnd > normalizedStart)
        {
            builder.Append(text, normalizedStart, normalizedEnd - normalizedStart);
        }
    }

    private static bool IsQuoteLineEmpty(string text, int contentStart)
    {
        for (var index = Math.Clamp(contentStart, 0, text.Length); index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
