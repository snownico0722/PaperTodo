using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private MathElementGenerator? _mathElementGenerator;

    private bool RenderMath =>
        ApplyMarkdownStyle && (_editor.IsPreviewMode || IsFullMode);

    private void AttachMathPresentation()
    {
        _mathElementGenerator = new MathElementGenerator(this);
        _editor.TextArea.TextView.ElementGenerators.Add(_mathElementGenerator);
    }

    private void DetachMathPresentation()
    {
        if (_mathElementGenerator == null)
        {
            return;
        }

        _editor.TextArea.TextView.ElementGenerators.Remove(_mathElementGenerator);
        _mathElementGenerator = null;
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
        private bool _hasPrepared;
        private int _preparedOffset;
        private MarkdownSemanticSpan _preparedSpan;
        private MarkdownMathBitmap _preparedBitmap;

        public MathElementGenerator(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            ResetPrepared();
        }

        public override void FinishGeneration()
        {
            ResetPrepared();
            base.FinishGeneration();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            ResetPrepared();
            if (!_owner.RenderMath ||
                !_owner.TryCurrentSnapshot(out var snapshot))
            {
                return -1;
            }

            var document = CurrentContext.Document;
            var visualLine = CurrentContext.VisualLine;
            var physicalLine = visualLine.FirstDocumentLine;
            if (document == null || physicalLine == null)
            {
                return -1;
            }

            var source = _owner._editor.Text ?? string.Empty;
            var lineIndex = Math.Max(0, physicalLine.LineNumber - 1);

            // When the viewport starts inside a display formula whose first row is above it,
            // construct the same folded visual object from the current row and consume only the
            // remaining source. Normal top-down layout reaches the formula at span.Start instead.
            foreach (var span in snapshot.SpansForLine(lineIndex))
            {
                if (span.Kind == MarkdownSemanticSpanKind.BlockMath &&
                    span.Start < startOffset &&
                    span.End > startOffset &&
                    TryPrepare(startOffset, span, source))
                {
                    return startOffset;
                }
            }

            var spans = snapshot.Spans;
            var index = LowerBoundSpanStart(spans, startOffset);
            for (; index < spans.Count; index++)
            {
                var span = spans[index];
                if (span.Start > physicalLine.EndOffset)
                {
                    break;
                }
                if (span.Kind is not (
                        MarkdownSemanticSpanKind.InlineMath or
                        MarkdownSemanticSpanKind.BlockMath))
                {
                    continue;
                }
                if (TryPrepare(span.Start, span, source))
                {
                    return span.Start;
                }
            }

            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            if (!_hasPrepared || offset != _preparedOffset)
            {
                return null!;
            }

            var span = _preparedSpan;
            var rendered = _preparedBitmap;
            var documentLength = Math.Max(0, span.End - offset);
            var element = _owner.CreateMathElement(
                rendered,
                span.Kind == MarkdownSemanticSpanKind.BlockMath);
            ResetPrepared();
            return new InlineObjectElement(documentLength, element);
        }

        private bool TryPrepare(
            int offset,
            MarkdownSemanticSpan span,
            string source)
        {
            if (span.Length <= 0 ||
                span.Start < 0 ||
                span.End > source.Length ||
                (_owner.IsFullMode && _owner.IsRangeRevealed(span.Start, span.End)) ||
                !MarkdownMathSource.TryExtract(source, span, out var formula, out var display))
            {
                return false;
            }

            var fontSize = Math.Clamp(
                _owner._editor.FontSize * (display ? 1.05 : 1.0),
                4,
                256);
            if (!MarkdownMathRenderer.TryRender(
                    formula,
                    display,
                    fontSize,
                    _owner.MathColor(),
                    out var rendered))
            {
                return false;
            }

            _preparedOffset = offset;
            _preparedSpan = span;
            _preparedBitmap = rendered;
            _hasPrepared = true;
            return true;
        }

        private void ResetPrepared()
        {
            _hasPrepared = false;
            _preparedOffset = -1;
            _preparedSpan = default;
            _preparedBitmap = default;
        }

        private static int LowerBoundSpanStart(
            IReadOnlyList<MarkdownSemanticSpan> spans,
            int startOffset)
        {
            var low = 0;
            var high = spans.Count;
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                if (spans[middle].Start < startOffset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }
}
