using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private static bool HasTaskMarkerOnLine(
        MarkdownSemanticSnapshot snapshot,
        DocumentLine line)
    {
        foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
        {
            if (span.Kind == MarkdownSemanticSpanKind.TaskListMarker &&
                span.Start < line.EndOffset &&
                span.End > line.Offset)
            {
                return true;
            }
        }
        return false;
    }

    private sealed partial class SemanticColorizer
    {
        private void ApplyListMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            ApplyTaskMarkerSemantics(line, snapshot);

            // 任务行：Enhanced 预览保持旧行为不在此处理；Full 档仍需隐藏/显灵项目符号。
            if (!_owner.IsFullMode &&
                (!_owner.RenderListBullets || HasTaskMarkerOnLine(snapshot, line)))
            {
                return;
            }

            foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind is not (
                        MarkdownSemanticSpanKind.UnorderedListMarker or
                        MarkdownSemanticSpanKind.OrderedListMarker) ||
                    marker.End <= line.Offset ||
                    marker.Start >= line.EndOffset)
                {
                    continue;
                }

                var revealed = _owner.IsRevealed(
                    line.LineNumber,
                    marker.Start,
                    marker.Length,
                    marker.Kind);
                var brush = _owner.IsFullMode
                    ? _owner.RevealColor(Theme.ActiveBrush, revealed)
                    : Brushes.Transparent;
                ApplyAbsolute(
                    line,
                    marker.Start,
                    marker.End,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }

        private void ApplyTaskMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind != MarkdownSemanticSpanKind.TaskListMarker ||
                    marker.Length < 3 ||
                    marker.End <= line.Offset ||
                    marker.Start >= line.EndOffset)
                {
                    continue;
                }

                var revealTask = !_owner.IsFullMode ||
                    _owner.IsRevealed(
                        line.LineNumber,
                        marker.Start,
                        marker.Length,
                        MarkdownSemanticSpanKind.TaskListMarker);
                ApplyAbsolute(
                    line,
                    marker.Start,
                    marker.End,
                    element => element.TextRunProperties.SetForegroundBrush(
                        _owner.RevealColor(Theme.ActiveBrush, revealTask)));
                if (marker.Checked && revealTask)
                {
                    ApplyAbsolute(
                        line,
                        marker.Start + 1,
                        Math.Min(marker.End, marker.Start + 2),
                        element => element.TextRunProperties.SetTypeface(StrongTypeface));
                }
            }
        }
    }

    private sealed class SemanticListRenderer : IBackgroundRenderer
    {
        private readonly MarkdownSemanticPresentation _owner;
        private Typeface? _listMarkerTypeface;
        private string? _listMarkerFontFamily;
        private FontStyle _listMarkerFontStyle;
        private FontWeight _listMarkerFontWeight;
        private FontStretch _listMarkerFontStretch;

        private Typeface ListMarkerTypeface
        {
            get
            {
                var family = NoteTypography.FontFamily;
                var style = NoteTypography.FontStyle;
                var weight = NoteTypography.FontWeight;
                var stretch = NoteTypography.FontStretch;
                if (_listMarkerTypeface == null ||
                    !string.Equals(_listMarkerFontFamily, family.Source, StringComparison.Ordinal) ||
                    _listMarkerFontStyle != style ||
                    _listMarkerFontWeight != weight ||
                    _listMarkerFontStretch != stretch)
                {
                    _listMarkerTypeface = new Typeface(family, style, weight, stretch);
                    _listMarkerFontFamily = family.Source;
                    _listMarkerFontStyle = style;
                    _listMarkerFontWeight = weight;
                    _listMarkerFontStretch = stretch;
                }
                return _listMarkerTypeface;
            }
        }

        public SemanticListRenderer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (!_owner.RenderListBullets || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var snapshot = _owner.CurrentSnapshot();
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    if (HasTaskMarkerOnLine(snapshot, line))
                    {
                        // Full 档：任务行按活动块显灵源码，否则以勾选框呈现。
                        if (_owner.IsFullMode)
                        {
                            if (IsTaskRevealed(snapshot, line))
                            {
                                continue;
                            }

                            DrawTaskCheckBox(textView, drawingContext, line);
                        }
                        continue;
                    }

                    foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
                    {
                        if (marker.Kind is not (
                                MarkdownSemanticSpanKind.UnorderedListMarker or
                                MarkdownSemanticSpanKind.OrderedListMarker) ||
                            marker.End <= line.Offset ||
                            marker.Start >= line.EndOffset)
                        {
                            continue;
                        }

                        // Full 档已把该 marker 还原成源码（活动行），跳过图形覆盖。
                        if (_owner.IsFullMode &&
                            _owner.IsRevealed(
                                line.LineNumber,
                                marker.Start,
                                marker.Length,
                                marker.Kind))
                        {
                            continue;
                        }

                        DrawMarker(textView, drawingContext, document, line, marker);
                    }
                }
            }
        }

        private bool IsTaskRevealed(MarkdownSemanticSnapshot snapshot, DocumentLine line)
        {
            foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind == MarkdownSemanticSpanKind.TaskListMarker &&
                    marker.Start < line.EndOffset &&
                    marker.End > line.Offset)
                {
                    return _owner.IsRevealed(
                        line.LineNumber,
                        marker.Start,
                        marker.Length,
                        MarkdownSemanticSpanKind.TaskListMarker);
                }
            }

            return false;
        }

        private void DrawTaskCheckBox(
            TextView textView,
            DrawingContext drawingContext,
            DocumentLine line)
        {
            var task = default(MarkdownSemanticSpan);
            var hasTask = false;
            foreach (var marker in _owner.CurrentSnapshot().SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind == MarkdownSemanticSpanKind.TaskListMarker &&
                    marker.End <= line.EndOffset &&
                    marker.Start >= line.Offset)
                {
                    task = marker;
                    hasTask = true;
                    break;
                }
            }
            if (!hasTask)
            {
                return;
            }

            if (!MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    task.Start,
                    VisualYPosition.TextTop,
                    out var topLeft) ||
                !MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    task.End,
                    VisualYPosition.TextBottom,
                    out var bottomRight))
            {
                return;
            }

            var cellLeft = Math.Min(topLeft.X, bottomRight.X);
            var cellRight = Math.Max(topLeft.X, bottomRight.X);
            var height = Math.Max(1, bottomRight.Y - topLeft.Y);
            var boxSize = Math.Min(Math.Max(8, height * 0.7), cellRight - cellLeft);
            var rect = new Rect(
                cellLeft + (cellRight - cellLeft - boxSize) / 2,
                topLeft.Y + (height - boxSize) / 2,
                boxSize,
                boxSize);

            drawingContext.DrawRectangle(Theme.PaperBrush, null, rect);
            var pen = new Pen(Theme.PaperBorderBrush, 1);
            if (task.Checked)
            {
                drawingContext.DrawRectangle(Theme.ActiveBrush, null, rect);
                var checkPen = new Pen(Theme.PaperBrush, Math.Max(1.2, boxSize * 0.14));
                drawingContext.DrawLine(
                    checkPen,
                    new Point(rect.Left + boxSize * 0.22, rect.Top + boxSize * 0.5),
                    new Point(rect.Left + boxSize * 0.45, rect.Top + boxSize * 0.72));
                drawingContext.DrawLine(
                    checkPen,
                    new Point(rect.Left + boxSize * 0.45, rect.Top + boxSize * 0.72),
                    new Point(rect.Left + boxSize * 0.8, rect.Top + boxSize * 0.3));
            }
            else
            {
                drawingContext.DrawRectangle(null, pen, new Rect(rect.Left + 0.5, rect.Top + 0.5, rect.Width - 1, rect.Height - 1));
            }
        }

        private void DrawMarker(
            TextView textView,
            DrawingContext drawingContext,
            IDocument document,
            DocumentLine line,
            MarkdownSemanticSpan marker)
        {
            if (!MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    marker.Start,
                    VisualYPosition.TextTop,
                    out var markerTop) ||
                !MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    marker.Start,
                    VisualYPosition.TextMiddle,
                    out var markerMiddle) ||
                !MarkdownSemanticPresentation.TryGetTextPoint(
                    textView,
                    line,
                    marker.End,
                    VisualYPosition.TextBottom,
                    out var markerBottom))
            {
                return;
            }

            var markerLeft = markerTop.X;
            var markerRight = markerBottom.X;
            if (markerRight < markerLeft)
            {
                (markerLeft, markerRight) = (markerRight, markerLeft);
            }

            var markerWidth = Math.Max(1, markerRight - markerLeft);
            var markerHeight = Math.Max(1, markerBottom.Y - markerTop.Y);
            drawingContext.DrawRectangle(
                Theme.PaperBrush,
                null,
                new Rect(markerLeft - 1, markerTop.Y - 1, markerWidth + 2, markerHeight + 2));

            if (marker.Kind == MarkdownSemanticSpanKind.UnorderedListMarker)
            {
                var radius = Math.Max(
                    2.0,
                    Math.Min(3.2, _owner.ScaledFontSize(NoteTypography.FontSize) * 0.16));
                drawingContext.DrawEllipse(
                    Theme.TextBrush,
                    null,
                    new Point(markerLeft + markerWidth / 2, markerMiddle.Y),
                    radius,
                    radius);
                return;
            }

            var markerText = document.GetText(marker.Start, marker.Length);
            var formatted = new FormattedText(
                markerText,
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                ListMarkerTypeface,
                _owner.ScaledFontSize(NoteTypography.FontSize),
                Theme.TextBrush,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(textView).PixelsPerDip);
            drawingContext.DrawText(
                formatted,
                new Point(markerLeft, markerMiddle.Y - formatted.Height / 2));
        }
    }
}
