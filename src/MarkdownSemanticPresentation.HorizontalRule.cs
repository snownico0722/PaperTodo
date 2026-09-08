using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed class SemanticHorizontalRuleRenderer : IBackgroundRenderer
    {
        private readonly MarkdownSemanticPresentation _owner;

        public SemanticHorizontalRuleRenderer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (!_owner.RenderHorizontalRules || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var width = textView.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            var snapshot = _owner.CurrentSnapshot();
            var pen = new Pen(Theme.PaperBorderBrush, Math.Max(0.75, _owner.ZoomFactor()));
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
                    if (!semantic.IsHorizontalRule)
                    {
                        continue;
                    }

                    // Full 档光标行直接显示源码，不再叠加最终横线。
                    if (_owner.IsFullMode &&
                        _owner.IsRevealed(
                            line.LineNumber,
                            line.Offset,
                            Math.Max(1, line.Length),
                            MarkdownSemanticSpanKind.HorizontalRule))
                    {
                        continue;
                    }

                    var text = document.GetText(line);
                    var container = MarkdownContainerPrefix.Parse(
                        text,
                        semantic.QuoteLevel,
                        snapshot,
                        line.Offset,
                        line.EndOffset);
                    var ruleStart = Math.Clamp(container.ContentStart, 0, text.Length);
                    while (ruleStart < text.Length && char.IsWhiteSpace(text[ruleStart]))
                    {
                        ruleStart++;
                    }
                    var ruleEnd = text.Length;
                    while (ruleEnd > ruleStart && char.IsWhiteSpace(text[ruleEnd - 1]))
                    {
                        ruleEnd--;
                    }

                    if (!MarkdownSemanticPresentation.TryGetTextPoint(
                            textView,
                            line,
                            line.Offset + ruleStart,
                            VisualYPosition.TextMiddle,
                            out var startPoint) ||
                        !MarkdownSemanticPresentation.TryGetTextPoint(
                            textView,
                            line,
                            line.Offset + ruleEnd,
                            VisualYPosition.TextMiddle,
                            out var endPoint))
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

                    // Enhanced 预览仍需盖住可见的 --- 源码，但只擦控制符本身；Full 源码已透明，
                    // 不擦整行，避免把外层引用竖线、列表圆点或任务框一起盖掉。
                    if (_owner.FadeSyntax)
                    {
                        var eraseLeft = Math.Min(startPoint.X, endPoint.X) - 1;
                        var eraseRight = Math.Max(startPoint.X, endPoint.X) + 1;
                        drawingContext.DrawRectangle(
                            Theme.PaperBrush,
                            null,
                            new Rect(
                                eraseLeft,
                                top,
                                Math.Max(1, eraseRight - eraseLeft),
                                Math.Max(1, height)));
                    }

                    var left = _owner.FadeSyntax || _owner.IsFullMode
                        ? startPoint.X
                        : endPoint.X + 8;
                    left = Math.Max(0, left);
                    var y = Math.Round(startPoint.Y) + 0.5;
                    drawingContext.DrawLine(
                        pen,
                        new Point(left, y),
                        new Point(Math.Max(left, width - 4), y));
                }
            }
        }
    }
}
