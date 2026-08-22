using System;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    static MarkdownTextBox()
    {
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            UIElement.MouseMoveEvent,
            new MouseEventHandler(OnOptimizedLinkHoverMouseMove));
    }

    private static void OnOptimizedLinkHoverMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not MarkdownTextBox box ||
            (!box.IsPreviewMode &&
             (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control))
        {
            return;
        }

        var isOverLink = box.TryGetOpenableLinkFromTextViewPointFast(
            e.GetPosition(box.TextArea.TextView),
            out _);
        box.SetInteractionCursor(
            isOverLink
                ? Cursors.Hand
                : box.IsPreviewMode ? Cursors.Arrow : Cursors.IBeam);

        // PaperWindow's older instance MouseMove handler performs a full visible-line scan.
        // The class handler runs first, so handling this event keeps hover hit-testing local to
        // the line under the pointer while click handling retains the existing full parser path.
        e.Handled = true;
    }

    private bool TryGetOpenableLinkFromTextViewPointFast(Point point, out string url)
    {
        url = "";
        if (Document == null)
        {
            return false;
        }

        try
        {
            EnsureVisualLines();
            var textView = TextArea.TextView;
            if (!textView.VisualLinesValid)
            {
                return false;
            }

            var editorPoint = textView.TranslatePoint(point, this);
            if (!TryGetCharacterIndexFromPoint(editorPoint, out var characterIndex))
            {
                return false;
            }

            var offset = Math.Clamp(characterIndex, 0, Document.TextLength);
            var line = Document.GetLineByOffset(offset);
            var relativeOffset = Math.Clamp(offset - line.Offset, 0, line.Length);

            if (RenderOptions.HighlightLinks)
            {
                var text = Document.GetText(line);
                foreach (var link in EnumerateInlineLinks(text))
                {
                    if (relativeOffset >= link.Start &&
                        relativeOffset <= link.End &&
                        IsLinkSegmentHit(
                            point,
                            line.Offset + link.Start,
                            link.End - link.Start))
                    {
                        url = link.Url;
                        return true;
                    }
                }
            }

            var analysis = GetLineAnalysis(Document, line);
            if (analysis.Style.Kind is MarkdownLineKind.CodeFence or MarkdownLineKind.CodeBlock)
            {
                return false;
            }

            foreach (var link in EnumerateBareWebLinks(analysis.Text))
            {
                if (relativeOffset >= link.Start &&
                    relativeOffset <= link.End &&
                    IsLinkSegmentHit(
                        point,
                        line.Offset + link.Start,
                        link.End - link.Start))
                {
                    url = link.Url;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private bool IsLinkSegmentHit(Point point, int startOffset, int length)
    {
        if (length <= 0)
        {
            return false;
        }

        var textView = TextArea.TextView;
        var segment = new TextSegment
        {
            StartOffset = startOffset,
            Length = length
        };

        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(
                     textView,
                     segment,
                     true))
        {
            var hitRect = new Rect(
                rect.X - 2,
                rect.Y - 2,
                rect.Width + 4,
                rect.Height + 4);
            if (hitRect.Contains(point))
            {
                return true;
            }
        }

        return false;
    }
}
