using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownSemanticPresentation _owner;
        private readonly Dictionary<TypefaceCacheKey, Typeface> _typefaceCache = new();
        private TypefaceCacheRevision _typefaceCacheRevision;
        private bool _hasTypefaceCacheRevision;

        private readonly record struct SourceRange(int Start, int End)
        {
            public bool Covers(int start, int end) => Start <= start && End >= end;
        }

        private readonly record struct TypefaceCacheKey(
            string Family,
            FontStyle Style,
            FontWeight Weight,
            FontStretch Stretch);

        private readonly record struct TypefaceCacheRevision(
            string NormalFamily,
            string BoldFamily,
            string CodeFamily,
            FontStyle Style,
            FontWeight NormalWeight,
            FontWeight BoldWeight,
            FontStretch Stretch);

        private Typeface NormalTypeface => GetCachedTypeface(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static FontFamily SemanticBoldFontFamily =>
            AppTypography.FontFamilyFor(content: true, bold: true);

        private static FontWeight SemanticBoldFontWeight =>
            AppTypography.UsesCustomBoldFace(true)
                ? AppTypography.FontWeightFor(true)
                : NoteTypography.HeadingFontWeight;

        private Typeface HeadingTypeface => GetCachedTypeface(
            SemanticBoldFontFamily,
            NoteTypography.FontStyle,
            SemanticBoldFontWeight,
            NoteTypography.FontStretch);

        private Typeface StrongTypeface => HeadingTypeface;

        private Typeface CodeTypeface => GetCachedTypeface(
            NoteTypography.CodeFontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        public SemanticColorizer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (!_owner.ApplyMarkdownStyle || line.Length <= 0)
            {
                return;
            }

            var document = CurrentContext.Document;
            if (!_owner.TryCurrentSnapshot(out var snapshot))
            {
                return;
            }
            if (TryHideImageReference(line))
            {
                return;
            }

            var lineIndex = Math.Max(0, line.LineNumber - 1);
            var semantic = snapshot.GetLine(lineIndex);
            if (semantic.Traits == MarkdownSemanticLineTraits.None &&
                semantic.HeadingLevel == 0 &&
                snapshot.SpansForLine(lineIndex).IsEmpty &&
                snapshot.LinksForLine(lineIndex).IsEmpty)
            {
                return;
            }

            var text = semantic.IsQuoted || semantic.HeadingLevel > 0
                ? document.GetText(line)
                : string.Empty;
            ApplyBlockSemantics(line, snapshot, text);
            ApplyListMarkerSemantics(line, snapshot);
            // Link foreground first; Markdown/HTML emphasis and code then compose on top.
            ApplyLinkSemantics(line, snapshot);
            ApplyInlineSemantics(line, snapshot);
            ApplyHtmlSemantics(line, snapshot);
            ApplyEscapeSemantics(line, snapshot);
        }

        private bool TryHideImageReference(DocumentLine line)
        {
            var semantic = _owner.CurrentSnapshot().GetLine(Math.Max(0, line.LineNumber - 1));
            if (semantic.IsCode ||
                !_owner._editor.ShouldHideImageReferenceTextForSemanticPresentation ||
                !_owner._editor.IsImageReferenceLineForSemanticPresentation(line))
            {
                return false;
            }

            // Preserve the source line's metrics exactly as the mature colorizer did. The image
            // element generator still belongs to MarkdownTextBox; only its reference text styling
            // moves into the semantic presentation authority.
            ApplyAbsolute(line, line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetTypeface(NormalTypeface);
                var size = _owner.ScaledFontSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontRenderingEmSize(size);
                element.TextRunProperties.SetFontHintingEmSize(size);
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
            });
            return true;
        }

        private void ApplyLinkSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            if (lineEnd <= lineStart)
            {
                return;
            }

            var isPreviewMode = _owner._editor.IsPreviewMode;
            var isFullMode = _owner.IsFullMode;
            var showLabelStyle = isPreviewMode || isFullMode;

            Brush SyntaxBrush(MarkdownSemanticLink currentLink)
            {
                if (isFullMode)
                {
                    return _owner.RevealColor(
                        Theme.ActiveBrush,
                        _owner.IsRangeRevealed(currentLink.Start, currentLink.End));
                }

                return _owner.FadeSyntax
                    ? Theme.SyntaxFadeBrush
                    : Theme.ActiveBrush;
            }

            Brush DestinationBrush(MarkdownSemanticLink currentLink)
            {
                if (isFullMode)
                {
                    return _owner.RevealColor(
                        Theme.WeakTextBrush,
                        _owner.IsRangeRevealed(currentLink.Start, currentLink.End));
                }

                return _owner.FadeSyntax
                    ? Theme.SyntaxFadeBrush
                    : Theme.WeakTextBrush;
            }

            foreach (var link in snapshot.LinksForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (link.End <= lineStart || link.Start >= lineEnd)
                {
                    continue;
                }

                if (!link.IsAuto)
                {
                    // Color only syntax around the label. Painting the whole source span first would
                    // incorrectly recolor the label in edit mode (and inside quote styling).
                    ApplyAbsolute(
                        line,
                        link.Start,
                        Math.Min(link.LabelStart, link.End),
                        element => element.TextRunProperties.SetForegroundBrush(SyntaxBrush(link)));
                    ApplyAbsolute(
                        line,
                        Math.Max(link.LabelEnd, link.Start),
                        link.End,
                        element => element.TextRunProperties.SetForegroundBrush(SyntaxBrush(link)));

                    if (link.DestinationStart >= 0 && link.DestinationLength > 0)
                    {
                        ApplyAbsolute(
                            line,
                            link.DestinationStart,
                            link.DestinationEnd,
                            element => element.TextRunProperties.SetForegroundBrush(DestinationBrush(link)));
                    }
                }

                if (showLabelStyle && link.LabelLength > 0)
                {
                    ApplyAbsolute(
                        line,
                        link.LabelStart,
                        link.LabelEnd,
                        element =>
                        {
                            element.TextRunProperties.SetForegroundBrush(Theme.LinkBrush);
                            MergeDecoration(element, TextDecorations.Underline);
                        });
                }
            }
        }

        private void ApplyInlineSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            if (lineEnd <= lineStart)
            {
                return;
            }

            var lineSpans = snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1));
            var hasInlineSemantic = false;
            foreach (var span in lineSpans)
            {
                if (span.End > lineStart &&
                    span.Start < lineEnd &&
                    span.Kind is MarkdownSemanticSpanKind.Emphasis or
                        MarkdownSemanticSpanKind.Strong or
                        MarkdownSemanticSpanKind.Strikethrough or
                        MarkdownSemanticSpanKind.InlineCode)
                {
                    hasInlineSemantic = true;
                    break;
                }
            }
            if (!hasInlineSemantic)
            {
                return;
            }

            List<SourceRange>? emphasisRanges = null;
            List<SourceRange>? strongRanges = null;
            List<int>? boundaries = null;
            var fullMode = _owner.IsFullMode;

            Brush MarkerBrushFor(MarkdownSemanticSpan span)
            {
                var revealed = _owner.IsRevealed(
                    line.LineNumber,
                    span.Start,
                    span.Length,
                    span.Kind,
                    span.Start,
                    span.End);
                if (fullMode)
                {
                    return _owner.RevealColor(Theme.ActiveBrush, revealed);
                }

                return _owner.FadeSyntax
                    ? Theme.SyntaxFadeBrush
                    : Theme.ActiveBrush;
            }

            foreach (var span in lineSpans)
            {
                if (span.End <= lineStart || span.Start >= lineEnd)
                {
                    continue;
                }

                if (span.Kind is not (
                        MarkdownSemanticSpanKind.Emphasis or
                        MarkdownSemanticSpanKind.Strong or
                        MarkdownSemanticSpanKind.Strikethrough or
                        MarkdownSemanticSpanKind.InlineCode))
                {
                    continue;
                }

                var markerLength = Math.Clamp(
                    span.MarkerLength,
                    1,
                    Math.Max(1, span.Length / 2));
                var contentStart = Math.Min(span.End, span.Start + markerLength);
                var contentEnd = Math.Max(contentStart, span.End - markerLength);

                ApplyAbsolute(
                    line,
                    span.Start,
                    contentStart,
                    element =>
                    {
                        if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                        {
                            ApplyCodeTypography(element, MarkerBrushFor(span));
                        }
                        else
                        {
                            element.TextRunProperties.SetForegroundBrush(MarkerBrushFor(span));
                        }
                    });
                ApplyAbsolute(
                    line,
                    contentEnd,
                    span.End,
                    element =>
                    {
                        if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                        {
                            ApplyCodeTypography(element, MarkerBrushFor(span));
                        }
                        else
                        {
                            element.TextRunProperties.SetForegroundBrush(MarkerBrushFor(span));
                        }
                    });

                if (contentEnd <= contentStart)
                {
                    continue;
                }

                if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                {
                    ApplyAbsolute(
                        line,
                        contentStart,
                        contentEnd,
                        element => ApplyCodeTypography(element, Theme.ActiveBrush));
                    continue;
                }

                if (span.Kind == MarkdownSemanticSpanKind.Strikethrough)
                {
                    ApplyAbsolute(
                        line,
                        contentStart,
                        contentEnd,
                        element => MergeDecoration(element, TextDecorations.Strikethrough));
                    continue;
                }

                var clippedStart = Math.Max(lineStart, contentStart);
                var clippedEnd = Math.Min(lineEnd, contentEnd);
                if (clippedEnd <= clippedStart)
                {
                    continue;
                }

                var range = new SourceRange(clippedStart, clippedEnd);
                if (span.Kind == MarkdownSemanticSpanKind.Strong)
                {
                    (strongRanges ??= new List<SourceRange>()).Add(range);
                }
                else
                {
                    (emphasisRanges ??= new List<SourceRange>()).Add(range);
                }

                boundaries ??= new List<int> { lineStart, lineEnd };
                boundaries.Add(clippedStart);
                boundaries.Add(clippedEnd);
            }

            if (boundaries == null)
            {
                return;
            }

            boundaries.Sort();
            var compactCount = 0;
            var previous = int.MinValue;
            for (var read = 0; read < boundaries.Count; read++)
            {
                var boundary = boundaries[read];
                if (boundary == previous)
                {
                    continue;
                }
                boundaries[compactCount++] = boundary;
                previous = boundary;
            }

            for (var index = 0; index + 1 < compactCount; index++)
            {
                var start = boundaries[index];
                var end = boundaries[index + 1];
                if (end <= start)
                {
                    continue;
                }

                var strong = Covers(strongRanges, start, end);
                var emphasis = Covers(emphasisRanges, start, end);
                if (!strong && !emphasis)
                {
                    continue;
                }

                ApplyAbsolute(
                    line,
                    start,
                    end,
                    element =>
                    {
                        var current = element.TextRunProperties.Typeface;
                        var family = strong && AppTypography.UsesCustomBoldFace(true)
                            ? SemanticBoldFontFamily
                            : current.FontFamily;
                        var style = emphasis ? FontStyles.Italic : current.Style;
                        var weight = strong ? SemanticBoldFontWeight : current.Weight;
                        element.TextRunProperties.SetTypeface(GetCachedTypeface(
                            family,
                            style,
                            weight,
                            current.Stretch));
                    });
            }
        }

        private static bool Covers(List<SourceRange>? ranges, int start, int end)
        {
            if (ranges == null)
            {
                return false;
            }

            foreach (var range in ranges)
            {
                if (range.Covers(start, end))
                {
                    return true;
                }
            }
            return false;
        }

        private Typeface GetCachedTypeface(
            FontFamily family,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch)
        {
            EnsureTypefaceCacheCurrent();
            var key = new TypefaceCacheKey(family.Source, style, weight, stretch);
            if (_typefaceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            cached = new Typeface(family, style, weight, stretch);
            _typefaceCache[key] = cached;
            return cached;
        }

        private void EnsureTypefaceCacheCurrent()
        {
            var revision = new TypefaceCacheRevision(
                NoteTypography.FontFamily.Source,
                SemanticBoldFontFamily.Source,
                NoteTypography.CodeFontFamily.Source,
                NoteTypography.FontStyle,
                NoteTypography.FontWeight,
                SemanticBoldFontWeight,
                NoteTypography.FontStretch);
            if (_hasTypefaceCacheRevision && revision.Equals(_typefaceCacheRevision))
            {
                return;
            }

            _typefaceCache.Clear();
            _typefaceCacheRevision = revision;
            _hasTypefaceCacheRevision = true;
        }

        private void ApplyCodeTypography(
            VisualLineElement element,
            Brush foreground)
        {
            element.TextRunProperties.SetTypeface(CodeTypeface);
            var size = _owner.ScaledFontSize(NoteTypography.CodeFontSize);
            element.TextRunProperties.SetFontRenderingEmSize(size);
            element.TextRunProperties.SetFontHintingEmSize(size);
            element.TextRunProperties.SetForegroundBrush(foreground);
        }

        private void ApplyAbsolute(
            DocumentLine line,
            int absoluteStart,
            int absoluteEnd,
            Action<VisualLineElement> action)
        {
            var start = Math.Max(line.Offset, absoluteStart);
            var end = Math.Min(line.EndOffset, absoluteEnd);
            if (end > start)
            {
                ChangeLinePart(start, end, action);
            }
        }
    }
}
