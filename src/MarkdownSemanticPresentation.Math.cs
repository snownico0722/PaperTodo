using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _mathCollapseSyncQueued;

    private readonly record struct MathCollapseKey(int Start, int End);

    private readonly record struct MathCollapseTarget(
        DocumentLine StartLine,
        DocumentLine EndLine);

    private bool RenderMath =>
        ApplyMarkdownStyle && (_editor.IsPreviewMode || IsFullMode);

    private void AttachMathPresentation()
    {
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
        ClearMathCollapsedLines();

        if (_mathElementGenerator != null)
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_mathElementGenerator);
            _mathElementGenerator = null;
        }

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
        if (_disposed || _syncingMathCollapsedLines)
        {
            return false;
        }

        _syncingMathCollapsedLines = true;
        var changed = false;
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
                changed = true;
            }

            var textView = _editor.TextArea.TextView;
            foreach (var target in desired)
            {
                try
                {
                    _mathCollapsedSections[target.Key] = textView.CollapseLines(
                        target.Value.StartLine,
                        target.Value.EndLine);
                    changed = true;
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

            // CollapseLines hides complete physical lines. If the closing delimiter shares its
            // line with real text after the formula, rendering the formula would hide that text.
            // Keep exact Markdown source in that uncommon shape instead.
            if (!HasOnlyWhitespaceAfterSpanOnLastLine(document, span, lastLine))
            {
                continue;
            }

            // Do not hide continuation lines until RaTeX has produced a valid replacement at the
            // current font/theme. Unsupported or invalid TeX stays exact source.
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

    private static bool HasOnlyWhitespaceAfterSpanOnLastLine(
        ICSharpCode.AvalonEdit.Document.TextDocument document,
        MarkdownSemanticSpan span,
        DocumentLine lastLine)
    {
        if (span.End >= lastLine.EndOffset)
        {
            return true;
        }

        var suffix = document.GetText(span.End, lastLine.EndOffset - span.End);
        return string.IsNullOrWhiteSpace(suffix);
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
        if (!MarkdownMathSource.TryExtract(source, span, out var formula, out var display))
        {
            return false;
        }

        var fontSize = Math.Clamp(
            _editor.FontSize * (display ? 1.05 : 1.0),
            4,
            256);
        if (!MarkdownMathRenderer.TryRender(
                formula,
                display,
                fontSize,
                MathColor(),
                out var rendered))
        {
            return false;
        }

        element = CreateMathElement(rendered, display);
        return true;
    }

    private FrameworkElement CreateMathElement(
        MarkdownMathBitmap rendered,
        bool display)
    {
        var textView = _editor.TextArea.TextView;
        var zoom = ZoomFactor();
        var availableWidth = Math.Max(32, textView.ActualWidth - 16 * zoom);
        var scale = Math.Min(1.0, availableWidth / Math.Max(1, rendered.Width));
        var width = Math.Max(1, rendered.Width * scale);
        var height = Math.Max(1, rendered.Height * scale);
        var image = new Image
        {
            Source = rendered.Source,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        if (!display)
        {
            TextBlock.SetBaselineOffset(
                image,
                Math.Clamp(rendered.Baseline * scale, 0, height));
            return image;
        }

        var verticalPadding = 4 * zoom;
        var host = new Grid
        {
            Width = availableWidth,
            Height = height + verticalPadding * 2,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(image);
        TextBlock.SetBaselineOffset(host, host.Height);
        return host;
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
