using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal readonly record struct MarkdownPreviewArtifactLink(
    Rect Bounds,
    string Target);

// The speculative product is immutable and has no visual-tree ownership. A demand hit creates
// one lightweight drawing surface; no TextBlock/Border/Button tree is retained by the cache.
internal sealed record MarkdownPreviewArtifact(
    DrawingGroup Drawing,
    double LayoutWidth,
    double ContentHeight,
    bool Truncated,
    IReadOnlyList<MarkdownPreviewArtifactLink> Links);

// The cache owns only drawings. Each demand owns this small surface and native link buttons;
// WPF, rather than another hand-written state machine, owns focus, capture and release semantics.
internal sealed class MarkdownPreviewArtifactSurface : Panel
{
    static MarkdownPreviewArtifactSurface() => ClipProperty.OverrideMetadata(
        typeof(MarkdownPreviewArtifactSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange));

    internal MarkdownPreviewArtifact Artifact { get; }

    internal MarkdownPreviewArtifactSurface(MarkdownPreviewArtifact artifact, Action<string> openExternal)
    {
        if (!artifact.Drawing.IsFrozen)
            throw new ArgumentException("Markdown preview artifacts must be frozen.", nameof(artifact));
        Artifact = artifact;
        NoteTypography.ApplyTextRendering(this);
        foreach (var link in artifact.Links)
            Children.Add(MarkdownPreviewLinkHit.Create(link.Target, link.Bounds.Size, openExternal));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        for (var i = 0; i < Children.Count; i++) Children[i].Measure(Artifact.Links[i].Bounds.Size);
        var width = double.IsFinite(availableSize.Width)
            ? Math.Min(Math.Max(0, availableSize.Width), Artifact.LayoutWidth)
            : Artifact.LayoutWidth;
        return new Size(width, Artifact.ContentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Clip?.Bounds ?? new Rect(finalSize);
        for (var i = 0; i < Children.Count; i++)
        {
            // Height-independent artifacts also contain rows below a smaller viewport. Those
            // links must not enter keyboard navigation while their text is clipped away.
            Children[i].IsEnabled = Artifact.Links[i].Bounds.IntersectsWith(visible);
            Children[i].Arrange(Artifact.Links[i].Bounds);
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawDrawing(Artifact.Drawing);
    }

    // Only the native link children consume input. Gaps/text keep the host's paper-open gesture.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // The whole card is capped at 410 DIPs. Preparing this much body is therefore sufficient for
    // every current card height and lets one artifact survive a smaller host-height constraint.
    internal const double ArtifactMaximumBodyHeight = 410;
    private const double ArtifactViewHorizontalInsets = 22; // view margins 19 + viewport margins 3

    internal static double ArtifactBodyWidth(EdgeCapsulePreviewSize cardSize) =>
        Math.Max(1, cardSize.ContentSize.Width - ArtifactViewHorizontalInsets);

    internal enum ArtifactBlockKind
    {
        Text,
        Quote,
        List,
        Rule,
        Image,
        Code,
        EmptyState
    }

    private enum ArtifactBaseStyle
    {
        Normal,
        Weak,
        Heading1,
        Heading2,
        Heading3,
        HeadingOther,
        CodeSource,
        CodeBlock,
        Image,
        Done,
        EmptyState
    }

    internal sealed record ArtifactBlock(
        ArtifactBlockKind Kind,
        IReadOnlyList<MarkdownLayoutPiece> Pieces,
        IReadOnlyList<MarkdownLayoutPiece>? Marker = null,
        IReadOnlyList<IReadOnlyList<MarkdownLayoutPiece>>? Rows = null,
        bool RowBackground = false,
        bool RuleTextVisible = true,
        bool RuleFullSpan = true);

    internal sealed record MarkdownPreviewArtifactPlan(
        double Width,
        double MaximumHeight,
        double PixelsPerDip,
        TextFormattingMode FormattingMode,
        IReadOnlyList<MarkdownRunStyle> Styles,
        IReadOnlyList<string> LinkTargets,
        IReadOnlyList<ArtifactBlock> Blocks,
        Brush HoverBrush,
        Pen BorderPen,
        double ListGap,
        bool SourceTruncated);

    internal abstract record ArtifactCommand;
    private sealed record DrawingCommand(Drawing Drawing, double X, double Y) : ArtifactCommand;
    private sealed record RectangleCommand(Brush Brush, Rect Bounds, double Radius) : ArtifactCommand;
    private sealed record LineCommand(Pen Pen, Point Start, Point End) : ArtifactCommand;

    internal sealed record MarkdownPreviewArtifactDraft(
        double Width,
        double ContentHeight,
        bool Truncated,
        IReadOnlyList<ArtifactCommand> Commands,
        IReadOnlyList<MarkdownPreviewArtifactLink> Links);

    private sealed class ArtifactStyleFactory
    {
        private readonly double _zoom;
        private readonly Brush _text;
        private readonly Brush _weak;
        private readonly Brush _link;
        internal readonly Brush Hover;
        internal readonly Brush Border;
        private readonly Brush _syntax;
        private readonly Dictionary<(ArtifactBaseStyle Base, InlineStyle Flags, bool Link, bool ScopedUnderline), int> _indices = new();
        private readonly List<MarkdownRunStyle> _styles = new();

        private sealed record BaseSpec(
            FontFamily Family,
            FontStyle Style,
            FontWeight Weight,
            FontStretch Stretch,
            double Size,
            Brush Foreground,
            Brush? Background,
            bool Strike);

        internal ArtifactStyleFactory(FrameworkElement surface, double zoom)
        {
            _zoom = zoom;
            T FreezeCopy<T>(T value) where T : Freezable
            {
                if (value.IsFrozen) return value;
                var copy = (T)value.CloneCurrentValue();
                if (!copy.CanFreeze) throw new InvalidOperationException("Preview resource cannot form a frozen artifact snapshot.");
                copy.Freeze();
                return copy;
            }
            Brush Resource(string key, Brush fallback) =>
                FreezeCopy(surface.TryFindResource(key) as Brush ?? fallback);
            _text = Resource("TextBrushKey", Brushes.Black);
            _weak = Resource("WeakTextBrushKey", _text);
            _link = Resource("LinkBrushKey", _text);
            Hover = Resource("HoverBrushKey", Brushes.Transparent);
            Border = Resource("PaperBorderBrushKey", _weak);
            _syntax = FreezeCopy(Theme.SyntaxFadeBrush);
            _ = Style(ArtifactBaseStyle.Normal, InlineStyle.None, link: false); // request default slot
        }

        internal IReadOnlyList<MarkdownRunStyle> Snapshot() =>
            Array.AsReadOnly(_styles.ToArray());

        private BaseSpec Base(ArtifactBaseStyle kind)
        {
            var normalFamily = NoteTypography.FontFamily;
            var normalStyle = NoteTypography.FontStyle;
            var normalWeight = NoteTypography.FontWeight;
            var normalStretch = NoteTypography.FontStretch;
            var strongFamily = AppTypography.FontFamilyFor(content: true, bold: true);
            var strongWeight = AppTypography.UsesCustomBoldFace(true)
                ? AppTypography.FontWeightFor(true)
                : NoteTypography.HeadingFontWeight;
            double Scale(double size) => Math.Round(size * _zoom, 1);
            return kind switch
            {
                ArtifactBaseStyle.Weak => new(normalFamily, normalStyle, normalWeight, normalStretch,
                    Scale(NoteTypography.FontSize), _weak, null, false),
                ArtifactBaseStyle.Heading1 => new(strongFamily, normalStyle, strongWeight, normalStretch,
                    Scale(NoteTypography.Heading1FontSize), _text, null, false),
                ArtifactBaseStyle.Heading2 => new(strongFamily, normalStyle, strongWeight, normalStretch,
                    Scale(NoteTypography.Heading2FontSize), _text, null, false),
                ArtifactBaseStyle.Heading3 => new(strongFamily, normalStyle, strongWeight, normalStretch,
                    Scale(NoteTypography.Heading3FontSize), _text, null, false),
                ArtifactBaseStyle.HeadingOther => new(strongFamily, normalStyle, strongWeight, normalStretch,
                    Scale(NoteTypography.FontSize), _text, null, false),
                ArtifactBaseStyle.CodeSource => new(NoteTypography.CodeFontFamily, normalStyle, normalWeight, normalStretch,
                    Scale(NoteTypography.CodeFontSize), _text, Hover, false),
                ArtifactBaseStyle.CodeBlock => new(NoteTypography.CodeFontFamily, normalStyle, normalWeight, normalStretch,
                    Scale(NoteTypography.CodeFontSize), _text, null, false),
                ArtifactBaseStyle.Image => new(normalFamily, normalStyle, normalWeight, normalStretch,
                    Scale(AppTypography.Scale(11.5)), _weak, null, false),
                ArtifactBaseStyle.Done => new(normalFamily, normalStyle, normalWeight, normalStretch,
                    Scale(NoteTypography.FontSize), _weak, null, true),
                ArtifactBaseStyle.EmptyState => new(normalFamily, normalStyle, normalWeight, normalStretch,
                    AppTypography.Scale(16), _weak, null, false),
                _ => new(normalFamily, normalStyle, normalWeight, normalStretch,
                    Scale(NoteTypography.FontSize), _text, null, false)
            };
        }

        internal int Style(ArtifactBaseStyle @base, InlineStyle flags, bool link, bool scopedUnderline = false)
        {
            scopedUnderline &= link || (flags & InlineStyle.Underline) != 0;
            var key = (@base, flags, link, scopedUnderline);
            if (_indices.TryGetValue(key, out var existing)) return existing;
            bool Has(InlineStyle flag) => (flags & flag) != 0;
            var spec = Base(@base);
            var strong = Has(InlineStyle.Strong);
            var code = Has(InlineStyle.Code);
            var family = code
                ? NoteTypography.CodeFontFamily
                : strong ? AppTypography.FontFamilyFor(content: true, bold: true) : spec.Family;
            var weight = strong
                ? AppTypography.UsesCustomBoldFace(true) ? AppTypography.FontWeightFor(true) : NoteTypography.HeadingFontWeight
                : spec.Weight;
            var style = Has(InlineStyle.Italic) ? FontStyles.Italic : spec.Style;
            var size = code ? Math.Round(NoteTypography.CodeFontSize * _zoom, 1) : spec.Size;
            var foreground = Has(InlineStyle.Syntax) ? _syntax
                : Has(InlineStyle.Weak) ? _weak
                : link ? _link : spec.Foreground;
            var background = code ? Hover : spec.Background;

            TextDecorationCollection? decorations = null;
            var strike = spec.Strike || Has(InlineStyle.Strike);
            var underline = link || Has(InlineStyle.Underline);
            if (strike || underline)
            {
                var collection = new TextDecorationCollection();
                void Add(TextDecorationCollection source)
                {
                    foreach (var decoration in source)
                    {
                        var copy = (TextDecoration)decoration.CloneCurrentValue();
                        // Short cold rows carry underlines on a Hyperlink/Span. Preserve their
                        // full-formatting decoration metrics, rather than WPF's simplified ASCII
                        // underline rounding. The ordinary paragraph path stays unchanged.
                        if (scopedUnderline && copy.Location == TextDecorationLocation.Underline)
                        {
                            // Syntax fades the child glyph, not the enclosing underline.
                            copy.Pen = new Pen(link ? _link : spec.Foreground, 1);
                            copy.Pen.Freeze();
                        }
                        collection.Add(copy);
                    }
                }
                if (strike) Add(TextDecorations.Strikethrough);
                if (underline) Add(TextDecorations.Underline);
                collection.Freeze();
                decorations = collection;
            }

            var index = _styles.Count;
            _styles.Add(new MarkdownRunStyle(
                family.Source,
                family.BaseUri,
                style,
                weight,
                spec.Stretch,
                size,
                NoteTypography.Language.GetEquivalentCulture().Name,
                foreground,
                background,
                decorations));
            _indices.Add(key, index);
            return index;
        }
    }

    internal static string TypographyStamp(FrameworkElement surface) =>
        string.Join("|", NoteTypography.FontFamily.Source, NoteTypography.FontFamily.BaseUri,
            NoteTypography.CodeFontFamily.Source, NoteTypography.CodeFontFamily.BaseUri,
            AppTypography.FontFamilyFor(content: true, bold: true).Source,
            AppTypography.FontFamilyFor(content: true, bold: true).BaseUri,
            AppTypography.FontWeightFor(true), AppTypography.UsesCustomBoldFace(true),
            NoteTypography.FontWeight, NoteTypography.FontStyle, NoteTypography.FontStretch,
            NoteTypography.Language.IetfLanguageTag, NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,
            NoteTypography.FontSize, NoteTypography.CodeFontSize,
            NoteTypography.Heading1FontSize, NoteTypography.Heading2FontSize, NoteTypography.Heading3FontSize,
            AppTypography.Scale(1), surface.Language.IetfLanguageTag, surface.FlowDirection,
            TextOptions.GetTextRenderingMode(surface), TextOptions.GetTextHintingMode(surface));

    // Preserve the established underline/background metrics on old short versus long rows.
    // This is typography compatibility only: both sizes now use the same artifact builder.
    private const int LegacyParagraphLength = 256;
    private static bool UsesInlineDecorationScope(string text, string mode, PreviewInlineCache cache) =>
        text.Length < LegacyParagraphLength &&
        (text.Length < 96 || mode == MarkdownRenderModes.Off || cache.Get(text, mode).Pieces.Count < 24);

    internal static async Task<MarkdownPreviewArtifact?> PrepareArtifactAsync(
        FrameworkElement owner, PreviewContent content, Size viewport, double zoom,
        bool speculative, CancellationToken cancellation)
    {
        owner.Dispatcher.VerifyAccess();
        cancellation.ThrowIfCancellationRequested();
        object?[] Appearance() => new object?[]
        {
            owner.TryFindResource("TextBrushKey"), owner.TryFindResource("WeakTextBrushKey"),
            owner.TryFindResource("LinkBrushKey"), owner.TryFindResource("HoverBrushKey"),
            owner.TryFindResource("PaperBorderBrushKey"), Theme.SyntaxFadeBrush,
            VisualTreeHelper.GetDpi(owner), TypographyStamp(owner)
        };
        var appearance = Appearance();
        var plan = CaptureArtifactPlan(owner, content, viewport.Width, zoom) with { MaximumHeight = viewport.Height };
        var changed = false;
        var observed = appearance.OfType<Freezable>().Where(value => !value.IsFrozen).Distinct().ToArray();
        EventHandler handler = (_, _) => changed = true;
        foreach (var resource in observed) resource.Changed += handler;
        try
        {
            var draft = await BuildArtifactDraftAsync(plan, speculative, cancellation).ConfigureAwait(false);
            return await owner.Dispatcher.InvokeAsync(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                // Only reread the small appearance stamp, never rebuild semantic/style inputs.
                return changed || !appearance.SequenceEqual(Appearance()) ? null : ComposeArtifact(draft);
            }, speculative ? DispatcherPriority.ContextIdle : DispatcherPriority.Background);
        }
        finally
        {
            await owner.Dispatcher.InvokeAsync(() =>
            {
                foreach (var resource in observed) resource.Changed -= handler;
            }, DispatcherPriority.Send);
        }
    }

    internal static MarkdownPreviewArtifactPlan CaptureArtifactPlan(
        FrameworkElement surface,
        PreviewContent content,
        double width,
        double textZoom)
    {
        surface.Dispatcher.VerifyAccess();
        width = Math.Max(1, width);
        var zoom = NormalizeTextZoom(textZoom);
        var styles = new ArtifactStyleFactory(surface, zoom);
        var linkTargets = new List<string>();
        var linkIndices = new Dictionary<Uri, int>(ReferenceEqualityComparer.Instance);
        var blocks = new List<ArtifactBlock>();

        int LinkIndex(Uri? uri)
        {
            if (uri == null) return -1;
            if (linkIndices.TryGetValue(uri, out var index)) return index;
            index = linkTargets.Count;
            linkIndices.Add(uri, index);
            linkTargets.Add(uri.AbsoluteUri);
            return index;
        }

        IReadOnlyList<MarkdownLayoutPiece> Raw(
            string text,
            ArtifactBaseStyle @base,
            InlineStyle flags = InlineStyle.None)
        {
            // A blank TextBlock still reserves a natural text line. Keep the same metrics
            // without painting a glyph, including blank source rows inside a fence.
            if (text.Length == 0) text = "\u200B";
            return Array.AsReadOnly(new[]
            {
                new MarkdownLayoutPiece(text, styles.Style(@base, flags, link: false), -1)
            });
        }

        IReadOnlyList<MarkdownLayoutPiece> Inline(
            string text,
            string mode,
            ArtifactBaseStyle @base)
        {
            var values = new List<MarkdownLayoutPiece>();
            var scopedUnderline = UsesInlineDecorationScope(text, mode, content.Inlines);
            foreach (var piece in content.Inlines.Get(text, mode).Pieces)
            {
                if (piece.Text.Length == 0) continue;
                var link = LinkIndex(piece.Link);
                values.Add(new MarkdownLayoutPiece(
                    piece.Text,
                    styles.Style(@base, piece.Style, link >= 0, scopedUnderline),
                    link));
            }
            return values.Count == 0 ? Raw(string.Empty, @base) : Array.AsReadOnly(values.ToArray());
        }

        IReadOnlyList<MarkdownLayoutPiece> PrefixAndInline(
            string prefix,
            InlineStyle prefixStyle,
            string body,
            string mode,
            ArtifactBaseStyle @base)
        {
            var values = new List<MarkdownLayoutPiece>();
            if (prefix.Length > 0)
                values.Add(new(prefix, styles.Style(@base, prefixStyle, link: false), -1));
            values.AddRange(Inline(body, mode, @base));
            return Array.AsReadOnly(values.ToArray());
        }

        ArtifactBaseStyle HeadingBase(int level) => level switch
        {
            1 => ArtifactBaseStyle.Heading1,
            2 => ArtifactBaseStyle.Heading2,
            3 => ArtifactBaseStyle.Heading3,
            _ => ArtifactBaseStyle.HeadingOther
        };

        void AddSourceBlock(ContentLine previewLine)
        {
            var line = previewLine.Text;
            if (content.RenderMode == MarkdownRenderModes.Off)
            {
                blocks.Add(new(ArtifactBlockKind.Text, Raw(line, ArtifactBaseStyle.Normal)));
                return;
            }

            if (previewLine.WasInsideFence || previewLine.FenceKind == MarkdownFenceLineKind.Opening)
            {
                var syntax =
                    (previewLine.FenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing) &&
                    content.RenderMode == MarkdownRenderModes.Enhanced
                    ? InlineStyle.Syntax : InlineStyle.None;
                blocks.Add(new(ArtifactBlockKind.Text,
                    Raw(line, previewLine.FenceKind == MarkdownFenceLineKind.None &&
                        line.Length >= LegacyParagraphLength
                            ? ArtifactBaseStyle.CodeSource : ArtifactBaseStyle.CodeBlock, syntax),
                    RowBackground: true));
                return;
            }

            var trimmed = line.TrimStart();
            var prefixLength = line.Length - trimmed.Length;
            var prefixStyle = content.RenderMode == MarkdownRenderModes.Enhanced
                ? InlineStyle.Syntax : InlineStyle.None;
            var baseStyle = ArtifactBaseStyle.Normal;
            string? renderedPrefix = null;
            var heading = HeadingPattern.Match(trimmed);
            var task = TaskListPattern.Match(trimmed);
            var ordered = OrderedListPattern.Match(trimmed);
            var unordered = UnorderedListPattern.Match(trimmed);
            if (heading.Success)
            {
                baseStyle = HeadingBase(heading.Groups[1].Value.Length);
                prefixLength += heading.Groups[2].Index;
            }
            else if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                baseStyle = ArtifactBaseStyle.Weak;
                prefixLength++;
            }
            else if (task.Success || ordered.Success)
            {
                prefixLength += (task.Success ? task : ordered).Groups[2].Index;
                prefixStyle = InlineStyle.None;
            }
            else if (unordered.Success)
            {
                var markerStart = prefixLength;
                prefixLength += unordered.Groups[1].Index;
                if (content.RenderMode == MarkdownRenderModes.Enhanced)
                {
                    renderedPrefix = line[..markerStart] + "•" + line[(markerStart + 1)..prefixLength];
                    prefixStyle = InlineStyle.None;
                }
            }
            else if (HorizontalRulePattern.IsMatch(trimmed))
            {
                if (content.RenderMode == MarkdownRenderModes.Enhanced)
                    blocks.Add(new(ArtifactBlockKind.Rule,
                        Raw("\u200B", ArtifactBaseStyle.Normal), RuleTextVisible: false, RuleFullSpan: true));
                else
                    blocks.Add(new(ArtifactBlockKind.Rule,
                        Raw(line, ArtifactBaseStyle.Normal), RuleTextVisible: true, RuleFullSpan: false));
                return;
            }

            blocks.Add(new(ArtifactBlockKind.Text, line.Length == 0
                ? Raw(string.Empty, baseStyle)
                : PrefixAndInline(renderedPrefix ?? line[..prefixLength], prefixStyle,
                    line[prefixLength..], content.RenderMode, baseStyle)));
        }

        void AddFullBlock(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                blocks.Add(new(ArtifactBlockKind.Text, Raw(string.Empty, ArtifactBaseStyle.Normal)));
                return;
            }
            if (HorizontalRulePattern.IsMatch(trimmed))
            {
                blocks.Add(new(ArtifactBlockKind.Rule,
                    Raw("\u200B", ArtifactBaseStyle.Normal), RuleTextVisible: false, RuleFullSpan: true));
                return;
            }
            if (MarkdownImageReferences.TryParseReferenceLine(trimmed, out var imageReference))
            {
                var label = imageReference.Label;
                var text = string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}";
                blocks.Add(new(ArtifactBlockKind.Image, Raw(text, ArtifactBaseStyle.Image)));
                return;
            }
            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                blocks.Add(new(ArtifactBlockKind.Text,
                    Inline(heading.Groups[2].Value, MarkdownRenderModes.Full,
                        HeadingBase(heading.Groups[1].Value.Length))));
                return;
            }
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                blocks.Add(new(ArtifactBlockKind.Quote,
                    Inline(trimmed[1..].TrimStart(), MarkdownRenderModes.Full, ArtifactBaseStyle.Weak)));
                return;
            }
            var task = TaskListPattern.Match(trimmed);
            if (task.Success)
            {
                var done = !string.Equals(task.Groups[1].Value, " ", StringComparison.Ordinal);
                blocks.Add(new(ArtifactBlockKind.List,
                    Inline(task.Groups[2].Value, MarkdownRenderModes.Full,
                        done ? ArtifactBaseStyle.Done : ArtifactBaseStyle.Normal),
                    Raw(done ? "☑" : "☐", ArtifactBaseStyle.Weak)));
                return;
            }
            var ordered = OrderedListPattern.Match(trimmed);
            if (ordered.Success)
            {
                blocks.Add(new(ArtifactBlockKind.List,
                    Inline(ordered.Groups[2].Value, MarkdownRenderModes.Full, ArtifactBaseStyle.Normal),
                    Raw($"{ordered.Groups[1].Value}.", ArtifactBaseStyle.Weak)));
                return;
            }
            var unordered = UnorderedListPattern.Match(trimmed);
            if (unordered.Success)
            {
                blocks.Add(new(ArtifactBlockKind.List,
                    Inline(unordered.Groups[1].Value, MarkdownRenderModes.Full, ArtifactBaseStyle.Normal),
                    Raw("•", ArtifactBaseStyle.Weak)));
                return;
            }
            blocks.Add(new(ArtifactBlockKind.Text,
                Inline(trimmed, MarkdownRenderModes.Full, ArtifactBaseStyle.Normal)));
        }

        if (content.IsEmpty)
        {
            blocks.Add(new(ArtifactBlockKind.EmptyState, Raw("—", ArtifactBaseStyle.EmptyState)));
        }
        else if (content.RenderMode != MarkdownRenderModes.Full)
        {
            foreach (var line in content.Lines) AddSourceBlock(line);
        }
        else
        {
            List<IReadOnlyList<MarkdownLayoutPiece>>? codeRows = null;
            void FinishCode()
            {
                if (codeRows == null) return;
                if (codeRows.Count == 0)
                    codeRows.Add(Raw("\u200B", ArtifactBaseStyle.CodeBlock));
                blocks.Add(new(ArtifactBlockKind.Code, Array.Empty<MarkdownLayoutPiece>(),
                    Rows: Array.AsReadOnly(codeRows.ToArray())));
                codeRows = null;
            }
            foreach (var previewLine in content.Lines)
            {
                var line = previewLine.Text.TrimEnd();
                if (previewLine.FenceKind == MarkdownFenceLineKind.Opening)
                {
                    FinishCode();
                    codeRows = new();
                }
                else if (previewLine.FenceKind == MarkdownFenceLineKind.Closing)
                {
                    FinishCode();
                }
                else if (previewLine.WasInsideFence)
                {
                    codeRows ??= new();
                    codeRows.Add(line.Length == 0
                        ? Raw("\u200B", ArtifactBaseStyle.CodeBlock)
                        : Raw(line, ArtifactBaseStyle.CodeBlock));
                }
                else
                {
                    FinishCode();
                    AddFullBlock(line);
                }
            }
            FinishCode();
        }

        var pen = new Pen(styles.Border, 1);
        pen.Freeze();
        return new MarkdownPreviewArtifactPlan(
            width,
            ArtifactMaximumBodyHeight,
            VisualTreeHelper.GetDpi(surface).PixelsPerDip,
            AppTypography.TextFormattingMode,
            styles.Snapshot(),
            Array.AsReadOnly(linkTargets.ToArray()),
            Array.AsReadOnly(blocks.ToArray()),
            styles.Hover,
            pen,
            AppTypography.Scale(6),
            content.Truncated);
    }

    internal static async Task<MarkdownPreviewArtifactDraft> BuildArtifactDraftAsync(
        MarkdownPreviewArtifactPlan plan,
        bool speculative,
        CancellationToken cancellation)
    {
        var commands = new List<ArtifactCommand>();
        var links = new List<MarkdownPreviewArtifactLink>();
        var y = 0.0;
        var truncated = false;

        async Task<MarkdownParagraphResult?> Layout(
            IReadOnlyList<MarkdownLayoutPiece> pieces,
            double width,
            double remaining)
        {
            if (pieces.Count == 0) return null;
            var request = new MarkdownParagraphRequest(
                pieces,
                plan.Styles,
                plan.LinkTargets,
                new Size(Math.Max(1, width), Math.Max(0, remaining)),
                plan.PixelsPerDip,
                plan.FormattingMode);
            return await MarkdownLayoutWorker.Shared.PrepareAsync(
                request, speculative, cancellation).ConfigureAwait(false);
        }

        void AddResult(MarkdownParagraphResult? result, double x, double top, bool draw = true)
        {
            if (result == null) return;
            if (draw) commands.Add(new DrawingCommand(result.Drawing, x, top));
            foreach (var hit in result.Links)
            {
                if (hit.LinkIndex < 0 || hit.LinkIndex >= plan.LinkTargets.Count) continue;
                var bounds = hit.Bounds;
                bounds.Offset(x, top);
                links.Add(new(bounds, plan.LinkTargets[hit.LinkIndex]));
            }
        }

        double Remaining(double top, double reserve = 0) =>
            Math.Max(0, plan.MaximumHeight - top - reserve);

        foreach (var block in plan.Blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (y > plan.MaximumHeight)
            {
                truncated = true;
                break;
            }

            switch (block.Kind)
            {
                case ArtifactBlockKind.Text:
                {
                    var result = await Layout(block.Pieces, plan.Width, Remaining(y)).ConfigureAwait(false);
                    if (result != null)
                    {
                        if (block.RowBackground)
                            commands.Add(new RectangleCommand(plan.HoverBrush,
                                new Rect(0, y, plan.Width, result.Size.Height), 0));
                        AddResult(result, 0, y);
                        y += result.Size.Height;
                        truncated |= result.Truncated;
                    }
                    break;
                }
                case ArtifactBlockKind.Quote:
                {
                    const double left = 12;
                    const double right = 5;
                    var result = await Layout(block.Pieces,
                        Math.Max(1, plan.Width - left - right), Remaining(y)).ConfigureAwait(false);
                    AddResult(result, left, y);
                    if (result != null)
                    {
                        y += result.Size.Height;
                        truncated |= result.Truncated;
                    }
                    break;
                }
                case ArtifactBlockKind.List:
                {
                    const double left = 2;
                    var marker = await Layout(block.Marker ?? Array.Empty<MarkdownLayoutPiece>(),
                        Math.Max(1, plan.Width - left), Remaining(y)).ConfigureAwait(false);
                    var markerWidth = marker == null ? 0 : Math.Min(marker.ContentWidth, Math.Max(0, plan.Width - left));
                    var bodyX = Math.Min(plan.Width, left + markerWidth + plan.ListGap);
                    var body = await Layout(block.Pieces,
                        Math.Max(1, plan.Width - bodyX), Remaining(y)).ConfigureAwait(false);
                    AddResult(marker, left, y);
                    AddResult(body, bodyX, y);
                    var height = Math.Max(marker?.Size.Height ?? 0, body?.Size.Height ?? 0);
                    y += height;
                    truncated |= marker?.Truncated == true || body?.Truncated == true;
                    break;
                }
                case ArtifactBlockKind.Rule:
                {
                    var result = await Layout(block.Pieces, plan.Width, Remaining(y)).ConfigureAwait(false);
                    AddResult(result, 0, y, block.RuleTextVisible);
                    var height = result?.Size.Height ?? 0;
                    var start = block.RuleFullSpan
                        ? 2
                        : Math.Min(plan.Width - 2, (result?.ContentWidth ?? 0) + 8);
                    var end = Math.Max(start, plan.Width - 2);
                    commands.Add(new LineCommand(plan.BorderPen,
                        new Point(start, y + height / 2), new Point(end, y + height / 2)));
                    y += height;
                    truncated |= result?.Truncated == true;
                    break;
                }
                case ArtifactBlockKind.Image:
                {
                    const double marginX = 1;
                    const double marginY = 4;
                    const double paddingX = 8;
                    const double paddingY = 7;
                    var innerWidth = Math.Max(1, plan.Width - marginX * 2 - paddingX * 2);
                    var result = await Layout(block.Pieces, innerWidth,
                        Remaining(y, marginY * 2 + paddingY * 2)).ConfigureAwait(false);
                    var textHeight = result?.Size.Height ?? 0;
                    commands.Add(new RectangleCommand(plan.HoverBrush,
                        new Rect(marginX, y + marginY,
                            Math.Max(0, plan.Width - marginX * 2), textHeight + paddingY * 2), 5));
                    AddResult(result, marginX + paddingX, y + marginY + paddingY);
                    y += marginY * 2 + paddingY * 2 + textHeight;
                    truncated |= result?.Truncated == true;
                    break;
                }
                case ArtifactBlockKind.Code:
                {
                    var startY = y;
                    var insert = commands.Count;
                    foreach (var row in block.Rows ?? Array.Empty<IReadOnlyList<MarkdownLayoutPiece>>())
                    {
                        if (y > plan.MaximumHeight)
                        {
                            truncated = true;
                            break;
                        }
                        var result = await Layout(row, plan.Width, Remaining(y)).ConfigureAwait(false);
                        AddResult(result, 0, y);
                        if (result != null)
                        {
                            y += result.Size.Height;
                            if (result.Truncated)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                    commands.Insert(insert, new RectangleCommand(plan.HoverBrush,
                        new Rect(0, startY, plan.Width, Math.Max(0, y - startY)), 0));
                    break;
                }
                case ArtifactBlockKind.EmptyState:
                {
                    const double side = 4;
                    const double top = 18;
                    const double bottom = 4;
                    var result = await Layout(block.Pieces,
                        Math.Max(1, plan.Width - side * 2), Remaining(y, top + bottom)).ConfigureAwait(false);
                    var textWidth = result?.ContentWidth ?? 0;
                    var x = Math.Max(side, (plan.Width - textWidth) / 2);
                    AddResult(result, x, y + top);
                    y += top + (result?.Size.Height ?? 0) + bottom;
                    truncated |= result?.Truncated == true;
                    break;
                }
            }

            if (truncated) break;
        }

        return new MarkdownPreviewArtifactDraft(
            plan.Width,
            y,
            truncated || plan.SourceTruncated,
            Array.AsReadOnly(commands.ToArray()),
            Array.AsReadOnly(links.ToArray()));
    }

    internal static MarkdownPreviewArtifact ComposeArtifact(MarkdownPreviewArtifactDraft draft)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            foreach (var command in draft.Commands)
            {
                switch (command)
                {
                    case DrawingCommand drawing:
                        context.PushTransform(new TranslateTransform(drawing.X, drawing.Y));
                        context.DrawDrawing(drawing.Drawing);
                        context.Pop();
                        break;
                    case RectangleCommand rectangle:
                        context.DrawRoundedRectangle(rectangle.Brush, null, rectangle.Bounds,
                            rectangle.Radius, rectangle.Radius);
                        break;
                    case LineCommand line:
                        context.DrawLine(line.Pen, line.Start, line.End);
                        break;
                }
            }
        }
        if (!group.CanFreeze) throw new InvalidOperationException("Whole-preview drawing cannot be frozen.");
        group.Freeze();
        return new MarkdownPreviewArtifact(
            group,
            draft.Width,
            draft.ContentHeight,
            draft.Truncated,
            draft.Links);
    }
}
