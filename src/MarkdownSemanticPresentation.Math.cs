using System.Windows;
using System.Windows.Controls;
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
        // Use AvalonEdit's own folding subsystem for physical continuation lines instead of
        // manipulating TextView.CollapseLines directly. FoldingManager owns height-tree updates,
        // visual-line invalidation and document-offset rebasing as one coherent transaction.
        _mathFoldingManager = FoldingManager.Install(_editor.TextArea);
        _mathFoldingMargin = _editor.TextArea.LeftMargins
            .OfType<FoldingMargin>()
            .FirstOrDefault(margin => ReferenceEquals(margin.FoldingManager, _mathFoldingManager));
        if (_mathFoldingMargin != null)
        {
            // Formula folding is presentation-only; PaperTodo must not expose a code-editor folding
            // gutter or let the user toggle the internal continuation-line folds manually.
            _editor.TextArea.LeftMargins.Remove(_mathFoldingMargin);
        }

        _mathElementGenerator = new MathElementGenerator(this);
        _editor.MarkdownPresentationRefreshing += OnMarkdownPresentationRefreshing;
        _editor.SizeChanged += OnMathHostSizeChanged;

        // FoldingManager installs its own marker generator at index 0. Formula replacement must win
        // at the same source offset, while the stock folding generator remains as a safety net for
        // any folded span our generator cannot construct in a transient layout frame.
        _editor.TextArea.TextView.ElementGenerators.Insert(0, _mathElementGenerator);
        SyncMathCollapsedLines();
    }

    private void DetachMathPresentation()
    {
        _editor.MarkdownPresentationRefreshing -= OnMarkdownPresentationRefreshing;
        _editor.SizeChanged -= OnMathHostSizeChanged;
        _mathCollapseSyncQueued = false;

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

        // SizeChanged can run while WPF is still arranging the editor, before TextView has its final
        // width. Coalesce resizes and re-evaluate long-formula readability after that layout settles.
        QueueMathCollapseSyncAfterLayout();
    }

    private void QueueMathCollapseSyncAfterLayout()
    {
        if (_disposed || _mathCollapseSyncQueued)
        {
            return;
        }

        _mathCollapseSyncQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!_mathCollapseSyncQueued)
                {
                    return;
                }

                _mathCollapseSyncQueued = false;
                if (_disposed || _mathElementGenerator == null || _mathFoldingManager == null)
                {
                    return;
                }

                SyncMathCollapsedLines();
                ScheduleRedraw();
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
            SyncMathCollapsedLines();
            ScheduleRedraw();
        }
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
    /// Cross-line VisualLineElements are only legal when their continuation lines are folded.
    /// Let FoldingManager own that invariant instead of mutating TextView's HeightTree directly.
    /// </summary>
    private void SyncMathCollapsedLines()
    {
        if (_disposed || _syncingMathCollapsedLines || _mathFoldingManager == null)
        {
            return;
        }

        _syncingMathCollapsedLines = true;
        try
        {
            var desired = BuildMathCollapseTargets();
            foreach (var existing in _mathFoldings.ToArray())
            {
                if (desired.TryGetValue(existing.Key, out var target) &&
                    existing.Value.StartOffset == target.Start &&
                    existing.Value.EndOffset == target.End)
                {
                    existing.Value.IsFolded = target.ShouldFold;
                    desired.Remove(existing.Key);
                    continue;
                }

                _mathFoldingManager.RemoveFolding(existing.Value);
                _mathFoldings.Remove(existing.Key);
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
                }
                catch (ArgumentException)
                {
                    // A document replacement can invalidate a just-computed semantic range. The next
                    // semantic refresh rebuilds it; exact Markdown source remains untouched meanwhile.
                }
                catch (InvalidOperationException)
                {
                    // Detached/disposed editor: fail open to source rather than affecting note data.
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
                !SpansMultipleDocumentLines(span))
            {
                continue;
            }

            // Keep a valid section while the caret reveals its source, but leave it unfolded. That
            // avoids destroying/recreating folding objects on every entry/exit while still exposing
            // the complete Markdown range for editing.
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
