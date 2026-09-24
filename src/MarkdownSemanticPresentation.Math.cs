using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private readonly Dictionary<MathCollapseKey, FoldingSection> _mathFoldings = new();
    private FoldingManager? _mathFoldingManager;
    private FoldingMargin? _mathFoldingMargin;
    private MathElementGenerator? _mathElementGenerator;
    private MarkdownSemanticSpan? _lastRevealedMathSpan;
    private bool _syncingMathCollapsedLines;
    private bool _mathCollapseSyncQueued;

    private readonly record struct MathCollapseKey(int Start, int End);

    private readonly record struct MathCollapseTarget(
        int Start,
        int End,
        bool ShouldFold);

    private bool RenderMath =>
        ApplyMarkdownStyle && (_editor.IsPreviewMode || IsFullMode);

    private void AttachMathPresentation()
    {
        // Let AvalonEdit own physical-line folding and height-tree bookkeeping. Formula folding is
        // presentation-only, so hide the gutter and put PaperTodo's replacement generator first.
        _mathFoldingManager = FoldingManager.Install(_editor.TextArea);
        _mathFoldingMargin = _editor.TextArea.LeftMargins
            .OfType<FoldingMargin>()
            .FirstOrDefault(margin => ReferenceEquals(margin.FoldingManager, _mathFoldingManager));
        if (_mathFoldingMargin != null)
        {
            _editor.TextArea.LeftMargins.Remove(_mathFoldingMargin);
        }

        _mathElementGenerator = new MathElementGenerator(this);
        _semanticDocument.SnapshotChanged += OnMathSnapshotChanged;
        _editor.TextArea.Caret.PositionChanged += OnMathCaretPositionChanged;
        _editor.GotKeyboardFocus += OnMathEditorGotFocus;
        _editor.CaretRevealGestureEnded += OnMathCaretRevealGestureEnded;
        _editor.SizeChanged += OnMathHostSizeChanged;
        _editor.TextArea.TextView.VisualLinesChanged += OnMathVisualLinesChanged;

        // Math replacement must win when it starts at the same source offset as a normal syntax
        // collapse cell. Multi-line formulas are allowed to consume newlines only after their
        // continuation lines have been registered in AvalonEdit's height tree below.
        _editor.TextArea.TextView.ElementGenerators.Insert(0, _mathElementGenerator);
        SyncMathCollapsedLines();
    }

    private void DetachMathPresentation()
    {
        _semanticDocument.SnapshotChanged -= OnMathSnapshotChanged;
        _editor.TextArea.Caret.PositionChanged -= OnMathCaretPositionChanged;
        _editor.GotKeyboardFocus -= OnMathEditorGotFocus;
        _editor.CaretRevealGestureEnded -= OnMathCaretRevealGestureEnded;
        _editor.SizeChanged -= OnMathHostSizeChanged;
        _editor.TextArea.TextView.VisualLinesChanged -= OnMathVisualLinesChanged;
        if (_mathElementGenerator != null)
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_mathElementGenerator);
            _mathElementGenerator = null;
        }

        ClearMathCollapsedLines();
        if (_mathFoldingManager != null)
        {
            FoldingManager.Uninstall(_mathFoldingManager);
            _mathFoldingManager = null;
        }
        _mathFoldingMargin = null;

        _lastRevealedMathSpan = null;
        _mathCollapseSyncQueued = false;
    }

    private void OnMathSnapshotChanged(MarkdownSourceChange? change)
    {
        if (_disposed)
        {
            return;
        }

        _lastRevealedMathSpan = null;
        SyncMathCollapsedLines();
    }

    private void OnMathCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_disposed || _revealGestureFrozen)
        {
            return;
        }

        // MarkdownSemanticPresentation subscribed its normal caret handler before this one, so
        // CaretReveal already contains the new offset when this handler runs.
        SyncMathRevealRedraw();
    }

    private void OnMathEditorGotFocus(object? sender, KeyboardFocusChangedEventArgs e)
    {
        if (_disposed || _revealGestureFrozen)
        {
            return;
        }

        SyncMathRevealRedraw();
    }

    private void OnMathCaretRevealGestureEnded()
    {
        if (!_disposed)
        {
            SyncMathRevealRedraw();
        }
    }

    private void OnMathHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5)
        {
            return;
        }

        if (SyncMathCollapsedLines())
        {
            ScheduleRedraw();
        }
        else
        {
            // The bitmap is cached independently of the viewport, but its WPF Image element is
            // rescaled to the current available width.
            ScheduleRedraw();
        }
    }

    private void OnMathVisualLinesChanged(object? sender, EventArgs e)
    {
        // Render mode / preview changes synchronously rebuild the TextView and do not expose a
        // dedicated event to this presentation layer. Queue a post-layout reconciliation. The
        // generator itself refuses unsafe cross-line replacement until this reconciliation wins.
        QueueMathCollapseSync();
    }

    private void QueueMathCollapseSync()
    {
        if (_disposed || _mathCollapseSyncQueued)
        {
            return;
        }

        _mathCollapseSyncQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _mathCollapseSyncQueued = false;
                if (!_disposed && SyncMathCollapsedLines())
                {
                    ScheduleRedraw();
                }
            }),
            System.Windows.Threading.DispatcherPriority.Render);
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

        _lastRevealedMathSpan = next;
        SyncMathCollapsedLines();
        ScheduleRedraw();
    }

    private bool IsMathSpanRevealed(MarkdownSemanticSpan span)
    {
        var revealed = IsMathSpanCurrentlyRevealed(span);
        if (revealed)
        {
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
    /// A VisualLineElement may consume newline characters only when every physical continuation
    /// line is registered as collapsed in AvalonEdit's height tree. Without this, AvalonEdit throws
    /// "Line N was skipped by a VisualLineElementGenerator, but it is not collapsed" while laying
    /// out the note. The opening line remains visible and owns the formula object.
    /// </summary>
    private bool SyncMathCollapsedLines()
    {
        if (_disposed || _syncingMathCollapsedLines || _mathFoldingManager == null)
        {
            return false;
        }

        _syncingMathCollapsedLines = true;
        var changed = false;
        try
        {
            var desired = BuildMathCollapseTargets();
            foreach (var existing in _mathFoldings.ToArray())
            {
                if (desired.TryGetValue(existing.Key, out var target) &&
                    existing.Value.StartOffset == target.Start &&
                    existing.Value.EndOffset == target.End)
                {
                    if (existing.Value.IsFolded != target.ShouldFold)
                    {
                        existing.Value.IsFolded = target.ShouldFold;
                        changed = true;
                    }

                    desired.Remove(existing.Key);
                    continue;
                }

                _mathFoldingManager.RemoveFolding(existing.Value);
                _mathFoldings.Remove(existing.Key);
                changed = true;
            }

            foreach (var target in desired)
            {
                try
                {
                    var section = _mathFoldingManager.CreateFolding(
                        target.Value.Start,
                        target.Value.End);
                    section.Title = string.Empty;
                    section.IsFolded = target.Value.ShouldFold;
                    _mathFoldings[target.Key] = section;
                    changed = true;
                }
                catch (ArgumentException)
                {
                    // A document replacement can invalidate a just-computed semantic range. Exact
                    // Markdown remains visible and the next snapshot refresh rebuilds the fold.
                }
                catch (InvalidOperationException)
                {
                    // Detached/disposed editor: fail open to source rather than touching note data.
                }
            }
        }
        finally
        {
            _syncingMathCollapsedLines = false;
        }

        return changed;
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
                !SpansMultipleDocumentLines(span))
            {
                continue;
            }

            // Keep one stable FoldingSection while the caret reveals source. Toggling IsFolded lets
            // AvalonEdit rebase offsets and height-tree state without destroy/recreate churn.
            if (!TryCreateMathElement(span, out _))
            {
                continue;
            }

            targets[new MathCollapseKey(span.Start, span.End)] = new MathCollapseTarget(
                span.Start,
                span.End,
                !IsMathSpanCurrentlyRevealed(span));
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
        return _mathFoldings.TryGetValue(key, out var section) && section.IsFolded;
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
        if (_mathFoldingManager != null)
        {
            foreach (var section in _mathFoldings.Values.ToArray())
            {
                _mathFoldingManager.RemoveFolding(section);
            }
        }

        _mathFoldings.Clear();
    }

    private bool TryCreateMathElement(
        MarkdownSemanticSpan span,
        out UIElement element)
    {
        element = null!;
        var source = _editor.Text ?? string.Empty;
        if (!MarkdownMathSource.TryExtract(source, span, out var formula, out var display))
        {
            return false;
        }

        var fontSize = Math.Clamp(
            _editor.FontSize * (display ? 1.05 : 1.0),
            4,
            256);
        var pixelsPerDip = VisualTreeHelper.GetDpi(_editor).PixelsPerDip;
        if (!MarkdownMathRenderer.TryRender(
                formula,
                display,
                fontSize,
                MathColor(),
                pixelsPerDip,
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

    private Color MathColor()
    {
        return Theme.TextBrush is SolidColorBrush solid
            ? solid.Color
            : Theme.IsDark ? Colors.White : Colors.Black;
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
                    _owner.IsMathSpanRevealed(span))
                {
                    continue;
                }

                if (!_owner.IsMathSpanReadyForLayout(span))
                {
                    // Safety invariant: never return a cross-line replacement until the continuation
                    // lines are collapsed. Returning source for one frame is harmless; skipping an
                    // uncollapsed line crashes AvalonEdit.
                    _owner.QueueMathCollapseSync();
                    continue;
                }

                if (!_owner.TryCreateMathElement(span, out var element))
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
