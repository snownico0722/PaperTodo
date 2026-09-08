using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer
    {
        private void ApplyHtmlSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            var lineSpans = snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1));
            foreach (var span in lineSpans)
            {
                if (span.End <= line.Offset || span.Start >= line.EndOffset)
                {
                    continue;
                }

                switch (span.Kind)
                {
                    case MarkdownSemanticSpanKind.HtmlMarker:
                        var markerBrush = _owner.ControlBrush(
                            HtmlMarkerRevealed(line, span, lineSpans));
                        ApplyAbsolute(line, span.Start, span.End, element =>
                        {
                            element.TextRunProperties.SetTypeface(NormalTypeface);
                            var size = _owner.ScaledFontSize(NoteTypography.FontSize);
                            element.TextRunProperties.SetFontRenderingEmSize(size);
                            element.TextRunProperties.SetFontHintingEmSize(size);
                            element.TextRunProperties.SetForegroundBrush(markerBrush);
                        });
                        break;

                    case MarkdownSemanticSpanKind.HtmlStrong:
                        ApplyAbsolute(line, span.Start, span.End, element =>
                        {
                            var current = element.TextRunProperties.Typeface;
                            var family = AppTypography.UsesCustomBoldFace(true)
                                ? SemanticBoldFontFamily
                                : current.FontFamily;
                            element.TextRunProperties.SetTypeface(GetCachedTypeface(
                                family,
                                current.Style,
                                SemanticBoldFontWeight,
                                current.Stretch));
                        });
                        break;

                    case MarkdownSemanticSpanKind.HtmlEmphasis:
                        ApplyAbsolute(line, span.Start, span.End, element =>
                        {
                            var current = element.TextRunProperties.Typeface;
                            element.TextRunProperties.SetTypeface(GetCachedTypeface(
                                current.FontFamily,
                                FontStyles.Italic,
                                current.Weight,
                                current.Stretch));
                        });
                        break;

                    case MarkdownSemanticSpanKind.HtmlStrikethrough:
                        ApplyAbsolute(
                            line,
                            span.Start,
                            span.End,
                            element => MergeDecoration(element, TextDecorations.Strikethrough));
                        break;

                    case MarkdownSemanticSpanKind.HtmlUnderline:
                        ApplyAbsolute(
                            line,
                            span.Start,
                            span.End,
                            element => MergeDecoration(element, TextDecorations.Underline));
                        break;

                    case MarkdownSemanticSpanKind.HtmlCode:
                        ApplyAbsolute(line, span.Start, span.End, element =>
                        {
                            element.TextRunProperties.SetTypeface(CodeTypeface);
                            var size = _owner.ScaledFontSize(NoteTypography.CodeFontSize);
                            element.TextRunProperties.SetFontRenderingEmSize(size);
                            element.TextRunProperties.SetFontHintingEmSize(size);
                            element.TextRunProperties.SetForegroundBrush(Theme.ActiveBrush);
                        });
                        break;
                }
            }
        }

        /// <summary>
        /// HtmlMarker 是否显灵：按所属 HtmlContainer 的成对区间判定（与塌缩一致——开/闭标签整对显隐，
        /// 不按“光标是否越过单枚标签起点”）。
        /// </summary>
        private bool HtmlMarkerRevealed(
            DocumentLine line,
            MarkdownSemanticSpan marker,
            ReadOnlySpan<MarkdownSemanticSpan> lineSpans)
        {
            foreach (var candidate in lineSpans)
            {
                if (candidate.Kind == MarkdownSemanticSpanKind.HtmlContainer &&
                    (candidate.Start == marker.Start || candidate.End == marker.End))
                {
                    return _owner.IsRangeRevealed(candidate.Start, candidate.End);
                }
            }

            // 防御：找不到 container（理论上不发生）时回退原行边界显灵判定，避免退化成不可见。
            return _owner.IsRevealed(
                line.LineNumber,
                marker.Start,
                marker.Length,
                MarkdownSemanticSpanKind.HtmlMarker);
        }

        private void ApplyEscapeSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            if (_owner._editor.IsPreviewMode)
            {
                return;
            }

            var source = _owner._editor.Text ?? string.Empty;
            foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (span.Kind != MarkdownSemanticSpanKind.EscapeMarker ||
                    span.End <= line.Offset ||
                    span.Start >= line.EndOffset)
                {
                    continue;
                }

                if (_owner.IsFullMode &&
                    MarkdownLinkEscapeOwnership.TryGetOwningLink(
                        span,
                        snapshot,
                        source,
                        out _))
                {
                    // Full 中地址、标题、引用编号等 label 外链接语法由整条链接统一取色和显灵；
                    // 其他渲染档仍沿用原有独立转义符取色。
                    continue;
                }

                ApplyAbsolute(line, span.Start, span.End, element =>
                {
                    element.TextRunProperties.SetTypeface(NormalTypeface);
                    var size = _owner.ScaledFontSize(NoteTypography.FontSize);
                    element.TextRunProperties.SetFontRenderingEmSize(size);
                    element.TextRunProperties.SetFontHintingEmSize(size);
                    element.TextRunProperties.SetForegroundBrush(_owner.ControlBrush(
                        _owner.IsRevealed(
                            line.LineNumber,
                            span.Start,
                            span.Length,
                            MarkdownSemanticSpanKind.EscapeMarker)));
                });
            }
        }

        private static void MergeDecoration(
            VisualLineElement element,
            TextDecorationCollection additions)
        {
            var existing = element.TextRunProperties.TextDecorations;
            if (existing == null || existing.Count == 0)
            {
                element.TextRunProperties.SetTextDecorations(additions);
                return;
            }

            var needsMerge = false;
            foreach (var decoration in additions)
            {
                if (!existing.Contains(decoration))
                {
                    needsMerge = true;
                    break;
                }
            }
            if (!needsMerge)
            {
                return;
            }

            var merged = new TextDecorationCollection(existing);
            foreach (var decoration in additions)
            {
                if (!merged.Contains(decoration))
                {
                    merged.Add(decoration);
                }
            }
            element.TextRunProperties.SetTextDecorations(merged);
        }
    }
}
