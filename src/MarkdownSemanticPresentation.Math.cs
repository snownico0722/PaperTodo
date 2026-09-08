using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private MathElementGenerator? _mathElementGenerator;
    private MarkdownSemanticSpan? _lastRevealedMathSpan;

    private bool RenderMath =>
        ApplyMarkdownStyle && (_editor.IsPreviewMode || IsFullMode);

    private void AttachMathPresentation()
    {
        _mathElementGenerator = new MathElementGenerator(this);
        // Formula replacement must win when it starts at the same source offset as a normal
        // syntax-collapse cell.
        _editor.TextArea.TextView.ElementGenerators.Insert(0, _mathElementGenerator);
        _editor.SizeChanged += OnMathHostSizeChanged;
    }

    private void DetachMathPresentation()
    {
        _editor.SizeChanged -= OnMathHostSizeChanged;
        if (_mathElementGenerator != null)
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_mathElementGenerator);
            _mathElementGenerator = null;
        }

        _lastRevealedMathSpan = null;
    }

    private void OnMathHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_disposed && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
        {
            ScheduleRedraw();
        }
    }

    private void ResetMathPresentationState()
    {
        _lastRevealedMathSpan = null;
    }

    /// <summary>
    /// Math replacement is independent of the normal syntax-collapse table. Rebuild the affected
    /// visual line only when the caret enters or leaves a formula; moving inside the same formula
    /// keeps the source layout stable.
    /// </summary>
    private void SyncMathRevealRedraw()
    {
        MarkdownSemanticSpan? next = null;
        if (FullRevealEnabled && TryGetMathSpanAtOffset(CaretReveal.CaretOffset, out var found))
        {
            next = found;
        }

        if (SameMathRange(_lastRevealedMathSpan, next))
        {
            return;
        }

        var previous = _lastRevealedMathSpan;
        _lastRevealedMathSpan = next;
        if (previous.HasValue || next.HasValue)
        {
            // A block formula may own several DocumentLines. Full invalidation is rare (only at
            // formula entry/exit) and avoids leaving a stale continuation visual line behind.
            ScheduleRedraw();
        }
    }

    private bool IsMathSpanRevealed(MarkdownSemanticSpan span)
    {
        var revealed = FullRevealEnabled &&
            MarkdownSemanticReveal.RevealRange(CaretReveal, span.Start, span.End);
        if (revealed)
        {
            // Set during layout as well, so a render-mode switch with an unmoved caret still leaves
            // enough state for the next caret move to restore the formula element.
            _lastRevealedMathSpan = span;
        }

        return revealed;
    }

    private static bool SameMathRange(
        MarkdownSemanticSpan? left,
        MarkdownSemanticSpan? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue ||
         (left.Value.Start == right!.Value.Start &&
          left.Value.End == right.Value.End &&
          left.Value.Kind == right.Value.Kind));

    private bool TryGetMathSpanAtOffset(int offset, out MarkdownSemanticSpan span)
    {
        span = default;
        var document = _editor.Document;
        if (document == null || !TryCurrentSnapshot(out var snapshot))
        {
            return false;
        }

        var normalized = Math.Clamp(offset, 0, document.TextLength);
        if (TryGetMathSpanOnLine(snapshot, document.GetLineByOffset(normalized), normalized, out span))
        {
            return true;
        }

        return normalized > 0 &&
            TryGetMathSpanOnLine(
                snapshot,
                document.GetLineByOffset(normalized - 1),
                normalized,
                out span);
    }

    private static bool TryGetMathSpanOnLine(
        MarkdownSemanticSnapshot snapshot,
        DocumentLine line,
        int offset,
        out MarkdownSemanticSpan span)
    {
        foreach (var candidate in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
        {
            if (IsMathSpan(candidate) &&
                offset >= candidate.Start &&
                offset <= candidate.End)
            {
                span = candidate;
                return true;
            }
        }

        span = default;
        return false;
    }

    private static bool IsMathSpan(MarkdownSemanticSpan span) =>
        span.Kind is MarkdownSemanticSpanKind.InlineMath or
            MarkdownSemanticSpanKind.BlockMath;

    private bool TryCreateMathElement(
        MarkdownSemanticSpan span,
        out UIElement element)
    {
        element = null!;
        var source = _editor.Text ?? string.Empty;
        var display = span.Kind == MarkdownSemanticSpanKind.BlockMath;
        if (!MarkdownMathSource.TryExtract(source, span, out var formula, out display) ||
            !MarkdownMathRenderer.TryRender(
                formula,
                display,
                _editor.FontSize,
                Theme.TextBrush,
                _editor.FontFamily?.Source,
                out var drawing))
        {
            return false;
        }

        var textView = _editor.TextArea.TextView;
        var viewWidth = textView.ActualWidth;
        if (!double.IsFinite(viewWidth) || viewWidth <= 0)
        {
            viewWidth = _editor.ActualWidth;
        }
        if (!double.IsFinite(viewWidth) || viewWidth <= 0)
        {
            viewWidth = 600;
        }

        var availableWidth = Math.Max(48, viewWidth - 8);
        var scale = drawing.Width <= availableWidth
            ? 1.0
            : availableWidth / drawing.Width;
        if (!double.IsFinite(scale) || scale < 0.2)
        {
            // An unreadably small formula is less useful than its exact editable source.
            return false;
        }

        var visual = new MarkdownMathVisual(drawing, scale);
        if (!display || !IsDisplayFormulaOnOwnLine(source, span))
        {
            element = visual;
            return true;
        }

        var height = drawing.Height * scale + 8;
        var host = new Grid
        {
            Width = availableWidth,
            Height = height,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Focusable = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        visual.HorizontalAlignment = HorizontalAlignment.Center;
        visual.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(visual);
        TextBlock.SetBaselineOffset(host, height);
        element = host;
        return true;
    }

    private static bool IsDisplayFormulaOnOwnLine(
        string source,
        MarkdownSemanticSpan span)
    {
        if (span.Kind != MarkdownSemanticSpanKind.BlockMath ||
            span.Start < 0 ||
            span.End > source.Length)
        {
            return false;
        }

        var lineStart = span.Start;
        while (lineStart > 0 && source[lineStart - 1] is not ('\r' or '\n'))
        {
            lineStart--;
        }

        var lineEnd = span.End;
        while (lineEnd < source.Length && source[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }

        return IsWhitespace(source.AsSpan(lineStart, span.Start - lineStart)) &&
            IsWhitespace(source.AsSpan(span.End, lineEnd - span.End));
    }

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class MathElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownSemanticPresentation _owner;
        private PreparedMathElement? _prepared;

        public MathElementGenerator(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _prepared = null;
        }

        public override void FinishGeneration()
        {
            _prepared = null;
            base.FinishGeneration();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            _prepared = null;
            if (!_owner.RenderMath || !_owner.TryCurrentSnapshot(out var snapshot))
            {
                return -1;
            }

            var line = CurrentContext.VisualLine.FirstDocumentLine;
            var lineZero = Math.Max(0, line.LineNumber - 1);
            foreach (var span in snapshot.SpansForLine(lineZero))
            {
                if (!IsMathSpan(span) || span.End <= startOffset)
                {
                    continue;
                }

                int elementOffset;
                if (span.Start >= startOffset)
                {
                    elementOffset = span.Start;
                }
                else if (span.Kind == MarkdownSemanticSpanKind.BlockMath &&
                         span.Start < line.Offset &&
                         span.End > line.Offset)
                {
                    // TextView may start constructing at a physical line in the middle of a folded
                    // multi-line formula. Render the complete formula there and consume the
                    // remaining source; when its real opening line is present, the normal start
                    // path owns the whole range and this fallback is never reached.
                    elementOffset = startOffset;
                }
                else
                {
                    continue;
                }

                if (_owner.IsMathSpanRevealed(span) ||
                    !_owner.TryCreateMathElement(span, out var element))
                {
                    continue;
                }

                var documentLength = span.End - elementOffset;
                if (documentLength <= 0)
                {
                    continue;
                }

                _prepared = new PreparedMathElement(
                    elementOffset,
                    documentLength,
                    element);
                return elementOffset;
            }

            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var prepared = _prepared;
            _prepared = null;
            return prepared != null && prepared.Offset == offset
                ? new InlineObjectElement(prepared.DocumentLength, prepared.Element)
                : null!;
        }

        private sealed record PreparedMathElement(
            int Offset,
            int DocumentLength,
            UIElement Element);
    }
}
