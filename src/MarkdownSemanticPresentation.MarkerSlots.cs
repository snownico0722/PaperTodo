using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private MarkerSlotElementGenerator? _markerSlotGenerator;
    private MarkerSlotMetricKey? _markerSlotMetricKey;
    private MarkerSlotMetrics _markerSlotMetrics;
    private NativeSpaceMetricKey? _nativeSpaceMetricKey;
    private double _nativeSpaceAdvance;

    private readonly record struct MarkerSlotMetricKey(
        string FontFamily,
        double FontSize,
        FontStyle FontStyle,
        FontWeight FontWeight,
        FontStretch FontStretch,
        string StrongFontFamily,
        FontWeight StrongFontWeight,
        TextFormattingMode TextFormattingMode,
        double PixelsPerDip);

    private readonly record struct NativeSpaceMetricKey(
        string FontFamily,
        FontStyle FontStyle,
        FontWeight FontWeight,
        FontStretch FontStretch,
        double FontRenderingEmSize,
        double FontHintingEmSize,
        string CultureName,
        TextFormattingMode TextFormattingMode,
        double PixelsPerDip);

    private readonly record struct MarkerSlotMetrics(
        double QuoteWidth,
        double TaskOpenWidth,
        double TaskStateWidth,
        double TaskCloseWidth);

    private enum MarkerSlotSpecKind
    {
        PrefixText,
        FixedCell,
        NativeSpaceCell
    }

    private readonly record struct MarkerSlotSpec(
        int Start,
        int Length,
        MarkerSlotSpecKind Kind,
        double Width);

    private void AttachMarkerSlots()
    {
        _markerSlotGenerator = new MarkerSlotElementGenerator(this);
        // Keep one authority for real container-prefix layout. This generator owns the real
        // quote/list/task prefix; SyntaxCollapseElementGenerator remains responsible for collapsed
        // inline syntax and presentation-only lazy-quote indentation.
        _editor.TextArea.TextView.ElementGenerators.Add(_markerSlotGenerator);
    }

    private void DetachMarkerSlots()
    {
        if (_markerSlotGenerator == null)
        {
            return;
        }

        _editor.TextArea.TextView.ElementGenerators.Remove(_markerSlotGenerator);
        _markerSlotGenerator = null;
    }

    private MarkerSlotMetrics GetMarkerSlotMetrics(TextView textView)
    {
        var dpi = VisualTreeHelper.GetDpi(textView).PixelsPerDip;
        var strongFamily = AppTypography.FontFamilyFor(content: true, bold: true);
        var strongWeight = AppTypography.UsesCustomBoldFace(true)
            ? AppTypography.FontWeightFor(true)
            : NoteTypography.HeadingFontWeight;
        var key = new MarkerSlotMetricKey(
            _editor.FontFamily.Source,
            _editor.FontSize,
            _editor.FontStyle,
            _editor.FontWeight,
            _editor.FontStretch,
            strongFamily.Source,
            strongWeight,
            AppTypography.TextFormattingMode,
            dpi);
        if (_markerSlotMetricKey == key)
        {
            return _markerSlotMetrics;
        }

        var normalTypeface = new Typeface(
            _editor.FontFamily,
            _editor.FontStyle,
            _editor.FontWeight,
            _editor.FontStretch);
        var strongTypeface = new Typeface(
            strongFamily,
            _editor.FontStyle,
            strongWeight,
            _editor.FontStretch);

        var quote = MeasureMarkerText(textView, ">", normalTypeface);
        var taskOpen = MeasureMarkerText(textView, "[", normalTypeface);
        var taskState = Math.Max(
            MeasureMarkerText(textView, " ", normalTypeface),
            Math.Max(
                MeasureMarkerText(textView, "x", strongTypeface),
                MeasureMarkerText(textView, "X", strongTypeface)));
        var taskClose = MeasureMarkerText(textView, "]", normalTypeface);

        _markerSlotMetricKey = key;
        _markerSlotMetrics = new MarkerSlotMetrics(
            Math.Max(1, quote),
            Math.Max(1, taskOpen),
            Math.Max(1, taskState),
            Math.Max(1, taskClose));
        return _markerSlotMetrics;
    }

    private double MeasureMarkerText(TextView textView, string text, Typeface typeface)
    {
        var formatted = new FormattedText(
            text,
            UiLanguages.EffectiveUiCulture,
            FlowDirection.LeftToRight,
            typeface,
            _editor.FontSize,
            Brushes.Transparent,
            null,
            AppTypography.TextFormattingMode,
            VisualTreeHelper.GetDpi(textView).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private double GetQuoteUnitWidth(TextView textView, TextRunProperties properties) =>
        GetMarkerSlotMetrics(textView).QuoteWidth + GetNativeSpaceAdvance(textView, properties);

    private double GetNativeSpaceAdvance(
        TextView textView,
        TextRunProperties properties)
    {
        // The bullet cell must track the actual transformed run, not only the editor-wide font.
        // Code/list combinations can change Typeface and em size after the marker metrics were
        // prepared; key the cache from those final TextRunProperties so a code-space advance is
        // never reused by a later normal-text bullet (or vice versa).
        var typeface = properties.Typeface;
        var formattingMode = TextOptions.GetTextFormattingMode(textView);
        var dpi = VisualTreeHelper.GetDpi(textView).PixelsPerDip;
        var key = new NativeSpaceMetricKey(
            typeface.FontFamily.Source,
            typeface.Style,
            typeface.Weight,
            typeface.Stretch,
            properties.FontRenderingEmSize,
            properties.FontHintingEmSize,
            (properties.CultureInfo ?? UiLanguages.EffectiveUiCulture).Name,
            formattingMode,
            dpi);
        if (_nativeSpaceMetricKey == key && _nativeSpaceAdvance > 0)
        {
            return _nativeSpaceAdvance;
        }

        using var formatter = TextFormatter.Create(formattingMode);
        using var line = FormattedTextElement.PrepareText(formatter, " ", properties);
        var width = Math.Max(0.5, line.WidthIncludingTrailingWhitespace);
        _nativeSpaceMetricKey = key;
        _nativeSpaceAdvance = width;
        return width;
    }

    private sealed class MarkerSlotElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownSemanticPresentation _owner;
        private readonly List<MarkerSlotSpec> _specs = new();

        public MarkerSlotElementGenerator(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _specs.Clear();
            if (!_owner.IsFullMode || !_owner.TryCurrentSnapshot(out var snapshot))
            {
                return;
            }

            var line = context.VisualLine.FirstDocumentLine;
            var text = context.Document.GetText(line);
            var container = MarkdownContainerPrefix.Parse(
                text,
                snapshot,
                line.Offset,
                line.EndOffset);
            var metrics = _owner.GetMarkerSlotMetrics(context.TextView);

            foreach (var token in container.Tokens)
            {
                var start = line.Offset + token.MarkerStart;
                var length = token.MarkerEnd - token.MarkerStart;
                if (length <= 0)
                {
                    continue;
                }

                switch (token.Kind)
                {
                    case MarkdownContainerPrefixKind.Quote:
                        AddFixedMarker(start, length, metrics.QuoteWidth);
                        break;

                    case MarkdownContainerPrefixKind.UnorderedList:
                        AddNativeSpaceMarker(start, length);
                        break;

                    case MarkdownContainerPrefixKind.OrderedList:
                        _specs.Add(new MarkerSlotSpec(
                            start,
                            length,
                            MarkerSlotSpecKind.PrefixText,
                            0));
                        break;
                }
            }

            if (container.TaskMarkerStart >= 0 &&
                container.TaskMarkerEnd > container.TaskMarkerStart)
            {
                var start = line.Offset + container.TaskMarkerStart;
                var length = container.TaskMarkerEnd - container.TaskMarkerStart;
                if (length == 3)
                {
                    AddFixedCell(start, metrics.TaskOpenWidth);
                    AddFixedCell(start + 1, metrics.TaskStateWidth);
                    AddFixedCell(start + 2, metrics.TaskCloseWidth);
                }
                else
                {
                    AddFixedMarker(
                        start,
                        length,
                        metrics.TaskOpenWidth + metrics.TaskStateWidth + metrics.TaskCloseWidth);
                }
            }

            CompletePrefixCoverage(line, container);
        }

        private void CompletePrefixCoverage(
            DocumentLine line,
            MarkdownContainerPrefixInfo container)
        {
            _specs.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            if (_specs.Count == 0)
            {
                return;
            }

            var prefixEnd = line.Offset + Math.Clamp(container.VisualIndentEnd, 0, line.Length);
            var complete = new List<MarkerSlotSpec>(_specs.Count * 2 + 1);
            var cursor = line.Offset;
            foreach (var spec in _specs)
            {
                if (spec.Start > cursor)
                {
                    complete.Add(new MarkerSlotSpec(
                        cursor,
                        spec.Start - cursor,
                        MarkerSlotSpecKind.PrefixText,
                        0));
                }

                if (spec.Start < cursor)
                {
                    continue;
                }

                complete.Add(spec);
                cursor = spec.Start + spec.Length;
            }

            if (cursor < prefixEnd)
            {
                complete.Add(new MarkerSlotSpec(
                    cursor,
                    prefixEnd - cursor,
                    MarkerSlotSpecKind.PrefixText,
                    0));
            }

            _specs.Clear();
            _specs.AddRange(complete);
        }

        private void AddFixedMarker(int start, int length, double totalWidth)
        {
            var cellWidth = Math.Max(0.5, totalWidth / length);
            for (var index = 0; index < length; index++)
            {
                AddFixedCell(start + index, cellWidth);
            }
        }

        private void AddNativeSpaceMarker(int start, int length)
        {
            for (var index = 0; index < length; index++)
            {
                _specs.Add(new MarkerSlotSpec(
                    start + index,
                    1,
                    MarkerSlotSpecKind.NativeSpaceCell,
                    0));
            }
        }

        private void AddFixedCell(int start, double width)
        {
            _specs.Add(new MarkerSlotSpec(
                start,
                1,
                MarkerSlotSpecKind.FixedCell,
                Math.Max(0.5, width)));
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var spec in _specs)
            {
                if (spec.Start >= startOffset)
                {
                    return spec.Start;
                }
            }

            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            foreach (var spec in _specs)
            {
                if (spec.Start != offset)
                {
                    continue;
                }

                var source = CurrentContext.Document.GetText(offset, spec.Length);
                return spec.Kind switch
                {
                    MarkerSlotSpecKind.PrefixText =>
                        new MarkerPrefixTextElement(CurrentContext.VisualLine, spec.Length),
                    MarkerSlotSpecKind.FixedCell =>
                        new FixedMarkerCellElement(
                            _owner,
                            spec.Width,
                            source,
                            useNativeSpaceAdvance: false),
                    MarkerSlotSpecKind.NativeSpaceCell =>
                        new FixedMarkerCellElement(
                            _owner,
                            0,
                            source,
                            useNativeSpaceAdvance: true),
                    _ => null!
                };
            }

            return null!;
        }
    }

    /// <summary>
    /// Native source text that still participates in container indentation. This is used for the
    /// whitespace between semantic slots and for ordered-list markers. The whole real prefix stays
    /// under this generator so soft wrapping keeps inheriting the body column.
    /// </summary>
    private sealed class MarkerPrefixTextElement : VisualLineText
    {
        public MarkerPrefixTextElement(VisualLine parentVisualLine, int length)
            : base(parentVisualLine, length)
        {
        }

        protected override VisualLineText CreateInstance(int length) =>
            new MarkerPrefixTextElement(ParentVisualLine, length);

        public override bool IsWhitespace(int visualColumn) => true;
    }

    /// <summary>
    /// One source character maps to one visual caret cell, while the cell's physical advance is
    /// semantic and stable. The element still flows through SemanticColorizer, so hidden preview
    /// source, active-source reveal, strong task state and reveal fade all use the existing single
    /// style authority instead of a second overlay renderer.
    /// </summary>
    private sealed class FixedMarkerCellElement : VisualLineElement
    {
        private readonly MarkdownSemanticPresentation _owner;
        private readonly double _width;
        private readonly string _source;
        private readonly bool _useNativeSpaceAdvance;

        public FixedMarkerCellElement(
            MarkdownSemanticPresentation owner,
            double width,
            string source,
            bool useNativeSpaceAdvance)
            : base(visualLength: 1, documentLength: 1)
        {
            _owner = owner;
            _width = Math.Max(0, width);
            _source = source;
            _useNativeSpaceAdvance = useNativeSpaceAdvance;
        }

        public override TextRun CreateTextRun(
            int startVisualColumn,
            ITextRunConstructionContext context)
        {
            var properties = TextRunProperties;
            var formatted = new FormattedText(
                _source,
                properties.CultureInfo ?? UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                properties.Typeface,
                properties.FontRenderingEmSize,
                properties.ForegroundBrush ?? Brushes.Transparent,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(context.TextView).PixelsPerDip);
            var width = _useNativeSpaceAdvance
                ? _owner.GetNativeSpaceAdvance(context.TextView, properties)
                : Math.Max(0.5, _width);
            return new FixedMarkerCellRun(properties, width, formatted);
        }

        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class FixedMarkerCellRun : TextEmbeddedObject
    {
        private readonly TextRunProperties _properties;
        private readonly double _width;
        private readonly FormattedText _formatted;

        public FixedMarkerCellRun(
            TextRunProperties properties,
            double width,
            FormattedText formatted)
        {
            _properties = properties;
            _width = Math.Max(0.5, width);
            _formatted = formatted;
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => new();
        public override int Length => 1;
        public override TextRunProperties Properties => _properties;

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) =>
            new(
                _width,
                Math.Max(1, _formatted.Height),
                Math.Clamp(_formatted.Baseline, 0, Math.Max(1, _formatted.Height)));

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) =>
            new(0, 0, _width, Math.Max(1, _formatted.Height));

        public override void Draw(
            DrawingContext drawingContext,
            Point origin,
            bool rightToLeft,
            bool sideways)
        {
            // The marker glyph may overhang its semantic cell while editing (notably `+`), but the
            // following content keeps the exact native-space advance used by physical continuations.
            origin.X += (_width - _formatted.WidthIncludingTrailingWhitespace) / 2;
            origin.Y -= _formatted.Baseline;
            drawingContext.DrawText(_formatted, origin);
        }
    }
}
