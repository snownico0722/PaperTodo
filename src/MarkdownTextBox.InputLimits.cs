using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private const int MaxSafePasteLength = 30000;
    private const int MaxSafePasteLineLength = 6000;

    private bool _textLimitNoticeShown;

    public int MaxLength { get; set; }

    private int CurrentTextLengthLimit()
    {
        return TextLengthLimitFor(Text);
    }

    private int TextLengthLimitFor(string text)
    {
        if (MaxLength <= 0 || text.Length <= 0)
        {
            return MaxLength;
        }

        var structuralDelimiterLength = MarkdownImageReferences.TrailingAnchorDelimiterLength(text);
        return MaxLength > int.MaxValue - structuralDelimiterLength
            ? int.MaxValue
            : MaxLength + structuralDelimiterLength;
    }

    private bool TryBuildSafePasteText(string text, int selectedLength, out string pasteText)
    {
        pasteText = text;
        return IsSafePasteSize(text) && CanApplyTextReplacement(text, SelectionStart, selectedLength);
    }

    private static bool IsSafePasteSize(string text) =>
        text.Length <= MaxSafePasteLength && !ContainsLineLongerThan(text, MaxSafePasteLineLength);

    private static bool ContainsLineLongerThan(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return false;
        }

        var lineLength = 0;
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                lineLength = 0;
                continue;
            }

            lineLength++;
            if (lineLength > maxLength)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSafeImageReferencePaste(
        string pasteText,
        DocumentReplacementTarget replacementTarget)
    {
        if (!IsSafePasteSize(pasteText))
        {
            return false;
        }

        var candidateText = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            pasteText);
        return CanAcceptDocumentReplacement(replacementTarget.OriginalText, candidateText);
    }

    private bool CanAcceptDocumentReplacement(string original, string candidate)
    {
        return MaxLength <= 0 || candidate.Length <= TextLengthLimitFor(candidate) ||
            (candidate.Length <= original.Length && original.Length > TextLengthLimitFor(original));
    }

    private void EnsureImageInsertionFits(string original, string candidateText, int imageCount)
    {
        if (CanAcceptDocumentReplacement(original, candidateText))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot insert {imageCount} image reference(s): the note's {MaxLength:N0}-character limit would be exceeded.");
    }

    private bool CanApplyTextReplacement(string replacement) =>
        CanApplyTextReplacement(replacement, SelectionStart, SelectionLength);

    private bool CanApplyTextReplacement(string replacement, int start, int selectedLength)
    {
        if (MaxLength <= 0)
        {
            return true;
        }

        var originalLength = Document.TextLength;
        var projectedLength = (long)originalLength - selectedLength + replacement.Length;
        if (projectedLength <= MaxLength)
        {
            return true;
        }

        // Never make an already-oversized legacy note larger, but always permit an edit that
        // reduces or preserves its length. Most importantly, never "repair" it by deleting a
        // different part of the document after the edit has happened.
        if (projectedLength <= originalLength && originalLength > CurrentTextLengthLimit())
        {
            return true;
        }

        // Only a trailing delimiter after an internal image reference may live just beyond the
        // nominal limit. Build the exact candidate for that narrow boundary case.
        if (projectedLength > (long)MaxLength + Environment.NewLine.Length)
        {
            return false;
        }

        var candidate = ReplaceRange(Text, start, selectedLength, replacement);
        return candidate.Length <= TextLengthLimitFor(candidate);
    }

    private bool CanApplyTextReplacementWithNotice(string replacement) =>
        CheckTextLimitWithNotice(CanApplyTextReplacement(replacement));

    private bool CanInsertNewLineWithNotice()
    {
        if (Document == null || MaxLength <= 0)
        {
            return true;
        }

        var start = SelectionStart;
        var selectedLength = SelectionLength;
        var newLine = TextUtilities.GetNewLineFromDocument(Document, TextArea.Caret.Line);
        // AvalonEdit's default indentation also replaces the new line's leading whitespace
        // with the indentation left before the split. Include both sides of that edit.
        var line = Document.GetLineByOffset(start);
        var indent = TextUtilities.GetWhitespaceAfter(Document, line.Offset);
        var copiedLength = Math.Min(indent.Length, start - line.Offset);
        var replacedIndent = TextUtilities.GetWhitespaceAfter(Document, start + selectedLength);
        var replacement = newLine + Document.GetText(line.Offset, copiedLength);
        return CheckTextLimitWithNotice(CanApplyTextReplacement(
            replacement, start, selectedLength + replacedIndent.Length));
    }

    private bool CanIndentSelection()
    {
        if (!TextArea.Selection.IsMultiline)
        {
            return CanApplyTextReplacement(Options.GetIndentationString(TextArea.Caret.Column));
        }

        // AvalonEdit indents each touched line without replacing the selected text.
        var range = TextArea.Selection.SurroundingSegment;
        var first = Document.GetLineByOffset(range.Offset);
        var last = Document.GetLineByOffset(range.EndOffset);
        var lineCount = last.LineNumber - first.LineNumber + (range.EndOffset == last.Offset ? 0 : 1);
        var addedLength = (long)lineCount * Options.IndentationString.Length;
        return MaxLength <= 0 || Document.TextLength + addedLength <= CurrentTextLengthLimit();
    }

    private bool CheckTextLimitWithNotice(bool inputAllowed)
    {
        if (inputAllowed)
        {
            _textLimitNoticeShown = false;
            return true;
        }

        if (_textLimitNoticeShown)
        {
            return false;
        }

        _textLimitNoticeShown = true;
        var maximumCharacters = MaxLength;
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (Window.GetWindow(this) is not PaperWindow owner)
                {
                    return;
                }

                PaperNoticeDialog.Show(
                    owner,
                    Strings.Get("NoteInputLimitTitle"),
                    Strings.Format(
                        "NoteInputLimitMessage",
                        maximumCharacters));
            }),
            DispatcherPriority.Background);
        return false;
    }
}
