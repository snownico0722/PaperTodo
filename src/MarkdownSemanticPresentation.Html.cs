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
            foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (span.End <= line.Offset || span.Start >= line.EndOffset)
                {
                    continue;
                }

                switch (span.Kind)
                {
                    case MarkdownSemanticSpanKind.HtmlMarker:
                        var markerBrush = _owner.ControlBrush(
                            _owner.IsRevealed(
                                line.LineNumber,
                                span.Start,
                                span.Length,
                                MarkdownSemanticSpanKind.HtmlMarker));
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

        private void ApplyEscapeSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            if (_owner._editor.IsPreviewMode)
            {
                return;
            }

            foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (span.Kind != MarkdownSemanticSpanKind.EscapeMarker ||
                    span.End <= line.Offset ||
                    span.Start >= line.EndOffset)
                {
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
