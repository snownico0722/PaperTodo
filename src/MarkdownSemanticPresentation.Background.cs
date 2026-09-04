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
            var quotePen = new Pen(Theme.QuoteBorderBrush, 3);
            var inlineCodeBuilder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                CornerRadius = 3,
                BorderThickness = 0
            };

            // 收集当前视口可见的 DocumentLine（软折行会被多个 VisualLine 访问，去重后仍按行号升序）。
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
                var lineIndex = Math.Max(0, line.LineNumber - 1);
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

            // 代码块背景：把“纳入代码块的可见行”合成一个 run，一次画一整块圆角面板，
            // 消除逐行绘制造成的接缝；Full 档围栏开/闭行也并入（代码面板上下连续）。
            DrawCodeRuns(textView, drawingContext, snapshot, visible);

            // 引用竖条：把“连续 IsQuoted 的可见行”合成一个 run，一个 run 只画一条贯穿竖条，
            // 消除逐行 2px 缩进造成的行间断裂（> a\n>\n> b 与惰性续行/软折行都保持连续）。
            DrawQuoteRuns(textView, drawingContext, snapshot, visible, quotePen);

            var inlineCodeGeometry = inlineCodeBuilder.CreateGeometry();
            if (inlineCodeGeometry != null)
            {
                drawingContext.DrawGeometry(Theme.CodeBrush, null, inlineCodeGeometry);
            }
        }

        /// <summary>该行是否被纳入代码块背景（Full 档含围栏开/闭行，其余档位维持旧语义）。</summary>
        private bool IsCodeRow(MarkdownSemanticSnapshot snapshot, DocumentLine line)
        {
            var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
            if (!semantic.IsCode)
            {
                return false;
            }

            if (semantic.IsFencedCodeMarker && !_owner.IsFullMode)
            {
                // 非 Full 档：围栏开闭行不垫底，避免与可见源码标记叠印。
                return false;
            }

            return true;
        }

        private void DrawCodeRuns(
            TextView textView,
            DrawingContext drawingContext,
            MarkdownSemanticSnapshot snapshot,
            List<DocumentLine> visible)
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
                drawingContext.DrawRoundedRectangle(
                    Theme.CodeBrush,
                    null,
                    new Rect(
                        0,
                        top + 1,
                        Math.Max(0, textView.ActualWidth - 4),
                        Math.Max(1, height - 2)),
                    4,
                    4);
                index++;
            }
        }

        private void DrawQuoteRuns(
            TextView textView,
            DrawingContext drawingContext,
            MarkdownSemanticSnapshot snapshot,
            List<DocumentLine> visible,
            Pen quotePen)
        {
            const double x = 2.5;
            var count = visible.Count;
            var index = 0;
            while (index < count)
            {
                if (!snapshot.GetLine(Math.Max(0, visible[index].LineNumber - 1)).IsQuoted)
                {
                    index++;
                    continue;
                }

                var first = visible[index];
                var last = first;
                while (index + 1 < count &&
                       snapshot.GetLine(Math.Max(0, visible[index + 1].LineNumber - 1)).IsQuoted)
                {
                    index++;
                    last = visible[index];
                }

                var top = RowTop(textView, first);
                var bottom = RowBottom(textView, last);
                // 只在 run 的真首/末方向留 1px，中间不再有任何 inset，竖条无缝。
                drawingContext.DrawLine(
                    quotePen,
                    new Point(x, top + 1),
                    new Point(x, Math.Max(top + 1, bottom - 1)));
                index++;
            }
        }

        /// <summary>行在视口坐标系中的上沿（该行首 VisualLine 的上沿）。</summary>
        private static double RowTop(TextView textView, DocumentLine line) =>
            textView.GetVisualTopByDocumentLine(line.LineNumber) - textView.VerticalOffset;

        /// <summary>
        /// 行在视口坐标系中的下沿：优先用“下一 DocumentLine 的上沿”（覆盖整行含软折行全部
        /// VisualLine）；若已是文档末行，则取其最后一个 VisualLine 的底部兜底，避免折行缺尾。
        /// </summary>
        private double RowBottom(TextView textView, DocumentLine line)
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
