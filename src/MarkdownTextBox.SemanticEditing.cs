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
                        if (MaxLength > 0 && Text.Length + insertion.Length > MaxLength)
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

        if (!CanApplyTextReplacement(NewLineTextAtCaret()))
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
