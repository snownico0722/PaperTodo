using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace PaperTodo;

// A bounded preview paragraph, not an editor document or a second presentation owner.
// Only values and frozen Freezables cross the worker boundary. No UI/cache callbacks live here.
internal sealed record MarkdownRunStyle(
    string FontFamily, Uri? FontBaseUri, FontStyle FontStyle, FontWeight FontWeight,
    FontStretch FontStretch, double FontSize, string Culture,
    Brush Foreground, Brush? Background, TextDecorationCollection? Decorations);
internal readonly record struct MarkdownLayoutPiece(string Text, int StyleIndex, int LinkIndex);
internal readonly record struct MarkdownLinkHit(Rect Bounds, int LinkIndex);
internal sealed record MarkdownParagraphResult(
    DrawingGroup Drawing, Size Size, string VisibleText, int FormattedLines, bool Truncated,
    IReadOnlyList<MarkdownLinkHit> Links, double ContentWidth, int FormattingThreadId);

internal sealed class MarkdownParagraphRequest : IEquatable<MarkdownParagraphRequest>
{
    internal IReadOnlyList<MarkdownLayoutPiece> Pieces { get; }
    internal IReadOnlyList<MarkdownRunStyle> Styles { get; }
    internal IReadOnlyList<string> LinkTargets { get; }
    internal Size Viewport { get; }
    internal double PixelsPerDip { get; }
    internal TextFormattingMode FormattingMode { get; }
    private readonly int _hash;

    internal MarkdownParagraphRequest(IEnumerable<MarkdownLayoutPiece> pieces,
        IEnumerable<MarkdownRunStyle> styles, IEnumerable<string> links,
        Size viewport, double pixelsPerDip, TextFormattingMode formattingMode)
    {
        Pieces = Array.AsReadOnly(pieces.ToArray());
        Styles = Array.AsReadOnly(styles.ToArray());
        LinkTargets = Array.AsReadOnly(links.ToArray());
        Viewport = viewport; PixelsPerDip = pixelsPerDip; FormattingMode = formattingMode;
        if (Styles.Count == 0 || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height < 0 || !double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0)
            throw new ArgumentException("A paragraph needs finite bounded geometry and a default style.");
        foreach (var style in Styles)
            if (!style.Foreground.IsFrozen || style.Background?.IsFrozen == false || style.Decorations?.IsFrozen == false)
                throw new ArgumentException("Layout resources must be frozen snapshots.");
        foreach (var piece in Pieces)
            if (piece.StyleIndex < 0 || piece.StyleIndex >= Styles.Count || piece.LinkIndex < -1 || piece.LinkIndex >= LinkTargets.Count)
                throw new ArgumentException("Invalid paragraph piece index.");
        var hash = new HashCode(); hash.Add(Viewport); hash.Add(PixelsPerDip); hash.Add(FormattingMode);
        foreach (var piece in Pieces) hash.Add(piece);
        foreach (var link in LinkTargets) hash.Add(link);
        // Keep the cheap hash aligned with the fields used by deep equality. Resource drawings are
        // still compared conservatively below; typography alone removes the common cross-style collisions.
        foreach (var style in Styles)
        {
            hash.Add(style.FontFamily); hash.Add(style.FontBaseUri); hash.Add(style.FontStyle);
            hash.Add(style.FontWeight); hash.Add(style.FontStretch); hash.Add(style.FontSize); hash.Add(style.Culture);
        }
        _hash = hash.ToHashCode();
    }

    public override int GetHashCode() => _hash;
    public override bool Equals(object? other) => other is MarkdownParagraphRequest request && Equals(request);
    public bool Equals(MarkdownParagraphRequest? other) => ReferenceEquals(this, other) ||
        (other != null && _hash == other._hash && Viewport == other.Viewport && PixelsPerDip == other.PixelsPerDip &&
         FormattingMode == other.FormattingMode && Pieces.SequenceEqual(other.Pieces) &&
         LinkTargets.SequenceEqual(other.LinkTargets) && Styles.Count == other.Styles.Count &&
         Styles.Zip(other.Styles).All(pair => SameStyle(pair.First, pair.Second)));

    private static bool SameStyle(MarkdownRunStyle a, MarkdownRunStyle b) =>
        a.FontFamily == b.FontFamily && a.FontBaseUri == b.FontBaseUri && a.FontStyle == b.FontStyle &&
        a.FontWeight == b.FontWeight && a.FontStretch == b.FontStretch && a.FontSize == b.FontSize &&
        a.Culture == b.Culture && SameBrush(a.Foreground, b.Foreground) && SameBrush(a.Background, b.Background) &&
        SameDecorations(a.Decorations, b.Decorations);

    // Conservative identity for uncommon brush kinds: missing a coalescing opportunity is safe;
    // treating two different drawings as the same request is not. Solid theme brushes use values.
    private static bool SameBrush(Brush? a, Brush? b) => ReferenceEquals(a, b) ||
        (a is SolidColorBrush x && b is SolidColorBrush y && x.Color == y.Color && x.Opacity == y.Opacity &&
         x.Transform.Value == y.Transform.Value && x.RelativeTransform.Value == y.RelativeTransform.Value);
    private static bool SameDecorations(TextDecorationCollection? a, TextDecorationCollection? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Location != b[i].Location || a[i].PenOffset != b[i].PenOffset ||
                a[i].PenOffsetUnit != b[i].PenOffsetUnit || a[i].PenThicknessUnit != b[i].PenThicknessUnit ||
                !ReferenceEquals(a[i].Pen, b[i].Pen)) return false;
        return true;
    }
}

internal static class MarkdownParagraphLayout
{
    // The worker retains the iterator on its STA. Each yield is a safe cancellation/preemption
    // boundary; TextLine/TextLineBreak/formatter never escape to the UI or another worker.
    internal static IEnumerable<MarkdownParagraphResult?> Prepare(MarkdownParagraphRequest request)
    {
        var source = new ParagraphSource(request);
        using var formatter = TextFormatter.Create(request.FormattingMode);
        var runCache = new TextRunCache();
        var paragraph = new ParagraphProperties(source.DefaultProperties);
        var drawing = new DrawingGroup();
        var links = new List<MarkdownLinkHit>();
        TextLineBreak? previous = null;
        var offset = 0; var height = 0.0; var contentWidth = 0.0; var nextLink = 0; var lines = 0;
        var width = request.Viewport.Width;
        try
        {
            while (offset < source.Text.Length && height <= request.Viewport.Height)
            {
                using var line = formatter.FormatLine(source, offset, width, paragraph, previous, runCache);
                previous?.Dispose(); previous = line.GetTextLineBreak();
                var lineDrawing = new DrawingGroup();
                using (var context = lineDrawing.Open()) line.Draw(context, new Point(0, height), InvertAxes.None);
                // Inputs are frozen copies. Freezing this owned result cannot freeze a host brush.
                if (!lineDrawing.CanFreeze) throw new InvalidOperationException("Text drawing is not free-threaded.");
                lineDrawing.Freeze(); drawing.Children.Add(lineDrawing);
                contentWidth = Math.Max(contentWidth, line.WidthIncludingTrailingWhitespace);
                var end = Math.Min(source.Text.Length, offset + line.Length);
                if (end <= offset) throw new InvalidOperationException("TextFormatter made no progress.");
                while (nextLink < source.Links.Count && source.Links[nextLink].End <= offset) nextLink++;
                for (var i = nextLink; i < source.Links.Count && source.Links[i].Start < end; i++)
                {
                    var range = source.Links[i];
                    var start = Math.Max(offset, range.Start); var stop = Math.Min(end, range.End);
                    if (stop <= start) continue;
                    foreach (var bounds in line.GetTextBounds(start, stop - start))
                    {
                        var rect = bounds.Rectangle; rect.Offset(0, height);
                        links.Add(new(rect, range.LinkIndex));
                    }
                }
                height += line.Height; offset = end; lines++;
                yield return null;
            }
        }
        finally { previous?.Dispose(); }
        drawing.Freeze();
        yield return new(drawing, new(width, height), source.Text[..offset], lines,
            offset < source.Text.Length, Array.AsReadOnly(links.ToArray()), contentWidth,
            Environment.CurrentManagedThreadId);
    }

    private sealed class ParagraphSource : TextSource
    {
        private readonly (int Start, int End, RunProperties Properties)[] _runs;
        internal readonly record struct LinkRange(int Start, int End, int LinkIndex);
        internal string Text { get; }
        internal List<LinkRange> Links { get; } = new();
        internal RunProperties DefaultProperties { get; }
        internal ParagraphSource(MarkdownParagraphRequest request)
        {
            PixelsPerDip = request.PixelsPerDip;
            var styles = request.Styles.Select(style => new RunProperties(style) { PixelsPerDip = request.PixelsPerDip }).ToArray();
            DefaultProperties = styles[0];
            Text = string.Concat(request.Pieces.Select(piece => piece.Text));
            _runs = new (int, int, RunProperties)[request.Pieces.Count];
            var offset = 0;
            for (var i = 0; i < request.Pieces.Count; i++)
            {
                var piece = request.Pieces[i];
                _runs[i] = (offset, offset + piece.Text.Length, styles[piece.StyleIndex]);
                if (piece.LinkIndex >= 0)
                {
                    if (Links.Count > 0 && Links[^1].End == offset && Links[^1].LinkIndex == piece.LinkIndex)
                        Links[^1] = Links[^1] with { End = offset + piece.Text.Length };
                    else Links.Add(new(offset, offset + piece.Text.Length, piece.LinkIndex));
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
        internal RunProperties(MarkdownRunStyle style)
        {
            var family = style.FontBaseUri == null ? new FontFamily(style.FontFamily) : new FontFamily(style.FontBaseUri, style.FontFamily);
            Typeface = new(family, style.FontStyle, style.FontWeight, style.FontStretch);
            FontRenderingEmSize = style.FontSize;
            CultureInfo = System.Globalization.CultureInfo.GetCultureInfo(style.Culture);
            TextDecorations = style.Decorations;
            ForegroundBrush = style.Foreground; BackgroundBrush = style.Background;
        }
    }
}
