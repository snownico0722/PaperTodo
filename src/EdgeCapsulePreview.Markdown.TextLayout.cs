using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

// The UI element owns resources, input and publication. Heavy formatting uses immutable requests;
// the worker never sees this Canvas, its template, the mutable inline cache, or its callbacks.
internal sealed class MarkdownEdgePreviewParagraph : Canvas
{
    internal const int MinimumSourceLength = 256;
    [ThreadStatic] private static ControlTemplate? _linkHitTemplate;
    private static ControlTemplate LinkHitTemplate
    {
        get
        {
            if (_linkHitTemplate != null) return _linkHitTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            return _linkHitTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }
    private readonly TextBlock _template;
    private readonly string _source;
    private readonly string _mode;
    private readonly MarkdownEdgeCapsulePreviewRenderer.PreviewInlineCache _inlineCache;
    private readonly double _zoom;
    private readonly Action<string> _openExternal;
    private DrawingGroup _drawing = new();
    private Size _size;
    internal double MeasuredWidth { get; private set; }
    internal string VisibleText { get; private set; } = "";
    internal int FormattedLines { get; private set; }
    internal int FormattingThreadId { get; private set; }
    internal bool Truncated { get; private set; }

    internal MarkdownEdgePreviewParagraph(TextBlock template, string source, string mode,
        double zoom, Action<string> openExternal, MarkdownEdgeCapsulePreviewRenderer.PreviewInlineCache inlineCache)
    {
        _template = template; _source = source; _mode = mode; _zoom = zoom; _openExternal = openExternal;
        _inlineCache = inlineCache;
        Margin = template.Margin;
        template.Margin = new Thickness();
        template.Opacity = 0; template.IsHitTestVisible = false;
        Children.Add(template);
        NoteTypography.ApplyTextRendering(this);
    }

    // Explicit eager renderer/checks use the SAME kernel locally; the live viewport never calls
    // this path or blocks on a worker Task. It supplies a preparation context and awaits below.
    internal IEnumerable<bool> Prepare(Size viewport)
    {
        var snapshot = Capture(viewport);
        FormattedLines = 0;
        foreach (var result in MarkdownParagraphLayout.Prepare(snapshot.Request))
        {
            if (result != null) Apply(snapshot, result);
            else FormattedLines++; // One null kernel step is one completed visible line.
            yield return result?.Truncated ?? false;
        }
    }

    internal async Task PrepareAsync(Size viewport, MarkdownPreviewPreparation preparation)
    {
        Dispatcher.VerifyAccess();
        var snapshot = Capture(viewport);
        var changed = false;
        EventHandler changedHandler = (_, _) => changed = true;
        foreach (var resource in snapshot.Observed) resource.Changed += changedHandler;
        MarkdownParagraphResult? result = null;
        try
        {
            result = await MarkdownLayoutWorker.Shared.PrepareAsync(
                snapshot.Request, preparation.Speculative, preparation.Cancellation).ConfigureAwait(false);
        }
        finally
        {
            // A caller may own a Dispatcher without an installed SynchronizationContext.
            // Both publication AND event removal must return to the captured UI Dispatcher.
            // Use a low-priority operation; this is not an animation barrier or a synchronous wait.
            await Dispatcher.InvokeAsync(() =>
            {
                var resumedAt = Stopwatch.GetTimestamp();
                try
                {
                    if (result == null || !preparation.IsCurrent()) return;
                    // Mutable resources are observed while the worker runs; this lightweight stamp
                    // catches replacement of frozen/resources without rebuilding pieces/styles.
                    if (changed || !snapshot.Appearance.SequenceEqual(CaptureAppearance(viewport)))
                    {
                        preparation.Invalidate();
                        return;
                    }
                    Apply(snapshot, result);
                }
                finally
                {
                    foreach (var resource in snapshot.Observed) resource.Changed -= changedHandler;
                    preparation.MaxResumeUiMilliseconds = Math.Max(preparation.MaxResumeUiMilliseconds,
                        Stopwatch.GetElapsedTime(resumedAt).TotalMilliseconds);
                }
            }, preparation.Speculative
                ? System.Windows.Threading.DispatcherPriority.ContextIdle
                : System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private sealed record Snapshot(MarkdownParagraphRequest Request, Brush? Background,
        IReadOnlyList<object?> Appearance, IReadOnlyList<Freezable> Observed);

    private IReadOnlyList<object?> CaptureAppearance(Size viewport)
    {
        Dispatcher.VerifyAccess();
        Brush Resource(string key, Brush fallback) => _template.TryFindResource(key) as Brush ?? fallback;
        var strongFamily = AppTypography.FontFamilyFor(content: true, bold: true);
        var codeFamily = NoteTypography.CodeFontFamily;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Array.AsReadOnly(new object?[]
        {
            viewport,
            _template.FontFamily.Source, _template.FontFamily.BaseUri,
            _template.FontWeight, _template.FontStyle, _template.FontStretch, _template.FontSize,
            _template.Language.IetfLanguageTag,
            _template.Foreground, _template.Background, _template.TextDecorations,
            Resource("WeakTextBrushKey", _template.Foreground),
            Resource("LinkBrushKey", _template.Foreground),
            Resource("HoverBrushKey", Brushes.Transparent),
            Theme.SyntaxFadeBrush,
            strongFamily.Source, strongFamily.BaseUri,
            AppTypography.UsesCustomBoldFace(true), AppTypography.FontWeightFor(true),
            NoteTypography.HeadingFontWeight,
            codeFamily.Source, codeFamily.BaseUri, NoteTypography.CodeFontSize,
            _zoom, dpi, AppTypography.TextFormattingMode
        });
    }

    private Snapshot Capture(Size viewport)
    {
        Dispatcher.VerifyAccess();
        var appearance = CaptureAppearance(viewport);
        var observed = new HashSet<Freezable>(ReferenceEqualityComparer.Instance);
        T? FreezeCopy<T>(T? value) where T : Freezable
        {
            if (value == null) return null;
            if (value.IsFrozen) return value;
            observed.Add(value);
            var copy = (T)value.CloneCurrentValue();
            if (!copy.CanFreeze) throw new InvalidOperationException("Preview resource cannot form a frozen snapshot.");
            copy.Freeze(); return copy;
        }
        var styles = new List<MarkdownRunStyle>();
        var indices = new Dictionary<(MarkdownEdgeCapsulePreviewRenderer.InlineStyle, bool), int>();
        int Style(MarkdownEdgeCapsulePreviewRenderer.InlineStyle flags, bool link)
        {
            if (indices.TryGetValue((flags, link), out var index)) return index;
            bool Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle flag) => (flags & flag) != 0;
            var strong = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Strong);
            var code = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Code);
            var family = code ? NoteTypography.CodeFontFamily : strong ? AppTypography.FontFamilyFor(content: true, bold: true) : _template.FontFamily;
            var weight = strong ? AppTypography.UsesCustomBoldFace(true) ? AppTypography.FontWeightFor(true) : NoteTypography.HeadingFontWeight : _template.FontWeight;
            var fontStyle = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Italic) ? FontStyles.Italic : _template.FontStyle;
            var fontSize = Math.Round((code ? NoteTypography.CodeFontSize : _template.FontSize) * _zoom, 1);
            var culture = _template.Language.GetEquivalentCulture().Name;
            Brush Resource(string key, Brush fallback) => _template.TryFindResource(key) as Brush ?? fallback;
            var foreground = Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Syntax) ? Theme.SyntaxFadeBrush
                : Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Weak) ? Resource("WeakTextBrushKey", _template.Foreground)
                : link ? Resource("LinkBrushKey", _template.Foreground) : _template.Foreground;
            var background = code ? Resource("HoverBrushKey", Brushes.Transparent) : _template.Background;
            var frozenForeground = FreezeCopy(foreground)!;
            var frozenBackground = FreezeCopy(background);
            var decorations = new TextDecorationCollection();
            void Add(TextDecorationCollection? values)
            {
                if (FreezeCopy(values) is { } frozen) foreach (var item in frozen) decorations.Add(item);
            }
            Add(_template.TextDecorations);
            if (Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Strike)) Add(TextDecorations.Strikethrough);
            if (link || Has(MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Underline)) Add(TextDecorations.Underline);
            decorations.Freeze();
            index = styles.Count;
            styles.Add(new(family.Source, family.BaseUri, fontStyle, weight, _template.FontStretch, fontSize,
                culture, frozenForeground, frozenBackground, decorations.Count == 0 ? null : decorations));
            indices[(flags, link)] = index;
            return index;
        }
        Style(0, false); // slot zero is the paragraph default
        var pieces = Prefix(_template);
        pieces.AddRange(_inlineCache.Get(_source, _mode).Pieces.Where(piece => piece.Text.Length > 0));
        var links = new List<string>();
        var linkIndices = new Dictionary<Uri, int>(ReferenceEqualityComparer.Instance);
        var inputs = new List<MarkdownLayoutPiece>();
        foreach (var piece in pieces)
        {
            var linkIndex = -1;
            if (piece.Link != null && !linkIndices.TryGetValue(piece.Link, out linkIndex))
            {
                linkIndex = links.Count;
                links.Add(piece.Link.AbsoluteUri); linkIndices.Add(piece.Link, linkIndex);
            }
            inputs.Add(new(piece.Text, Style(piece.Style, piece.Link != null), linkIndex));
        }
        var backgroundSnapshot = FreezeCopy(_template.Background);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = AppTypography.TextFormattingMode;
        return new(new(inputs, styles, links, viewport, dpi, mode), backgroundSnapshot,
            appearance, Array.AsReadOnly(observed.ToArray()));
    }

    private void Apply(Snapshot snapshot, MarkdownParagraphResult result)
    {
        Dispatcher.VerifyAccess();
        if (!result.Drawing.IsFrozen) throw new InvalidOperationException("Unfrozen paragraph result.");
        Children.Clear();
        Background = snapshot.Background;
        _drawing = result.Drawing; _size = result.Size;
        VisibleText = result.VisibleText; FormattedLines = result.FormattedLines;
        FormattingThreadId = result.FormattingThreadId; Truncated = result.Truncated;
        foreach (var link in result.Links)
        {
            var uri = snapshot.Request.LinkTargets[link.LinkIndex];
            var rect = link.Bounds;
            var hit = new Button
            {
                Background = Brushes.Transparent, Template = LinkHitTemplate, ClickMode = ClickMode.Release,
                Padding = new Thickness(), BorderThickness = new Thickness(),
                Width = rect.Width, Height = rect.Height, Cursor = Cursors.Hand,
                Focusable = true, ToolTip = uri
            };
            EdgeCapsulePreviewInteraction.SetConsumesPointer(hit, true);
            hit.Click += (_, e) => { _openExternal(uri); e.Handled = true; };
            SetLeft(hit, rect.X); SetTop(hit, rect.Y); Children.Add(hit);
        }
        InvalidateMeasure(); InvalidateVisual();
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
}

// UI-owned bridge for a single viewport build. Its pending operation is awaited by that existing
// owner, never by Measure/Arrange or the animation scheduler. Not a second publication manager.
internal sealed class MarkdownPreviewPreparation(
    bool speculative, CancellationToken cancellation, Func<bool> isCurrent, Action invalidate)
{
    internal bool Speculative { get; } = speculative;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal Func<bool> IsCurrent { get; } = isCurrent;
    internal Action Invalidate { get; } = invalidate;
    internal Task? Pending { get; set; }
    internal double MaxResumeUiMilliseconds { get; set; }
}
