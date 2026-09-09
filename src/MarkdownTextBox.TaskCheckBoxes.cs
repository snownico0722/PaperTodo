using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

/// <summary>
/// Full-mode Markdown task checkbox interaction. The source document remains authoritative:
/// clicking the rendered box only replaces the marker state character (`[ ]` ↔ `[x]`).
/// </summary>
public sealed partial class MarkdownTextBox
{
    private const double TaskCheckBoxHitPadding = 2.0;
    private bool _renderedTaskCheckBoxMouseDownHandled;

    /// <summary>
    /// PaperWindow installs a handled-events-too preview handler for the shared Markdown surface.
    /// Keep a narrow flag so that handler can recognize an interaction already consumed here and
    /// avoid reinterpreting the same click as "enter source editing".
    /// </summary>
    internal bool RenderedTaskCheckBoxMouseDownHandled =>
        _renderedTaskCheckBoxMouseDownHandled;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _renderedTaskCheckBoxMouseDownHandled = false;

        if (!e.Handled &&
            e.ChangedButton == MouseButton.Left &&
            TryGetRenderedTaskCheckBoxAtPoint(
                e.GetPosition(TextArea.TextView),
                out var task))
        {
            // A double-click sends two MouseDown events. Toggle only on the first press, but consume
            // the second one as well so it cannot unexpectedly open the task marker as source text.
            if (e.ClickCount <= 1)
            {
                TryToggleTaskMarker(task);
            }

            _renderedTaskCheckBoxMouseDownHandled = true;
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!e.Handled &&
            TryGetRenderedTaskCheckBoxAtPoint(
                e.GetPosition(TextArea.TextView),
                out _))
        {
            SetInteractionCursor(Cursors.Hand);
            e.Handled = true;
            return;
        }

        base.OnMouseMove(e);
    }

    /// <summary>
    /// Activates the rendered task checkbox at a TextView-local point. Interaction checks use this
    /// same source mutation path as the real mouse handler without synthesizing an OS mouse device.
    /// </summary>
    internal bool TryToggleRenderedTaskCheckBoxAtPoint(Point textViewPoint)
    {
        return TryGetRenderedTaskCheckBoxAtPoint(textViewPoint, out var task) &&
            TryToggleTaskMarker(task);
    }

    private bool TryGetRenderedTaskCheckBoxAtPoint(
        Point textViewPoint,
        out MarkdownSemanticSpan task)
    {
        task = default;
        if (!RenderModeIsFull ||
            IsCaretRevealGestureActive ||
            Document == null ||
            _semanticDocument == null ||
            !_semanticDocument.TryGetCurrent(out var snapshot))
        {
            return false;
        }

        var textView = TextArea.TextView;
        Point editorPoint;
        try
        {
            editorPoint = textView.TranslatePoint(textViewPoint, this);
        }
        catch
        {
            return false;
        }

        if (!TryGetCharacterIndexFromPoint(editorPoint, out var offset) ||
            !textView.VisualLinesValid)
        {
            return false;
        }

        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(Math.Clamp(offset, 0, Document.TextLength));
        }
        catch
        {
            return false;
        }

        foreach (var candidate in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
        {
            if (candidate.Kind != MarkdownSemanticSpanKind.TaskListMarker ||
                candidate.Length < 3 ||
                candidate.Start < line.Offset ||
                candidate.End > line.EndOffset ||
                IsTaskMarkerRevealed(line, candidate) ||
                !MarkdownTaskCheckBoxGeometry.TryGetRect(
                    textView,
                    line,
                    candidate,
                    out var rect))
            {
                continue;
            }

            rect.Inflate(TaskCheckBoxHitPadding, TaskCheckBoxHitPadding);
            if (rect.Contains(textViewPoint))
            {
                task = candidate;
                return true;
            }
        }

        return false;
    }

    private bool IsTaskMarkerRevealed(
        DocumentLine line,
        MarkdownSemanticSpan task)
    {
        if (IsPreviewMode || Document == null)
        {
            return false;
        }

        var caretOffset = Math.Clamp(CaretOffset, 0, Document.TextLength);
        var caretLine = Document.GetLineByOffset(caretOffset);
        return MarkdownSemanticReveal.RevealMarker(
            new MarkdownCaretReveal(caretOffset, caretLine.LineNumber - 1),
            line.LineNumber - 1,
            task.Start,
            task.Length,
            MarkdownSemanticSpanKind.TaskListMarker);
    }

    private bool TryToggleTaskMarker(MarkdownSemanticSpan task)
    {
        var document = Document;
        if (document == null ||
            task.Length < 3 ||
            task.Start < 0 ||
            task.Start + 3 > document.TextLength)
        {
            return false;
        }

        var marker = document.GetText(task.Start, 3);
        if (marker[0] != '[' || marker[2] != ']')
        {
            return false;
        }

        var replacement = marker[1] switch
        {
            ' ' => "x",
            'x' or 'X' => " ",
            _ => null
        };
        if (replacement == null)
        {
            return false;
        }

        // One source-character replacement is one normal AvalonEdit undo operation and flows through
        // the existing TextChanged/semantic snapshot/save pipeline; no second task state is stored.
        document.Replace(task.Start + 1, 1, replacement);
        return true;
    }
}

internal static class MarkdownTaskCheckBoxGeometry
{
    internal static bool TryGetRect(
        TextView textView,
        DocumentLine line,
        MarkdownSemanticSpan task,
        out Rect rect)
    {
        rect = Rect.Empty;
        if (!MarkdownSemanticPresentation.TryGetTextPoint(
                textView,
                line,
                task.Start,
                VisualYPosition.TextTop,
                out var topLeft) ||
            !MarkdownSemanticPresentation.TryGetTextPoint(
                textView,
                line,
                task.End,
                VisualYPosition.TextBottom,
                out var bottomRight))
        {
            return false;
        }

        var cellLeft = Math.Min(topLeft.X, bottomRight.X);
        var cellRight = Math.Max(topLeft.X, bottomRight.X);
        var height = Math.Max(1, bottomRight.Y - topLeft.Y);
        var boxSize = Math.Max(1, Math.Min(height * 0.7, cellRight - cellLeft));
        rect = new Rect(
            cellLeft + (cellRight - cellLeft - boxSize) / 2,
            topLeft.Y + (height - boxSize) / 2,
            boxSize,
            boxSize);
        return true;
    }
}
