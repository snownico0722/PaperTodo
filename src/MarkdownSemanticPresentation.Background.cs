using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed class SemanticBackgroundRenderer : IBackgroundRenderer
    {
        private readonly MarkdownSemanticPresentation _owner;

        public SemanticBackgroundRenderer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (!_owner.RenderBlocks || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var snapshot = _owner.CurrentSnapshot();
            var zoom = _owner.ZoomFactor();
            var quotePen = new Pen(Theme.QuoteBorderBrush, 3 * zoom);
            var inlineCodeBuilder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                CornerRadius = 3 * zoom,
                BorderThickness = 0
            };

            var visible = new List<DocumentLine>();
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    if (visible.Count > 0 && ReferenceEquals(visible[^1], line))
                    {
                        continue;
                    }

                    visible.Add(line);
                }
            }

            foreach (var line in visible)
            {
                var lineIndex = LineIndex(line);
                foreach (var span in snapshot.SpansForLine(lineIndex))
                {
                    if (span.Kind == MarkdownSemanticSpanKind.InlineCode && span.Length > 0)
                    {
                        inlineCodeBuilder.AddSegment(
                            textView,
                            new TextSegment
                            {
                                StartOffset = span.Start,
                                Length = span.Length
                            });
                    }
                }
            }

            DrawCodeRuns(textView, drawingContext, snapshot, visible, zoom);
            DrawQuoteRails(textView, drawingContext, document, snapshot, visible, quotePen, zoom);

            var inlineCodeGeometry = inlineCodeBuilder.CreateGeometry();
            if (inlineCodeGeometry != null)
            {
                drawingContext.DrawGeometry(Theme.CodeBrush, null, inlineCodeGeometry);
            }
        }

        private bool IsCodeRow(MarkdownSemanticSnapshot snapshot, DocumentLine line)
        {
            var semantic = snapshot.GetLine(LineIndex(line));
            if (!semantic.IsCode)
            {
                return false;
            }

            if (semantic.IsFencedCodeMarker && !_owner.IsFullMode)
            {
                return false;
            }

            return true;
        }

        private void DrawCodeRuns(
            TextView textView,
            DrawingContext drawingContext,
            MarkdownSemanticSnapshot snapshot,
            List<DocumentLine> visible,
            double zoom)
        {
            var count = visible.Count;
            var index = 0;
            while (index < count)
            {
                if (!IsCodeRow(snapshot, visible[index]))
                {
                    index++;
                    continue;
                }

                var first = visible[index];
                var last = first;
                while (index + 1 < count && IsCodeRow(snapshot, visible[index + 1]))
                {
                    index++;
                    last = visible[index];
                }

                var top = RowTop(textView, first);
                var bottom = RowBottom(textView, last);
                var height = Math.Max(1, bottom - top);
                var cornerRadius = 4 * zoom;
                drawingContext.DrawRoundedRectangle(
                    Theme.CodeBrush,
                    null,
                    new Rect(
                        0,
                        top + 1,
                        Math.Max(0, textView.ActualWidth - 4),
                        Math.Max(1, height - 2)),
                    cornerRadius,
                    cornerRadius);
                index++;
            }
        }

        /// <summary>
        /// Full 的固定槽位直接提供引用坐标；有序列表和其他档位保留原有逻辑前缀对齐。
        /// 虚拟引用从实际占位元素读取位置和宽度，不再重新测量一个透明的 `> ` 字符串。
        /// </summary>
        private void DrawQuoteRails(
            TextView textView,
            DrawingContext drawingContext,
            IDocument document,
            MarkdownSemanticSnapshot snapshot,
            List<DocumentLine> visible,
            Pen quotePen,
            double zoom)
        {
            var railRows = new List<double[]>(visible.Count);
            var virtualUnitWidth = MeasureVirtualQuoteUnit(textView);
            foreach (var line in visible)
            {
                railRows.Add(GetQuoteRailXs(
                    textView,
                    document,
                    snapshot,
                    line,
                    zoom,
                    virtualUnitWidth));
            }

            for (var row = 0; row < visible.Count; row++)
            {
                var rails = railRows[row];
                if (rails.Length == 0)
                {
                    continue;
                }

                var line = visible[row];
                var previousRails = row > 0 && visible[row - 1].NextLine == line
                    ? railRows[row - 1]
                    : Array.Empty<double>();
                var nextRails = row + 1 < visible.Count && line.NextLine == visible[row + 1]
                    ? railRows[row + 1]
                    : Array.Empty<double>();
                var top = RowTop(textView, line);
                var bottom = RowBottom(textView, line);
                foreach (var x in rails)
                {
                    var joinsPrevious = ContainsNear(previousRails, x);
                    var joinsNext = ContainsNear(nextRails, x);
                    var startY = top + (joinsPrevious ? -0.5 : 1);
                    var endY = bottom + (joinsNext ? 0.5 : -1);
                    if (endY < startY)
                    {
                        endY = startY;
                    }

                    drawingContext.DrawLine(
                        quotePen,
                        new Point(x, startY),
                        new Point(x, endY));
                }
            }
        }

        private double[] GetQuoteRailXs(
            TextView textView,
            IDocument document,
            MarkdownSemanticSnapshot snapshot,
            DocumentLine line,
            double zoom,
            double virtualUnitWidth)
        {
            var semantic = snapshot.GetLine(LineIndex(line));
            if (!semantic.IsQuoted || semantic.QuoteLevel <= 0)
            {
                return Array.Empty<double>();
            }

            var text = document.GetText(line);
            var container = MarkdownContainerPrefix.Parse(
                text,
                snapshot,
                line.Offset,
                line.EndOffset);
            var rails = new List<double>(semantic.QuoteLevel);
            foreach (var token in container.Tokens)
            {
                if (!token.IsQuote)
                {
                    continue;
                }

                if (TryGetLogicalPrefixPoint(
                        textView,
                        line,
                        text,
                        container,
                        token.MarkerStart,
                        VisualYPosition.TextMiddle,
                        out var point))
                {
                    rails.Add(point.X + 2.5 * zoom);
                }
            }

            if (container.MissingQuoteLevels > 0)
            {
                if (_owner.IsFullMode)
                {
                    var visualLine = textView.GetVisualLine(line.LineNumber);
                    var indent = visualLine?.Elements.OfType<QuoteIndentElement>().FirstOrDefault();
                    if (visualLine != null && indent != null)
                    {
                        // Source offsets can name either side of a zero-source element. Its visual
                        // column unambiguously identifies the gutter, even after a physical indent.
                        var indentPoint = visualLine.GetVisualPosition(indent.VisualColumn, VisualYPosition.TextMiddle);
                        for (var level = 0; level < indent.Levels; level++)
                        {
                            rails.Add(indentPoint.X - textView.HorizontalOffset + indent.UnitWidth * level + 2.5 * zoom);
                        }
                    }
                }
                else if (TryGetLogicalPrefixPoint(
                    textView, line, text, container, container.ContentStart,
                    VisualYPosition.TextMiddle, out var point))
                {
                    // Basic/Enhanced do not insert a gutter; retain their source-based position.
                    for (var level = 0; level < container.MissingQuoteLevels; level++)
                    {
                        rails.Add(point.X + virtualUnitWidth * level + 2.5 * zoom);
                    }
                }
            }

            // 防御性兜底：即使某个点暂时无法从 TextView 取得，也保证语义层级不会少画轨道。
            while (rails.Count < semantic.QuoteLevel)
            {
                rails.Add(2.5 * zoom + rails.Count * virtualUnitWidth);
            }

            rails.Sort();
            return rails.ToArray();
        }

        /// <summary>
        /// 把源码前缀换算成显示层逻辑 X。只把 Markdig 已确认的列表 marker 非空白字符替换成空格后
        /// 测量，引用 marker 与用户真实空白保持不变；因此列表首行和 continuation 行共享同一缩进列。
        /// </summary>
        private bool TryGetLogicalPrefixPoint(
            TextView textView,
            DocumentLine line,
            string sourceLine,
            MarkdownContainerPrefixInfo container,
            int relativeOffset,
            VisualYPosition yPosition,
            out Point point)
        {
            // Fixed unordered-list/quote slots now own the real prefix width in Full. Ordered
            // lists still use native glyph widths, so keep their existing continuation alignment.
            var hasOrderedListContext = false;
            foreach (var span in _owner.CurrentSnapshot().SpansForLine(LineIndex(line)))
            {
                if (span.Kind == MarkdownSemanticSpanKind.OrderedList)
                {
                    hasOrderedListContext = true;
                    break;
                }
            }
            if (_owner.IsFullMode && !hasOrderedListContext)
            {
                return MarkdownSemanticPresentation.TryGetTextPoint(
                    textView, line, line.Offset + relativeOffset, yPosition, out point);
            }

            point = default;
            if (!MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    line.Offset,
                    yPosition,
                    out var lineStartPoint))
            {
                return false;
            }

            var logicalPrefix = MarkdownContainerPrefix.BuildLogicalVisualPrefix(
                sourceLine,
                container,
                relativeOffset);
            if (logicalPrefix.Length == 0)
            {
                point = lineStartPoint;
                return true;
            }

            var typeface = new Typeface(
                _owner._editor.FontFamily,
                _owner._editor.FontStyle,
                _owner._editor.FontWeight,
                _owner._editor.FontStretch);
            var formatted = new FormattedText(
                logicalPrefix,
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                typeface,
                _owner._editor.FontSize,
                Brushes.Transparent,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(textView).PixelsPerDip);
            point = new Point(
                lineStartPoint.X + formatted.WidthIncludingTrailingWhitespace,
                lineStartPoint.Y);
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }

        private double MeasureVirtualQuoteUnit(TextView textView)
        {
            var typeface = new Typeface(
                _owner._editor.FontFamily,
                _owner._editor.FontStyle,
                _owner._editor.FontWeight,
                _owner._editor.FontStretch);
            var formatted = new FormattedText(
                "> ",
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                typeface,
                _owner._editor.FontSize,
                Brushes.Transparent,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(textView).PixelsPerDip);
            return Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
        }

        private static bool ContainsNear(IReadOnlyList<double> values, double target)
        {
            foreach (var value in values)
            {
                if (Math.Abs(value - target) <= 0.75)
                {
                    return true;
                }
            }

            return false;
        }

        private static double RowTop(TextView textView, DocumentLine line) =>
            textView.GetVisualTopByDocumentLine(line.LineNumber) - textView.VerticalOffset;

        private static int LineIndex(DocumentLine line) =>
            Math.Max(0, line.LineNumber - 1);

        private static double RowBottom(TextView textView, DocumentLine line)
        {
            if (line.NextLine != null)
            {
                return RowTop(textView, line.NextLine);
            }

            var bottom = RowTop(textView, line);
            foreach (var visualLine in textView.VisualLines)
            {
                var first = visualLine.FirstDocumentLine;
                if (first != null && first.LineNumber == line.LineNumber)
                {
                    bottom += visualLine.Height;
                }
            }

            return bottom;
        }
    }
}
