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
            var width = textView.ActualWidth;
            var quotePen = new Pen(Theme.QuoteBorderBrush, 3);
            var inlineCodeBuilder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                CornerRadius = 3,
                BorderThickness = 0
            };

            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
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

                    var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
                    var needsQuote = semantic.IsQuoted;
                    var isCodeRow = semantic.IsCode;
                    if (isCodeRow && semantic.IsFencedCodeMarker && !_owner.IsFullMode)
                    {
                        // 非 Full 档维持旧行为：围栏开闭行不垫底，避免与源码标记叠印。
                        isCodeRow = false;
                    }

                    var needsCode = isCodeRow;
                    if (!needsQuote && !needsCode)
                    {
                        continue;
                    }

                    var top = textView.GetVisualTopByDocumentLine(line.LineNumber) -
                        textView.VerticalOffset;
                    var height = visualLine.Height;
                    if (line.NextLine != null)
                    {
                        var nextTop = textView.GetVisualTopByDocumentLine(line.NextLine.LineNumber) -
                            textView.VerticalOffset;
                        height = Math.Max(textView.DefaultLineHeight, nextTop - top);
                    }

                    if (needsCode)
                    {
                        drawingContext.DrawRoundedRectangle(
                            Theme.CodeBrush,
                            null,
                            new Rect(
                                0,
                                top + 1,
                                Math.Max(0, width - 4),
                                Math.Max(1, height - 2)),
                            4,
                            4);
                    }

                    if (needsQuote)
                    {
                        const double x = 2.5;
                        drawingContext.DrawLine(
                            quotePen,
                            new Point(x, top + 2),
                            new Point(x, top + Math.Max(2, height - 2)));
                    }
                }
            }

            var inlineCodeGeometry = inlineCodeBuilder.CreateGeometry();
            if (inlineCodeGeometry != null)
            {
                drawingContext.DrawGeometry(Theme.CodeBrush, null, inlineCodeGeometry);
            }
        }
    }
}
