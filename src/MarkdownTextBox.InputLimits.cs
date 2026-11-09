using System.Windows;
using System.Windows.Threading;

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

        var structuralDelimiterLength = TrailingImageReferenceDelimiterLength(text);
        return MaxLength > int.MaxValue - structuralDelimiterLength
            ? int.MaxValue
            : MaxLength + structuralDelimiterLength;
    }

    private static int TrailingImageReferenceDelimiterLength(string text)
    {
        var delimiterStart = text.Length;
        if (delimiterStart > 0 && text[delimiterStart - 1] == '\n')
        {
            delimiterStart--;
            if (delimiterStart > 0 && text[delimiterStart - 1] == '\r')
            {
                delimiterStart--;
            }
        }
        else if (delimiterStart > 0 && text[delimiterStart - 1] == '\r')
        {
            delimiterStart--;
        }
        else
        {
            return 0;
        }

        var lineStart = delimiterStart;
        while (lineStart > 0 && text[lineStart - 1] is not '\r' and not '\n')
        {
            lineStart--;
        }

        return MarkdownImageReferences.TryParseReferenceLine(text[lineStart..delimiterStart], out _)
            ? text.Length - delimiterStart
            : 0;
    }

    private bool TryBuildSafePasteText(string text, int selectedLength, out string pasteText)
    {
        pasteText = text;
        if (text.Length > MaxSafePasteLength ||
            ContainsLineLongerThan(text, MaxSafePasteLineLength))
        {
            return false;
        }

        var candidateLength = Text.Length - Math.Max(0, selectedLength) + text.Length;
        return MaxLength <= 0 || candidateLength <= CurrentTextLengthLimit();
    }

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
        if (pasteText.Length > MaxSafePasteLength ||
            ContainsLineLongerThan(pasteText, MaxSafePasteLineLength))
        {
            return false;
        }

        var candidateText = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            pasteText);
        return MaxLength <= 0 || candidateText.Length <= TextLengthLimitFor(candidateText);
    }

    private void EnsureImageInsertionFits(string candidateText, int imageCount)
    {
        if (MaxLength <= 0 || candidateText.Length <= TextLengthLimitFor(candidateText))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot insert {imageCount} image reference(s): the note's {MaxLength:N0}-character limit would be exceeded.");
    }

    private bool CanApplyTextReplacement(string replacement)
    {
        if (MaxLength <= 0)
        {
            return true;
        }

        var start = Math.Clamp(SelectionStart, 0, Text.Length);
        var selectedLength = Math.Clamp(SelectionLength, 0, Text.Length - start);
        var projectedLength = checked(Text.Length - selectedLength + replacement.Length);
        if (projectedLength <= MaxLength)
        {
            return true;
        }

        // Never make an already-oversized legacy note larger, but always permit an edit that
        // reduces or preserves its length. Most importantly, never "repair" it by deleting a
        // different part of the document after the edit has happened.
        if (Text.Length > CurrentTextLengthLimit() && projectedLength <= Text.Length)
        {
            return true;
        }

        // Only a trailing delimiter after an internal image reference may live just beyond the
        // nominal limit. Build the exact candidate for that narrow boundary case.
        if (projectedLength > MaxLength + Environment.NewLine.Length)
        {
            return false;
        }

        var candidate = ReplaceRange(Text, start, selectedLength, replacement);
        return candidate.Length <= TextLengthLimitFor(candidate);
    }

    private bool CanApplyTextReplacementWithNotice(string replacement)
    {
        var inputAllowed = CanApplyTextReplacement(replacement);
        UpdateTextLimitNotice(inputAllowed);
        return inputAllowed;
    }

    private void UpdateTextLimitNotice(bool inputAllowed)
    {
        if (inputAllowed)
        {
            _textLimitNoticeShown = false;
            return;
        }

        if (_textLimitNoticeShown)
        {
            return;
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
    }
}
