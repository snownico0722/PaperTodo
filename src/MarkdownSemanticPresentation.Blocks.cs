using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer
    {
        private void ApplyBlockSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot,
            string text)
        {
            var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
            ApplyQuoteForeground(line, semantic);
            if (semantic.HeadingLevel > 0)
            {
                ApplyHeadingSemantics(line, snapshot, text);
            }
            ApplyCodeBlockSemantics(line, semantic);
            ApplyBlockMarkerSemantics(line, semantic);
            // 引用 marker 最后覆盖块级取色。统一容器前缀能识别 `- >`、`> - >` 等组合，
            // 不再只看物理行最开头的连续 `>`。
            if (semantic.IsQuoted)
            {
                ApplyQuoteMarkerSemantics(line, snapshot, text);
            }
        }

        private void ApplyQuoteForeground(
            DocumentLine line,
            MarkdownSemanticLine semantic)
        {
            if (!semantic.IsQuoted)
            {
                return;
            }

            // Quote foreground is presentation only. More specific inline/link/code styling is
            // applied later and can override it without touching the source TextDocument.
            ApplyAbsolute(
                line,
                line.Offset,
                line.EndOffset,
                element => element.TextRunProperties.SetForegroundBrush(Theme.WeakTextBrush));
        }

        private void ApplyQuoteMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot,
            string text)
        {
            var container = MarkdownContainerPrefix.Parse(
                text,
                snapshot,
                line.Offset,
                line.EndOffset);
            foreach (var token in container.Tokens)
            {
                if (!token.IsQuote)
                {
                    continue;
                }

                var start = line.Offset + token.MarkerStart;
                var end = line.Offset + token.ContentStart;
                var brush = _owner.QuoteControlBrush(
                    _owner.IsRevealed(
                        line.LineNumber,
                        start,
                        end - start,
                        MarkdownSemanticSpanKind.Quote));
                ApplyAbsolute(
                    line,
                    start,
                    end,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }

        private void ApplyHeadingSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot,
            string text)
        {
            foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (span.Kind is not (
                        MarkdownSemanticSpanKind.Heading or
                        MarkdownSemanticSpanKind.SetextHeading) ||
                    span.End <= line.Offset ||
                    span.Start >= line.EndOffset)
                {
                    continue;
                }

                var baseSize = span.Level switch
                {
                    1 => NoteTypography.Heading1FontSize,
                    2 => NoteTypography.Heading2FontSize,
                    3 => NoteTypography.Heading3FontSize,
                    _ => NoteTypography.FontSize
                };
                var size = _owner.ScaledFontSize(baseSize);
                ApplyAbsolute(
                    line,
                    span.Start,
                    span.End,
                    element =>
                    {
                        element.TextRunProperties.SetTypeface(HeadingTypeface);
                        element.TextRunProperties.SetFontRenderingEmSize(size);
                        element.TextRunProperties.SetFontHintingEmSize(size);
                    });

                if (span.Kind != MarkdownSemanticSpanKind.Heading)
                {
                    continue;
                }

                var localStart = Math.Clamp(span.Start - line.Offset, 0, text.Length);
                var markerEnd = localStart;
                while (markerEnd < text.Length && text[markerEnd] == '#')
                {
                    markerEnd++;
                }
                if (markerEnd == localStart)
                {
                    continue;
                }
                while (markerEnd < text.Length && char.IsWhiteSpace(text[markerEnd]))
                {
                    markerEnd++;
                }

                var openingStart = line.Offset + localStart;
                var openingLength = Math.Max(1, markerEnd - localStart);
                var markerBrush = _owner.ControlBrush(
                    _owner.IsRevealed(
                        line.LineNumber,
                        openingStart,
                        openingLength,
                        MarkdownSemanticSpanKind.Heading));
                ApplyAbsolute(
                    line,
                    openingStart,
                    line.Offset + markerEnd,
                    element => element.TextRunProperties.SetForegroundBrush(markerBrush));
                ApplyClosingAtxMarkerSemantics(line, text, markerEnd, markerBrush);
            }
        }

        private void ApplyClosingAtxMarkerSemantics(
            DocumentLine line,
            string text,
            int openingMarkerEnd,
            Brush markerBrush)
        {
            var closingEnd = text.Length;
            while (closingEnd > openingMarkerEnd && char.IsWhiteSpace(text[closingEnd - 1]))
            {
                closingEnd--;
            }

            var closingStart = closingEnd;
            while (closingStart > openingMarkerEnd && text[closingStart - 1] == '#')
            {
                closingStart--;
            }

            if (closingStart >= closingEnd ||
                closingStart <= openingMarkerEnd ||
                !char.IsWhiteSpace(text[closingStart - 1]))
            {
                return;
            }

            ApplyAbsolute(
                line,
                line.Offset + closingStart,
                line.Offset + closingEnd,
                element => element.TextRunProperties.SetForegroundBrush(markerBrush));
        }

        private void ApplyCodeBlockSemantics(
            DocumentLine line,
            MarkdownSemanticLine semantic)
        {
            if (!semantic.IsCode)
            {
                return;
            }

            var foreground = semantic.IsFencedCodeMarker
                ? _owner.ControlBrush(_owner.IsRevealed(
                    line.LineNumber,
                    line.Offset,
                    Math.Max(1, line.Length),
                    semantic.IsFencedCodeOpening
                        ? MarkdownSemanticSpanKind.FencedCodeOpening
                        : MarkdownSemanticSpanKind.FencedCodeClosing))
                : Theme.TextBrush;
            ApplyAbsolute(
                line,
                line.Offset,
                line.EndOffset,
                element => ApplyCodeTypography(element, foreground));
        }

        private void ApplyBlockMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticLine semantic)
        {
            if (semantic.IsSetextMarker || semantic.IsHorizontalRule)
            {
                var kind = semantic.IsSetextMarker
                    ? MarkdownSemanticSpanKind.SetextMarker
                    : MarkdownSemanticSpanKind.HorizontalRule;
                var brush = _owner.ControlBrush(
                    _owner.IsRevealed(
                        line.LineNumber,
                        line.Offset,
                        Math.Max(1, line.Length),
                        kind));
                ApplyAbsolute(
                    line,
                    line.Offset,
                    line.EndOffset,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
