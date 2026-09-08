using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private readonly Dictionary<MathCollapseKey, CollapsedLineSection> _mathCollapsedSections = new();
    private MathElementGenerator? _mathElementGenerator;
    private MarkdownSemanticSpan? _lastRevealedMathSpan;
    private bool _syncingMathCollapsedLines;

    private readonly record struct MathCollapseKey(int Start, int End);

    private readonly record struct MathCollapseTarget(
        DocumentLine StartLine,
        DocumentLine EndLine);

    private bool RenderMath =>
        ApplyMarkdownStyle && (_editor.IsPreviewMode || IsFullMode);

    private void AttachMathPresentation()
    {
        _mathElementGenerator = new MathElementGenerator(this);
        _editor.MarkdownPresentationRefreshing += OnMarkdownPresentationRefreshing;
        _editor.SizeChanged += OnMathHostSizeChanged;

        // Formula replacement must win when it starts at the same source offset as a normal
        // syntax-collapse cell. The redraw raised by Insert is deferred, so continuation lines can
        // be registered in the height tree immediately afterwards, before visual-line creation.
        _editor.TextArea.TextView.ElementGenerators.Insert(0, _mathElementGenerator);
        SyncMathCollapsedLines();
    }

    private void DetachMathPresentation()
    {
        _editor.MarkdownPresentationRefreshing -= OnMarkdownPresentationRefreshing;
        _editor.SizeChanged -= OnMathHostSizeChanged;
        ClearMathCollapsedLines();

        if (_mathElementGenerator != null)
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_mathElementGenerator);
            _mathElementGenerator = null;
        }

        _lastRevealedMathSpan = null;
    }

    private void OnMarkdownPresentationRefreshing()
    {
        if (!_disposed)
        {
            SyncMathCollapsedLines();
        }
    }

    private void OnMathHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5)
        {
            return;
        }

        // A formula that was too wide to remain legible may become renderable after widening, or
        // must fall back to source after narrowing below the minimum scale.
        SyncMathCollapsedLines();
        ScheduleRedraw();
    }

    private void ResetMathPresentationState()
    {
        _lastRevealedMathSpan = null;
        SyncMathCollapsedLines();
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
            // Uncollapse before rebuilding the source view; collapse before rebuilding the formula
            // view. This keeps AvalonEdit's height tree and element generator in agreement.
            SyncMathCollapsedLines();
            ScheduleRedraw();
        }
    }

    private bool IsMathSpanRevealed(MarkdownSemanticSpan span)
    {
        var revealed = IsMathSpanCurrentlyRevealed(span);
        if (revealed)
        {
            // Set during layout as well, so a render-mode switch with an unmoved caret still leaves
            // enough state for the next caret move to restore the formula element.
            _lastRevealedMathSpan = span;
        }

        return revealed;
    }

    private bool IsMathSpanCurrentlyRevealed(MarkdownSemanticSpan span) =>
        FullRevealEnabled &&
        MarkdownSemanticReveal.RevealRange(CaretReveal, span.Start, span.End);

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

    /// <summary>
    /// A visual element may consume newline characters only when the physical continuation lines
    /// are registered as collapsed in AvalonEdit's height tree. The opening line remains visible and
    /// owns the formula object; every later source line through the closing delimiter has zero
    /// document height until the formula is revealed for editing.
    /// </summary>
    private void SyncMathCollapsedLines()
    {
        if (_disposed || _syncingMathCollapsedLines)
        {
            return;
        }

        _syncingMathCollapsedLines = true;
        try
        {
            var desired = BuildMathCollapseTargets();
            foreach (var existing in _mathCollapsedSections.ToArray())
            {
                if (desired.TryGetValue(existing.Key, out var target) &&
                    existing.Value.IsCollapsed &&
                    ReferenceEquals(existing.Value.Start, target.StartLine) &&
                    ReferenceEquals(existing.Value.End, target.EndLine))
                {
                    desired.Remove(existing.Key);
                    continue;
                }

                existing.Value.Uncollapse();
                _mathCollapsedSections.Remove(existing.Key);
            }

            var textView = _editor.TextArea.TextView;
            foreach (var target in desired)
            {
                try
                {
                    _mathCollapsedSections[target.Key] = textView.CollapseLines(
                        target.Value.StartLine,
                        target.Value.EndLine);
                }
                catch (ArgumentException)
                {
                    // A concurrent document replacement/deletion can invalidate a line object. The
                    // generator checks the section table and leaves exact source visible instead.
                }
                catch (InvalidOperationException)
                {
                    // A detached/disposed TextView likewise falls back to source without affecting
                    // note data or the undo stack.
                }
            }
        }
        finally
        {
            _syncingMathCollapsedLines = false;
        }
    }

    private Dictionary<MathCollapseKey, MathCollapseTarget> BuildMathCollapseTargets()
    {
        var targets = new Dictionary<MathCollapseKey, MathCollapseTarget>();
        var document = _editor.Document;
        if (!RenderMath ||
            document == null ||
            document.TextLength == 0 ||
            !TryCurrentSnapshot(out var snapshot))
        {
            return targets;
        }

        foreach (var span in snapshot.Spans)
        {
            if (span.Kind != MarkdownSemanticSpanKind.BlockMath ||
                span.Length <= 0 ||
                span.Start < 0 ||
                span.End > document.TextLength ||
                IsMathSpanCurrentlyRevealed(span))
            {
                continue;
            }

            var firstLine = document.GetLineByOffset(span.Start);
            var lastLine = document.GetLineByOffset(Math.Max(span.Start, span.End - 1));
            if (lastLine.LineNumber <= firstLine.LineNumber || firstLine.NextLine == null)
            {
                continue;
            }

            // Do not hide continuation lines until WpfMath has produced a valid replacement at the
            // current font, theme and available width. Unsupported/invalid TeX remains exact source.
            if (!TryCreateMathElement(span, out _))
            {
                continue;
            }

            targets[new MathCollapseKey(span.Start, span.End)] = new MathCollapseTarget(
                firstLine.NextLine,
                lastLine);
        }

        return targets;
    }

    private bool IsMathSpanReadyForLayout(MarkdownSemanticSpan span)
    {
        if (span.Kind != MarkdownSemanticSpanKind.BlockMath ||
            !SpansMultipleDocumentLines(span))
        {
            return true;
        }

        var key = new MathCollapseKey(span.Start, span.End);
        return _mathCollapsedSections.TryGetValue(key, out var section) &&
            section.IsCollapsed;
    }

    private bool SpansMultipleDocumentLines(MarkdownSemanticSpan span)
    {
        var document = _editor.Document;
        return document != null &&
            span.Start >= 0 &&
            span.End <= document.TextLength &&
            document.GetLineByOffset(span.Start).LineNumber !=
            document.GetLineByOffset(Math.Max(span.Start, span.End - 1)).LineNumber;
    }

    private void ClearMathCollapsedLines()
    {
        foreach (var section in _mathCollapsedSections.Values)
        {
            section.Uncollapse();
        }

        _mathCollapsedSections.Clear();
    }

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
                if (!IsMathSpan(span) ||
                    span.Start < startOffset ||
                    span.End <= span.Start ||
                    _owner.IsMathSpanRevealed(span) ||
                    !_owner.IsMathSpanReadyForLayout(span) ||
                    !_owner.TryCreateMathElement(span, out var element))
                {
                    continue;
                }

                _prepared = new PreparedMathElement(
                    span.Start,
                    span.Length,
                    element);
                return span.Start;
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
