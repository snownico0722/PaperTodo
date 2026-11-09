using System.Text;
using System.Windows.Documents;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    internal bool TryHandleSemanticEnter()
    {
        if (_isPreviewMode ||
            IsReadOnly ||
            Document == null ||
            Keyboard.Modifiers != ModifierKeys.None ||
            _semanticDocument == null)
        {
            return false;
        }

        if (!_acceptsReturn)
        {
            return true;
        }

        DocumentLine line;
        var caret = Math.Clamp(CaretOffset, 0, Document.TextLength);
        try
        {
            line = Document.GetLineByOffset(caret);
        }
        catch
        {
            return false;
        }

        if (SelectionLength == 0)
        {
            var text = Document.GetText(line);

            // 先让最内层引用尝试接管 Enter。TryContinueQuoteOnEnter 会在 `> - item` 这类
            // “列表比引用更内层”的行主动返回 false，因此不会破坏既有 list continuation；
            // `- > item` / `> - > item` 则由引用续行保留外层 list content indent。
            if (RenderModeIsFull && TryContinueQuoteOnEnter(line, text))
            {
                return true;
            }

            if (TryBuildSemanticListContinuationPlan(line, text, out var plan))
            {
                var indexInLine = Math.Clamp(caret - line.Offset, 0, text.Length);
                if (indexInLine >= Math.Min(plan.ContentStart, text.Length))
                {
                    if (IsLineContentEmpty(text, plan.EmptyContentStart))
                    {
                        if (indexInLine >= Math.Min(plan.EmptyContentStart, text.Length))
                        {
                            RemoveEmptyListMarker(line, plan.MarkerStart, plan.EmptyContentStart);
                            return true;
                        }
                    }
                    else
                    {
                        var insertion = NewLineTextFor(line) + plan.Continuation;
                        if (!CanApplyTextReplacementWithNotice(insertion))
                        {
                            return true;
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
                }
            }
        }

        if (!CanApplyTextReplacementWithNotice(NewLineTextAtCaret()))
        {
            return true;
        }

        TextArea.PerformTextInput("\n");
        return true;
    }

    private bool TryBuildSemanticListContinuationPlan(
        DocumentLine line,
        string text,
        out MarkdownListContinuationPlan plan)
    {
        plan = default;
        return TryGetCurrentSemanticSnapshot(out var snapshot) &&
            MarkdownListEditing.TryBuildContinuationPlan(
                text,
                line.Offset,
                snapshot,
                out plan);
    }

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

        if (snapshot.GetLine(Math.Max(0, line.LineNumber - 1)).QuoteLevel <= 0 ||
            !TryBuildQuoteContinuationPrefix(
                text,
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
        if (!CanApplyTextReplacement(insertion))
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
    /// 最内层容器是列表，则让列表逻辑处理。惰性续行缺少的引用层级由 Markdig QuoteLevel 补到新行。
    /// </summary>
    private static bool TryBuildQuoteContinuationPrefix(
        string text,
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd,
        out string prefix,
        out int contentStart)
    {
        prefix = string.Empty;
        contentStart = 0;

        var container = MarkdownContainerPrefix.Parse(
            text,
            snapshot,
            absoluteLineStart,
            absoluteLineEnd);
        if (container.QuoteLevel <= 0)
        {
            return false;
        }

        contentStart = container.ContentStart;
        if (container.MissingQuoteLevels == 0 &&
            container.InnermostTokenIndex >= 0 &&
            container.Tokens[container.InnermostTokenIndex].IsList)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length + container.QuoteLevel * 2);
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

    internal bool TryHandleSemanticBackspace()
    {
        if (_isPreviewMode ||
            IsReadOnly ||
            Document == null ||
            Keyboard.Modifiers != ModifierKeys.None ||
            _semanticDocument == null)
        {
            return false;
        }

        if (HasSelectedImageReference && TryDeleteSelectedImageReference())
        {
            return true;
        }

        if (TryDeleteSemanticImageBeforeCaret())
        {
            return true;
        }

        if (!EditingCommands.Backspace.CanExecute(null, TextArea))
        {
            return false;
        }

        EditingCommands.Backspace.Execute(null, TextArea);
        return true;
    }

    private bool TryDeleteSemanticImageBeforeCaret()
    {
        if (Document == null || SelectionLength != 0)
        {
            return false;
        }

        var caret = Math.Clamp(CaretOffset, 0, Document.TextLength);
        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(caret);
        }
        catch
        {
            return false;
        }

        if (caret == line.Offset &&
            line.PreviousLine != null &&
            TryGetImageReferenceForLine(
                line.PreviousLine,
                out var previousReference,
                out _))
        {
            DeleteImageReferenceLine(line.PreviousLine, previousReference.ImageId);
            return true;
        }

        if (line.NextLine == null &&
            caret == line.EndOffset &&
            TryGetImageReferenceForLine(
                line,
                out var currentReference,
                out _))
        {
            DeleteImageReferenceLine(line, currentReference.ImageId);
            return true;
        }

        return false;
    }
}
