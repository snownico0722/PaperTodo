using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace PaperTodo;

// Bounded long or style-dense paragraphs use this element. WPF TextFormatter owns
// wrapping/shaping; the completed vector drawing is replayed by Measure/Arrange/Render without
// reformatting. This remains a child of the existing preview, never a window or a bitmap surface.
internal sealed class MarkdownEdgePreviewParagraph : Canvas
{
    internal const int MinimumSourceLength = 256;
    private readonly TextBlock _template;
    private readonly string _source;
    private readonly string _mode;
    private readonly MarkdownEdgeCapsulePreviewRenderer.PreviewInlineCache _inlineCache;
    private readonly double _zoom;
    private readonly Action<string> _openExternal;
    private readonly DrawingGroup _drawing = new();
    private Size _size;
    internal double MeasuredWidth { get; private set; }
    internal string VisibleText { get; private set; } = "";
    internal int FormattedLines { get; private set; }

    internal MarkdownEdgePreviewParagraph(TextBlock template, string source, string mode,
        double zoom, Action<string> openExternal, MarkdownEdgeCapsulePreviewRenderer.PreviewInlineCache inlineCache)
    {
        _template = template; _source = source; _mode = mode; _zoom = zoom; _openExternal = openExternal;
        _inlineCache = inlineCache;
        Margin = template.Margin;
        template.Margin = new Thickness();
        // A small template carries the normal paragraph/prefix typography and resource inheritance.
        // It is never measured or painted, and is removed once its values have been captured.
        template.Opacity = 0; template.IsHitTestVisible = false;
        Children.Add(template);
        NoteTypography.ApplyTextRendering(this);
    }

    internal IEnumerable<bool> Prepare(Size viewport)
    {
        var pieces = Prefix(_template);
        var count = 0;
        foreach (var piece in _inlineCache.Get(_source, _mode).Pieces)
        {
            if (piece.Text.Length > 0) pieces.Add(piece);
            if (++count % 32 == 0) yield return false;
        }
        var source = new ParagraphSource(pieces, _template, _zoom, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Background = _template.Background;
        Children.Remove(_template);
        using var formatter = TextFormatter.Create(AppTypography.TextFormattingMode);
        var runCache = new TextRunCache();
        var paragraph = new ParagraphProperties(source.DefaultProperties);
        TextLineBreak? previous = null;
        var offset = 0;
        var height = 0.0;
        var nextLink = 0;
        var width = Math.Max(1, viewport.Width);
        try
        {
            while (offset < source.Text.Length && height <= viewport.Height)
            {
                using var line = formatter.FormatLine(source, offset, width, paragraph, previous, runCache);
                previous?.Dispose(); previous = line.GetTextLineBreak();
                var lineDrawing = new DrawingGroup();
                using (var drawing = lineDrawing.Open()) line.Draw(drawing, new Point(0, height), InvertAxes.None);
                // Drawing commands reference the host's brushes. Freeze a snapshot, never
                // freeze those shared resources as a side effect of preparing this paragraph.
                if (lineDrawing.CanFreeze) lineDrawing = (DrawingGroup)lineDrawing.GetAsFrozen();
                _drawing.Children.Add(lineDrawing);
                var end = Math.Min(source.Text.Length, offset + line.Length);
                // Link ranges are ordered; do not rescan offscreen links for every visible line.
                while (nextLink < source.Links.Count && source.Links[nextLink].End <= offset) nextLink++;
                for (var i = nextLink; i < source.Links.Count && source.Links[i].Start < end; i++)
                {
                    var range = source.Links[i];
                    var start = Math.Max(offset, range.Start); var stop = Math.Min(end, range.End);
                    if (stop <= start) continue;
                    foreach (var bounds in line.GetTextBounds(start, stop - start))
                    {
                        var rect = bounds.Rectangle; rect.Offset(0, height);
                        var hit = new Border
                        {
                            Background = Brushes.Transparent,
                            Width = rect.Width,
                            Height = rect.Height,
                            Cursor = Cursors.Hand,
                            Focusable = true,
                            ToolTip = range.Uri.AbsoluteUri
                        };
                        EdgeCapsulePreviewInteraction.SetConsumesPointer(hit, true);
                        hit.MouseLeftButtonUp += (_, e) => { _openExternal(range.Uri.AbsoluteUri); e.Handled = true; };
                        hit.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { _openExternal(range.Uri.AbsoluteUri); e.Handled = true; } };
                        SetLeft(hit, rect.X); SetTop(hit, rect.Y); Children.Add(hit);
                    }
                }
                height += line.Height; offset = end; FormattedLines++;
                yield return false;
            }
        }
        finally { previous?.Dispose(); }
        _size = new Size(width, height);
        VisibleText = source.Text[..offset];
        InvalidateMeasure(); InvalidateVisual();
        yield return offset < source.Text.Length;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        MeasuredWidth = constraint.Width;
        base.MeasureOverride(constraint);
        return _size;
    }

    private static List<MarkdownEdgeCapsulePreviewRenderer.InlinePiece> Prefix(TextBlock template)
    {
        var pieces = new List<MarkdownEdgeCapsulePreviewRenderer.InlinePiece>();
        foreach (Inline inline in template.Inlines)
            if (inline is Run run && run.Text.Length > 0)
                pieces.Add(new(run.Text, ReferenceEquals(run.Foreground, Theme.SyntaxFadeBrush)
                    ? MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Syntax : 0));
        return pieces;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawDrawing(_drawing);
    }

    private sealed class ParagraphSource : TextSource
    {
        private readonly (int Start, int End, RunProperties Properties)[] _runs;
        internal readonly record struct LinkRange(int Start, int End, Uri Uri);
        internal string Text { get; }
        internal List<LinkRange> Links { get; } = new();
        internal RunProperties DefaultProperties { get; }
        internal ParagraphSource(List<MarkdownEdgeCapsulePreviewRenderer.InlinePiece> pieces, TextBlock template, double zoom, double dpi)
        {
            PixelsPerDip = dpi;
            DefaultProperties = new RunProperties(template, 0, false, zoom) { PixelsPerDip = dpi };
            Text = string.Concat(pieces.Select(p => p.Text));
            _runs = new (int, int, RunProperties)[pieces.Count];
            var styles = new Dictionary<(MarkdownEdgeCapsulePreviewRenderer.InlineStyle, bool), RunProperties>();
            var offset = 0;
            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i]; var key = (piece.Style, piece.Link != null);
                if (!styles.TryGetValue(key, out var properties))
                    styles[key] = properties = new RunProperties(template, piece.Style, key.Item2, zoom) { PixelsPerDip = dpi };
                _runs[i] = (offset, offset + piece.Text.Length, properties);
                if (piece.Link != null)
                {
                    if (Links.Count > 0 && Links[^1].End == offset && ReferenceEquals(Links[^1].Uri, piece.Link))
                        Links[^1] = Links[^1] with { End = offset + piece.Text.Length };
                    else Links.Add(new(offset, offset + piece.Text.Length, piece.Link));
                }
                offset += piece.Text.Length;
            }
        }
        public override TextRun GetTextRun(int index)
        {
            if (index >= Text.Length) return new TextEndOfParagraph(1, DefaultProperties);
            var low = 0; var high = _runs.Length - 1;
            while (low < high) { var middle = (low + high) / 2; if (_runs[middle].End <= index) low = middle + 1; else high = middle; }
            var run = _runs[low];
            return new TextCharacters(Text, index, run.End - index, run.Properties);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit) =>
            new(limit, new CultureSpecificCharacterBufferRange(DefaultProperties.CultureInfo, new CharacterBufferRange(Text, 0, Math.Min(limit, Text.Length))));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class ParagraphProperties(TextRunProperties properties) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => false;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
    private sealed class RunProperties : TextRunProperties
    {
        public override Typeface Typeface { get; }
        public override double FontRenderingEmSize { get; }
        public override double FontHintingEmSize => FontRenderingEmSize;
        public override TextDecorationCollection? TextDecorations { get; }
        public override Brush ForegroundBrush { get; }
        public override Brush? BackgroundBrush { get; }
        public override CultureInfo CultureInfo { get; }
        public override TextEffectCollection? TextEffects => null;
        internal RunProperties(TextBlock template, MarkdownEdgeCapsulePreviewRenderer.InlineStyle style, bool link, double zoom)
        {
            CultureInfo = template.Language.GetEquivalentCulture();
            bool Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle flag) => (style & flag) != 0;
            var strong = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Strong);
            var code = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Code);
            var family = code ? NoteTypography.CodeFontFamily : strong ? AppTypography.FontFamilyFor(content: true, bold: true) : template.FontFamily;
            var weight = strong ? AppTypography.UsesCustomBoldFace(true) ? AppTypography.FontWeightFor(true) : NoteTypography.HeadingFontWeight : template.FontWeight;
            Typeface = new Typeface(family, Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Italic) ? FontStyles.Italic : template.FontStyle, weight, template.FontStretch);
            FontRenderingEmSize = Math.Round((code ? NoteTypography.CodeFontSize : template.FontSize) * zoom, 1);
            Brush Resource(string key, Brush fallback) => template.TryFindResource(key) as Brush ?? fallback;
            ForegroundBrush = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Syntax) ? Theme.SyntaxFadeBrush
                : Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Weak) ? Resource("WeakTextBrushKey", template.Foreground)
                : link ? Resource("LinkBrushKey", template.Foreground) : template.Foreground;
            BackgroundBrush = code ? Resource("HoverBrushKey", Brushes.Transparent) : template.Background;
            var decorations = new TextDecorationCollection();
            if (template.TextDecorations != null) foreach (var item in template.TextDecorations) decorations.Add(item);
            if (Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Strike)) foreach (var item in System.Windows.TextDecorations.Strikethrough) decorations.Add(item);
            if (link || Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Underline)) foreach (var item in System.Windows.TextDecorations.Underline) decorations.Add(item);
            TextDecorations = decorations.Count == 0 ? null : decorations;
        }
    }
}
