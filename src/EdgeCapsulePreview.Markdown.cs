using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PaperTodo.EdgePreviewChecks")]

namespace PaperTodo;

internal sealed class MarkdownEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static MarkdownEdgeCapsulePreviewProvider Instance { get; } = new();

    private MarkdownEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var initialVersion = context.InvalidationSource.Version;
        var content = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher).Capture(context);
        var textScale = MarkdownEdgeCapsulePreviewRenderer.EstimateTextScale(context.Paper.TextZoom);
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            MarkdownEdgeCapsulePreviewRenderer.MeasureText(content),
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 460,
            fixedReserveWidthDip: 72,
            bodyScale: textScale);
        // The body loses host close/chrome 22 + view margins 19 + viewport margins 3.
        // Keep the estimate lightweight, but account for the same font size and per-note zoom
        // as rendering. Only the final card height is capped, not each admitted paragraph.
        var lines = MarkdownEdgeCapsulePreviewRenderer.EstimateVisualLines(
            content,
            Math.Max(1, width - 44) / textScale);
        var empty = content.IsEmpty;
        var height = empty
            ? 120
            : Math.Clamp(
                74 + lines * AppTypography.Scale(22) * textScale,
                150,
                410);
        if (empty)
        {
            width = Math.Max(130, width);
        }

        MarkdownEdgeCapsulePreviewView? view = null;
        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => view = new MarkdownEdgeCapsulePreviewView(context, size, content, initialVersion),
            visible => view?.SetPreviewActive(visible));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly MarkdownEdgeCapsulePreviewViewport _viewport;
    private MarkdownEdgeCapsulePreviewRenderer.PreviewContent? _initialContent;
    private readonly long _initialVersion;

    public MarkdownEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size,
        MarkdownEdgeCapsulePreviewRenderer.PreviewContent initialContent,
        long initialVersion = 0)
        : base(context, size)
    {
        _initialContent = initialContent;
        _initialVersion = initialVersion;
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var heading = new Grid
        {
            Margin = new Thickness(2, 0, 1, 8)
        };

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        heading.Children.Add(_title);
        Children.Add(heading);

        _viewport = new MarkdownEdgeCapsulePreviewViewport(new StackPanel())
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        Grid.SetRow(_viewport, 1);
        Children.Add(_viewport);

        InitializeLiveContent();
    }

    internal void SetPreviewActive(bool active) => _viewport.SetPreviewActive(active);
    internal MarkdownEdgeCapsulePreviewViewport PreloadViewport => _viewport;

    protected override void RebuildContent()
    {
        var title = Context.Title;
        _title.Text = title;
        _title.ToolTip = title;
        // Capture once on the owning Dispatcher. Deferred work never rereads a different paper
        // or mutable editor halfway through a build, and never touches WPF on a worker thread.
        var content = _initialContent != null && _initialVersion == Context.InvalidationSource.Version
            ? _initialContent : MarkdownEdgePreviewPreload.For(Dispatcher).Capture(Context);
        _initialContent = null;
        var textZoom = Context.Paper.TextZoom;
        _viewport.SetContent((target, size, preparation) => MarkdownEdgeCapsulePreviewRenderer.RenderSteps(
            target, content, Context.OpenExternal, size, textZoom, preparation),
            MarkdownEdgePreviewPreload.For(Dispatcher).Bind(Context, content, textZoom));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewViewport : Panel
{
    private StackPanel _body;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _bodyClip = new();
    private bool _sourceTruncated;
    private bool _previewActive = true;
    private Func<Panel, Size, MarkdownPreviewPreparation, IEnumerable<bool>>? _renderContent;
    private CancellationTokenSource? _buildCancellation;
    private Size? _renderedSize;
    // A completed body may survive a brief retract/resume at unchanged content and geometry.
    // Published bodies stay view-owned while mounted. On detach the optional bounded preload
    // cache may take exclusive ownership; no body can belong to two live trees.
    private Size? _publishedSize;
    private long _renderVersion;
    private MarkdownEdgePreviewPreload.Binding? _preloadBinding;
    private MarkdownEdgePreviewPreload.Key? _publishedKey;
    internal Func<bool>? PreloadStillCurrent { get; set; }
    internal event Action<bool>? PreparationFinished;

    public MarkdownEdgeCapsulePreviewViewport(StackPanel body)
    {
        ClipToBounds = true;
        Opacity = 0;
        IsHitTestVisible = false;
        _body = body;
        _body.Clip = _bodyClip;
        _overflowIndicator = new TextBlock
        {
            Text = "…",
            FontFamily = NoteTypography.FontFamily,
            FontSize = AppTypography.Scale(14),
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        _overflowIndicator.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Children.Add(_body);
        Children.Add(_overflowIndicator);
        Loaded += (_, _) => InvalidateArrange();
        Unloaded += (_, _) => { ReturnBodyToPreload(); InvalidateContentBuild(); };
        IsVisibleChanged += (_, _) => CancelPendingBuild();
    }

    public void SetContent(Func<Panel, Size, IEnumerable<bool>> renderContent,
        MarkdownEdgePreviewPreload.Binding? preloadBinding = null) =>
        SetContent((target, size, _) => renderContent(target, size), preloadBinding);

    internal void SetContent(Func<Panel, Size, MarkdownPreviewPreparation, IEnumerable<bool>> renderContent,
        MarkdownEdgePreviewPreload.Binding? preloadBinding = null)
    {
        _renderContent = renderContent;
        _preloadBinding = preloadBinding;
        InvalidateContentBuild();
    }

    internal bool ReturnBodyToPreload()
    {
        if (_publishedKey is not { } key || _publishedSize == null || !key.Binding.Current) return false;
        var body = _body;
        Children.Remove(body);
        body.Clip = null;
        body.IsHitTestVisible = false;
        _body = new StackPanel { Clip = _bodyClip };
        Children.Add(_body);
        var retained = key.Binding.Owner.Store(new(key, body, _sourceTruncated));
        _sourceTruncated = false;
        _publishedKey = null;
        _publishedSize = _renderedSize = null;
        _renderVersion++;
        Opacity = 0;
        IsHitTestVisible = false;
        return retained;
    }

    internal void SetPreviewActive(bool active)
    {
        if (_previewActive == active)
        {
            return;
        }
        _previewActive = active;
        IsHitTestVisible = active && Opacity > 0;
        // Cancel unfinished work immediately, but keep a complete body at the same size.
        // A content/DPI invalidation or unload separately revokes that reuse permission.
        CancelPendingBuild();
    }

    private void InvalidateContentBuild()
    {
        _publishedSize = null;
        _publishedKey = null;
        CancelPendingBuild();
    }

    private void CancelPendingBuild()
    {
        _buildCancellation?.Cancel();
        _renderVersion++;
        _renderedSize = _publishedSize;
        InvalidateArrange();
    }

    private bool IsBuildCurrent(long version) =>
        version == _renderVersion && _previewActive && IsLoaded && IsVisible &&
        !Dispatcher.HasShutdownStarted && _preloadBinding?.Current != false &&
        PreloadStillCurrent?.Invoke() != false;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateContentBuild();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Only measure already-published content. Markdown creation must not be pulled back
        // into shell layout by UpdateLayout, native handoff, or a new animation frame.
        var naturalSize = new Size(availableSize.Width, double.PositiveInfinity);
        _body.Measure(naturalSize);
        _overflowIndicator.Measure(naturalSize);
        return new Size(
            Math.Min(availableSize.Width, Math.Max(_body.DesiredSize.Width, _overflowIndicator.DesiredSize.Width)),
            Math.Min(availableSize.Height, _body.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_renderedSize != finalSize && _previewActive && IsLoaded && IsVisible &&
            finalSize.Width > 0 && finalSize.Height > 0 && PreloadStillCurrent?.Invoke() != false &&
            MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, finalSize) is { } cachedKey &&
            cachedKey.Binding.Owner.TryTake(cachedKey, out var cached, PreloadStillCurrent == null))
        {
            Children.Remove(_body);
            _body = cached!.Panel;
            Children.Add(_body);
            _body.Clip = _bodyClip;
            _body.Opacity = 1;
            _body.IsHitTestVisible = true;
            _body.Measure(new Size(finalSize.Width, double.PositiveInfinity));
            _sourceTruncated = cached.Truncated;
            _publishedSize = _renderedSize = finalSize;
            _publishedKey = cachedKey;
            _renderVersion++;
            Opacity = 1;
            IsHitTestVisible = true;
            PreparationFinished?.Invoke(true);
        }
        var overflow = _sourceTruncated || _body.DesiredSize.Height > finalSize.Height;
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _overflowIndicator.DesiredSize.Height) : 0;
        var visibleHeight = finalSize.Height - indicatorHeight;
        _bodyClip.Rect = new Rect(0, 0, finalSize.Width, visibleHeight);
        _body.Arrange(new Rect(0, 0, finalSize.Width, _body.DesiredSize.Height));
        _overflowIndicator.Opacity = overflow ? 1 : 0;
        _overflowIndicator.Arrange(new Rect(0, visibleHeight, finalSize.Width, indicatorHeight));

        if (_renderContent != null && _previewActive && IsLoaded && IsVisible &&
            _renderedSize != finalSize && finalSize.Width > 0 && finalSize.Height > 0)
        {
            _renderedSize = finalSize;
            BuildContentAsync(_renderContent, finalSize, ++_renderVersion);
        }
        return finalSize;
    }

    private async void BuildContentAsync(
        Func<Panel, Size, MarkdownPreviewPreparation, IEnumerable<bool>> renderContent,
        Size size,
        long version)
    {
        StackPanel? staging = null;
        using var cancellation = new CancellationTokenSource();
        _buildCancellation?.Cancel();
        _buildCancellation = cancellation;
        var preparation = new MarkdownPreviewPreparation(PreloadStillCurrent != null, cancellation.Token,
            () => IsBuildCurrent(version), InvalidateContentBuild);
        var started = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var maxBatchMs = 0.0;
        var publicationMs = 0.0;
        var totalSteps = 0;
        var published = false;
        var preparedKey = MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, size);
        try
        {
            // Yield even before creating the iterator: shell layout/Render and input have higher
            // priority. Moving one monolithic RenderInto to Background would still block them.
            await Dispatcher.Yield(PreloadStillCurrent == null
                ? DispatcherPriority.Background : DispatcherPriority.ContextIdle);
            if (!IsBuildCurrent(version))
            {
                return;
            }

            staging = new StackPanel { Opacity = 0, IsHitTestVisible = false };
            // Inherit the real host's resources and DPI while preparing, without participating
            // in its Measure/Arrange or exposing partially built text. No bitmap/second HWND.
            Children.Add(staging);
            var truncated = false;
            var batchSteps = 0;
            var batchStarted = Stopwatch.GetTimestamp();
            using (var steps = renderContent(staging, size, preparation).GetEnumerator())
            {
                while (IsBuildCurrent(version) && steps.MoveNext())
                {
                    if (!IsBuildCurrent(version))
                    {
                        return;
                    }
                    truncated = steps.Current;
                    totalSteps++;
                    if (preparation.Pending is { } pending)
                    {
                        preparation.Pending = null;
                        maxBatchMs = Math.Max(maxBatchMs, Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
                        // Await without an animation/reconcile barrier. The worker owns no UI
                        // object and only this viewport may publish its completed generation.
                        await pending;
                        if (!IsBuildCurrent(version)) return;
                        batchSteps = 0;
                        batchStarted = Stopwatch.GetTimestamp();
                    }
                    // Ordinary rows still use cooperative UI work. Heavy paragraphs now yield
                    // one asynchronous operation; their line steps stay on the layout STA.
                    if (++batchSteps >= 4 || Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds >= 2)
                    {
                        maxBatchMs = Math.Max(maxBatchMs, Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
                        await Dispatcher.Yield(PreloadStillCurrent == null
                ? DispatcherPriority.Background : DispatcherPriority.ContextIdle);
                        batchSteps = 0;
                        batchStarted = Stopwatch.GetTimestamp();
                    }
                }
            }
            if (!IsBuildCurrent(version))
            {
                return;
            }

            maxBatchMs = Math.Max(maxBatchMs, Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
            // Child blocks have already been measured at this exact width. Keep this root
            // attached when publishing so inherited resources/DPI do not invalidate that work.
            var publicationStarted = Stopwatch.GetTimestamp();
            staging.Measure(new Size(size.Width, double.PositiveInfinity));
            if (!IsBuildCurrent(version))
            {
                return;
            }
            Children.Remove(_body);
            _body = staging;
            staging = null;
            _body.Clip = _bodyClip;
            _body.Opacity = 1;
            _body.IsHitTestVisible = true;
            _sourceTruncated = truncated;
            _publishedSize = size;
            // A resource/DPI change during preparation makes this result non-cacheable.
            _publishedKey = preparedKey == MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, size)
                ? preparedKey : null;
            Opacity = 1;
            IsHitTestVisible = true;
            published = true;
            InvalidateMeasure();
            publicationMs = Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !IsBuildCurrent(version)) { }
        catch (Exception ex)
        {
            // Preserve an already-published excerpt on an optional refresh failure. No automatic
            // retry at the same size; a later content/visibility/size invalidation can recover.
            if (IsBuildCurrent(version))
            {
                _sourceTruncated |= _body.Children.Count == 0;
                InvalidateArrange();
            }
            Trace.TraceWarning("Edge note preview rendering failed: {0}", ex.GetType().Name);
        }
        finally
        {
            cancellation.Cancel();
            if (ReferenceEquals(_buildCancellation, cancellation)) _buildCancellation = null;
            maxBatchMs = Math.Max(maxBatchMs, preparation.MaxResumeUiMilliseconds);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"markdown.prepare version={version} published={published} steps={totalSteps} " +
                $"maxBatchMs={maxBatchMs:F3} publishMs={publicationMs:F3} elapsedMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(started):F3}");
            if (staging != null)
            {
                Children.Remove(staging);
            }
            // Only the current build may complete this viewport's preparation. An older
            // cancelled/resource-invalidated build must not retire its replacement preload.
            if (version == _renderVersion) PreparationFinished?.Invoke(published);
        }
    }
}

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // The preview is a navigation surface, not a second document renderer. Bound both visual
    // nodes and source text so one pathological note cannot stall the hover transition.
    private const int MaximumRenderedBlocks = 16;
    private const int MaximumRenderedCharacters = 6000;
    private const int MaximumBlockCharacters = MaximumRenderedCharacters;
    private const int MaximumCodeCharacters = MaximumRenderedCharacters;

    private readonly record struct PreviewLine(string Text, bool Truncated);

    internal sealed record PreviewContent(
        IReadOnlyList<ContentLine> Lines,
        string RenderMode,
        bool Truncated)
    {
        public bool IsEmpty => Lines.All(line => string.IsNullOrWhiteSpace(line.Text));
        internal PreviewInlineCache Inlines { get; } = new();
    }

    internal readonly record struct ContentLine(
        string Text,
        bool WasInsideFence,
        MarkdownFenceLineKind FenceKind);

    // Select the source once, before requesting card geometry. Measuring and rendering consume
    // this same excerpt; text beyond either budget must not reserve empty card space. In Full,
    // a fenced code block consumes one block, while its source still shares the character cap.
    public static PreviewContent CaptureContent(string? markdown, string renderMode)
    {
        var lines = new List<ContentLine>();
        var fencedCodeState = default(MarkdownFencedCodeState);
        var blocks = 0;
        var characters = 0;
        var truncated = false;
        foreach (var previewLine in NormalizeLines(markdown))
        {
            var startsBlock = renderMode != MarkdownRenderModes.Full || !fencedCodeState.IsInside;
            var separatorLength = lines.Count == 0 ? 0 : 1;
            var remaining = MaximumRenderedCharacters - characters - separatorLength;
            if ((startsBlock && blocks >= MaximumRenderedBlocks) || remaining < 0 ||
                (remaining == 0 && previewLine.Text.Length > 0))
            {
                truncated = true;
                break;
            }

            var line = LimitText(previewLine.Text, remaining, out var limitedLine);
            var wasInsideFence = fencedCodeState.IsInside;
            var fenceKind = MarkdownFencedCodeScanner.ClassifyLine(
                line, fencedCodeState, out fencedCodeState);
            lines.Add(new ContentLine(line, wasInsideFence, fenceKind));
            characters += separatorLength + line.Length;
            if (startsBlock)
            {
                blocks++;
            }
            if (previewLine.Truncated || limitedLine)
            {
                truncated = true;
                break;
            }
        }
        return new PreviewContent(lines, renderMode, truncated);
    }

    private static readonly Regex HeadingPattern = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrderedListPattern = new(
        @"^\s*(\d+)[\.)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnorderedListPattern = new(
        @"^\s*[-+*]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TaskListPattern = new(
        @"^\s*[-+*]\s+\[([ xX])\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HorizontalRulePattern = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static double NormalizeTextZoom(double textZoom) =>
        double.IsFinite(textZoom) ? Math.Clamp(textZoom, 0.5, 1.5) : 1.0;

    internal static double EstimateTextScale(double textZoom) =>
        Math.Round(NoteTypography.FontSize * NormalizeTextZoom(textZoom), 1) / AppTypography.Scale(14);

    internal static IEnumerable<bool> WarmInlineSteps(PreviewContent content)
    {
        foreach (var line in content.Lines)
        {
            if (content.RenderMode == MarkdownRenderModes.Off || line.WasInsideFence ||
                line.FenceKind != MarkdownFenceLineKind.None) { yield return false; continue; }
            var raw = content.RenderMode == MarkdownRenderModes.Full ? line.Text.Trim() : line.Text;
            var trimmed = raw.TrimStart();
            var prefix = raw.Length - trimmed.Length;
            var heading = HeadingPattern.Match(trimmed);
            var task = TaskListPattern.Match(trimmed);
            var ordered = OrderedListPattern.Match(trimmed);
            var unordered = UnorderedListPattern.Match(trimmed);
            if (heading.Success) prefix += heading.Groups[2].Index;
            else if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                prefix++;
                if (content.RenderMode == MarkdownRenderModes.Full)
                    while (prefix < raw.Length && char.IsWhiteSpace(raw[prefix])) prefix++;
            }
            else if (task.Success || ordered.Success) prefix += (task.Success ? task : ordered).Groups[2].Index;
            else if (unordered.Success) prefix += unordered.Groups[1].Index;
            if (!HorizontalRulePattern.IsMatch(trimmed)) content.Inlines.Get(raw[prefix..], content.RenderMode);
            if (content.RenderMode == MarkdownRenderModes.Full)
                content.Inlines.Get(StripBlockPrefix(raw), MarkdownRenderModes.Full);
            yield return false;
        }
    }

    public static string MeasureText(PreviewContent content)
    {
        var measured = new List<string>();
        foreach (var line in content.Lines)
        {
            var original = line.Text;
            if (content.RenderMode != MarkdownRenderModes.Full)
            {
                measured.Add(CompactText(original));
                continue;
            }
            if (line.FenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing ||
                string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var text = line.WasInsideFence
                ? original.TrimEnd()
                : PrepareInlineTextForMeasurement(StripBlockPrefix(original), content.Inlines);
            measured.Add(CompactText(text));
        }

        // The shared width helper samples only 32 lines. Supply the widest admitted line so
        // a long Full-mode code block has no second, unrelated measurement cutoff.
        return measured.MaxBy(EdgeCapsulePreviewMeasure.DisplayWidth) ?? string.Empty;
    }

    public static int EstimateVisualLines(PreviewContent content, double widthDip)
    {
        var estimate = 0;
        var emptyCodeBlock = false;
        foreach (var line in content.Lines)
        {
            var raw = line.Text;
            var trimmed = raw.Trim();
            if (line.FenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing)
            {
                if (content.RenderMode != MarkdownRenderModes.Full)
                {
                    estimate += 1;
                }
                else if (line.FenceKind == MarkdownFenceLineKind.Opening)
                {
                    emptyCodeBlock = true;
                }
                else if (emptyCodeBlock)
                {
                    estimate += 1;
                    emptyCodeBlock = false;
                }
            }
            else if (trimmed.Length == 0 ||
                     (!line.WasInsideFence && HorizontalRulePattern.IsMatch(trimmed)))
            {
                emptyCodeBlock = false;
                estimate += 1;
            }
            else
            {
                emptyCodeBlock = false;
                var measurementText = line.WasInsideFence || content.RenderMode != MarkdownRenderModes.Full
                    ? raw.TrimEnd()
                    : PrepareInlineTextForMeasurement(StripBlockPrefix(trimmed), content.Inlines);
                var lines = EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                    measurementText,
                    widthDip);
                estimate += lines;
            }
        }
        return Math.Max(1, estimate + (emptyCodeBlock ? 1 : 0));
    }

    public static bool RenderInto(
        Panel target,
        string? markdown,
        Action<string> openExternal,
        string renderMode = MarkdownRenderModes.Full,
        Size? viewportSize = null,
        double textZoom = 1.0)
    {
        var truncated = false;
        foreach (var sourceTruncated in RenderSteps(target, markdown, openExternal, renderMode, viewportSize, textZoom))
        {
            truncated = sourceTruncated;
        }
        return truncated;
    }

    // One step consumes at most one bounded source line; the last value reports source
    // truncation. Synchronous checks and cooperative live rendering use this same renderer.
    public static IEnumerable<bool> RenderSteps(
        Panel target,
        string? markdown,
        Action<string> openExternal,
        string renderMode = MarkdownRenderModes.Full,
        Size? viewportSize = null,
        double textZoom = 1.0) =>
        RenderSteps(target, CaptureContent(markdown, renderMode), openExternal, viewportSize, textZoom);

    public static IEnumerable<bool> RenderSteps(
        Panel target,
        PreviewContent content,
        Action<string> openExternal,
        Size? viewportSize = null,
        double textZoom = 1.0,
        MarkdownPreviewPreparation? preparation = null)
    {
        target.Children.Clear();
        if (content.IsEmpty)
        {
            AddEmptyState(target);
            yield return content.Truncated;
            yield break;
        }

        var zoom = NormalizeTextZoom(textZoom);
        var renderMode = content.RenderMode;
        var code = new StringBuilder();
        var codeLineCount = 0;
        StackPanel? codeRows = null;
        var insideFence = false;
        var renderedHeight = 0.0;
        var truncated = false;
        MarkdownEdgePreviewParagraph? paragraph = null;

        FrameworkElement InlineBlock(TextBlock template, string text, string mode)
        {
            if (viewportSize.HasValue && ShouldPrepareParagraph(text, mode, content.Inlines))
                return paragraph = new MarkdownEdgePreviewParagraph(template, text, mode, zoom, openExternal, content.Inlines);
            if (mode == MarkdownRenderModes.Off) { template.Text = text; return template; }
            AddInlineContent(template.Inlines, text, openExternal, mode, content.Inlines);
            return template;
        }

        IEnumerable<bool> AddParagraph(FrameworkElement block, Panel? parent = null)
        {
            ApplyTextZoom(block, zoom);
            (parent ?? target).Children.Add(block);
            if (viewportSize is not { } size) yield break;
            block.Measure(new Size(size.Width, double.PositiveInfinity));
            if (paragraph != null)
            {
                var paragraphSize = new Size(paragraph.MeasuredWidth, Math.Max(0, size.Height - renderedHeight));
                if (preparation != null)
                {
                    preparation.Pending = paragraph.PrepareAsync(paragraphSize, preparation);
                    yield return false; // viewport awaits before advancing this iterator
                    truncated |= paragraph.Truncated;
                }
                else
                {
                    foreach (var omitted in paragraph.Prepare(paragraphSize))
                    {
                        truncated |= omitted;
                        yield return false;
                    }
                }
                block.InvalidateMeasure();
                block.Measure(new Size(size.Width, double.PositiveInfinity));
            }
            renderedHeight += block.DesiredSize.Height;
        }

        void AddBlock(FrameworkElement block)
        {
            ApplyTextZoom(block, zoom);
            target.Children.Add(block);
            if (viewportSize is { } size)
            {
                block.Measure(new Size(size.Width, double.PositiveInfinity));
                renderedHeight += block.DesiredSize.Height;
            }
        }

        IEnumerable<bool> AddCodeRow(string line)
        {
            if (codeRows == null)
            {
                // A fence remains one content block. Prepare each admitted row once instead of
                // repeatedly measuring a growing TextBlock, including its invisible wrapped tail.
                codeRows = new StackPanel();
                var host = new Border { Child = codeRows };
                host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
                target.Children.Add(host);
            }

            var text = NewTextBlock(string.Empty, NoteTypography.CodeFontSize);
            text.FontFamily = NoteTypography.CodeFontFamily;
            FrameworkElement row;
            if (line.Length >= MarkdownEdgePreviewParagraph.MinimumSourceLength)
            {
                row = paragraph = new MarkdownEdgePreviewParagraph(
                    text, line, MarkdownRenderModes.Off, zoom, openExternal, content.Inlines);
            }
            else
            {
                // Explicit empty Runs preserve the first, middle and last blank code rows.
                text.Inlines.Add(new Run(line));
                row = text;
            }
            foreach (var step in AddParagraph(row, codeRows)) yield return step;
        }

        IEnumerable<bool> FinishCodeBlock()
        {
            if (!viewportSize.HasValue)
            {
                AddBlock(BuildCodeBlock(code.ToString()));
            }
            else if (codeRows == null)
            {
                foreach (var step in AddCodeRow(string.Empty)) yield return step;
            }
        }

        foreach (var previewLine in content.Lines)
        {
            paragraph = null;
            // Include the block crossing the bottom edge. Measuring actual wrapped heights
            // avoids building admitted content that cannot be seen. The source budget was
            // already applied before sizing, so it cannot diverge here from the geometry input.
            if (viewportSize is { } size && renderedHeight > size.Height)
            {
                truncated = true;
                break;
            }

            var line = renderMode == MarkdownRenderModes.Full
                ? previewLine.Text.TrimEnd()
                : previewLine.Text;
            var wasInsideFence = previewLine.WasInsideFence;
            var fenceKind = previewLine.FenceKind;
            if (renderMode != MarkdownRenderModes.Full)
            {
                foreach (var step in AddParagraph(BuildSourceBlock(
                    line, renderMode, wasInsideFence, fenceKind, openExternal, InlineBlock))) yield return step;
            }
            else if (fenceKind == MarkdownFenceLineKind.Opening)
            {
                code.Clear();
                codeLineCount = 0;
                codeRows = null;
                insideFence = true;
            }
            else if (fenceKind == MarkdownFenceLineKind.Closing)
            {
                foreach (var step in FinishCodeBlock()) yield return step;
                code.Clear();
                insideFence = false;
            }
            else if (wasInsideFence)
            {
                if (viewportSize.HasValue)
                {
                    foreach (var step in AddCodeRow(line)) yield return step;
                }
                else
                {
                    truncated |= AppendCodeLine(code, line, codeLineCount++ > 0);
                }
            }
            else
            {
                foreach (var step in AddParagraph(BuildBlock(line, openExternal, InlineBlock))) yield return step;
            }

            if (truncated)
            {
                break;
            }
            yield return false;
        }
        if (renderMode == MarkdownRenderModes.Full && insideFence)
        {
            foreach (var step in FinishCodeBlock()) yield return step;
        }
        if (target.Children.Count == 0)
        {
            AddEmptyState(target);
        }
        yield return truncated || content.Truncated;
    }

    private static bool ShouldPrepareParagraph(string text, string mode, PreviewInlineCache cache)
    {
        if (text.Length >= MarkdownEdgePreviewParagraph.MinimumSourceLength) return true;
        // Tiny ordinary text stays on the simpler path. Reuse already-admitted inline values;
        // a short source can still contain many expensive styled elements.
        return text.Length >= 96 && mode != MarkdownRenderModes.Off &&
            cache.Get(text, mode).Pieces.Count >= 24;
    }

    private static void AddEmptyState(Panel target)
    {
        var empty = NewTextBlock("—", AppTypography.Scale(16));
        empty.Margin = new Thickness(4, 18, 4, 4);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        target.Children.Add(empty);
    }

    // All modes use the note's typography and natural line metrics, without extra paragraph
    // margins. Basic/Enhanced retain source layout; Full still uses lightweight preview blocks.
    private static FrameworkElement BuildSourceBlock(
        string line,
        string renderMode,
        bool wasInsideFence,
        MarkdownFenceLineKind fenceKind,
        Action<string> openExternal,
        Func<TextBlock, string, string, FrameworkElement>? inlineBlock = null)
    {
        var text = NewTextBlock(string.Empty, NoteTypography.FontSize);
        if (renderMode == MarkdownRenderModes.Off)
        {
            if (inlineBlock != null) return inlineBlock(text, line, MarkdownRenderModes.Off);
            text.Text = line;
            return text;
        }

        if (wasInsideFence || fenceKind == MarkdownFenceLineKind.Opening)
        {
            text.FontFamily = NoteTypography.CodeFontFamily;
            text.FontSize = NoteTypography.CodeFontSize;
            text.SetResourceReference(TextBlock.BackgroundProperty, "HoverBrushKey");
            if (fenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing)
            {
                AddSourceSyntax(text.Inlines, line, renderMode);
            }
            else
            {
                if (inlineBlock != null) return inlineBlock(text, line, MarkdownRenderModes.Off);
                text.Text = line;
            }
            return text;
        }

        var trimmed = line.TrimStart();
        var prefixLength = line.Length - trimmed.Length;
        var prefixRenderMode = renderMode;
        string? renderedPrefix = null;
        var heading = HeadingPattern.Match(trimmed);
        var task = TaskListPattern.Match(trimmed);
        var ordered = OrderedListPattern.Match(trimmed);
        var unordered = UnorderedListPattern.Match(trimmed);
        if (heading.Success)
        {
            text.FontSize = HeadingFontSize(heading.Groups[1].Value.Length);
            ApplyStrongTypography(text);
            prefixLength += heading.Groups[2].Index;
        }
        else if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            prefixLength++;
        }
        else if (task.Success || ordered.Success)
        {
            prefixLength += (task.Success ? task : ordered).Groups[2].Index;
            // Ordered numbers and task states stay readable in Enhanced, just as in the note.
            prefixRenderMode = MarkdownRenderModes.Basic;
        }
        else if (unordered.Success)
        {
            var markerStart = prefixLength;
            prefixLength += unordered.Groups[1].Index;
            if (renderMode == MarkdownRenderModes.Enhanced)
            {
                renderedPrefix = line[..markerStart] + "•" + line[(markerStart + 1)..prefixLength];
                prefixRenderMode = MarkdownRenderModes.Basic;
            }
        }
        else if (HorizontalRulePattern.IsMatch(trimmed))
        {
            return BuildSourceHorizontalRule(text, line, renderMode);
        }

        AddSourceSyntax(text.Inlines, renderedPrefix ?? line[..prefixLength], prefixRenderMode);
        if (inlineBlock != null) return inlineBlock(text, line[prefixLength..], renderMode);
        AddInlineContent(text.Inlines, line[prefixLength..], openExternal, renderMode);
        return text;
    }

    private static FrameworkElement BuildSourceHorizontalRule(TextBlock text, string line, string renderMode)
    {
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition());
        text.Text = line;
        var enhanced = renderMode == MarkdownRenderModes.Enhanced;
        if (enhanced)
        {
            // Keep the source line's height while replacing its visible markers with a rule.
            text.Foreground = Brushes.Transparent;
            Grid.SetColumnSpan(text, 2);
        }
        host.Children.Add(text);
        var rule = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(enhanced ? 2 : 8, 0, 2, 0)
        };
        rule.SetResourceReference(Border.BackgroundProperty, "PaperBorderBrushKey");
        Grid.SetColumn(rule, enhanced ? 0 : 1);
        Grid.SetColumnSpan(rule, enhanced ? 2 : 1);
        host.Children.Add(rule);
        return host;
    }

    private static void AddSourceSyntax(InlineCollection target, string syntax, string renderMode)
    {
        if (syntax.Length == 0)
        {
            return;
        }
        var run = new Run(syntax);
        if (renderMode == MarkdownRenderModes.Enhanced)
        {
            run.Foreground = Theme.SyntaxFadeBrush;
        }
        target.Add(run);
    }

    private static FrameworkElement BuildBlock(
        string line,
        Action<string> openExternal,
        Func<TextBlock, string, string, FrameworkElement>? inlineBlock = null)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return NewTextBlock(string.Empty, NoteTypography.FontSize);
        }

        if (HorizontalRulePattern.IsMatch(trimmed))
        {
            return BuildSourceHorizontalRule(
                NewTextBlock(string.Empty, NoteTypography.FontSize), trimmed, MarkdownRenderModes.Enhanced);
        }

        if (MarkdownImageReferences.TryParseReferenceLine(
                trimmed,
                out var imageReference))
        {
            var label = imageReference.Label;
            var text = NewTextBlock(
                string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}",
                AppTypography.Scale(11.5));
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            var host = new Border
            {
                Margin = new Thickness(1, 4, 1, 4),
                Padding = new Thickness(8, 7, 8, 7),
                CornerRadius = new CornerRadius(5),
                Child = text
            };
            host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
            return host;
        }

        var heading = HeadingPattern.Match(trimmed);
        if (heading.Success)
        {
            var text = NewTextBlock(string.Empty, HeadingFontSize(heading.Groups[1].Value.Length));
            ApplyStrongTypography(text);
            if (inlineBlock != null) return inlineBlock(text, heading.Groups[2].Value, MarkdownRenderModes.Full);
            AddInlineContent(text.Inlines, heading.Groups[2].Value, openExternal);
            return text;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var text = NewTextBlock(string.Empty, NoteTypography.FontSize);
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            var body = inlineBlock?.Invoke(text, trimmed[1..].TrimStart(), MarkdownRenderModes.Full);
            if (body == null) AddInlineContent(text.Inlines, trimmed[1..].TrimStart(), openExternal);
            var host = new Border
            {
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 0, 5, 0),
                Child = body ?? text
            };
            return host;
        }

        var task = TaskListPattern.Match(trimmed);
        if (task.Success)
        {
            var done = !string.Equals(task.Groups[1].Value, " ", StringComparison.Ordinal);
            return BuildListRow(
                done ? "☑" : "☐",
                task.Groups[2].Value,
                openExternal,
                done, inlineBlock);
        }

        var ordered = OrderedListPattern.Match(trimmed);
        if (ordered.Success)
        {
            return BuildListRow(
                $"{ordered.Groups[1].Value}.",
                ordered.Groups[2].Value,
                openExternal,
                done: false, inlineBlock);
        }

        var unordered = UnorderedListPattern.Match(trimmed);
        if (unordered.Success)
        {
            return BuildListRow(
                "•",
                unordered.Groups[1].Value,
                openExternal,
                done: false, inlineBlock);
        }

        var normal = NewTextBlock(string.Empty, NoteTypography.FontSize);
        if (inlineBlock != null) return inlineBlock(normal, trimmed, MarkdownRenderModes.Full);
        AddInlineContent(normal.Inlines, trimmed, openExternal);
        return normal;
    }

    private static FrameworkElement BuildListRow(
        string marker,
        string content,
        Action<string> openExternal,
        bool done,
        Func<TextBlock, string, string, FrameworkElement>? inlineBlock = null)
    {
        var grid = new Grid
        {
            Margin = new Thickness(2, 0, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var markerText = NewTextBlock(marker, NoteTypography.FontSize);
        markerText.Margin = new Thickness(0, 0, AppTypography.Scale(6), 0);
        markerText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        grid.Children.Add(markerText);

        var body = NewTextBlock(string.Empty, NoteTypography.FontSize);
        if (done)
        {
            body.TextDecorations = TextDecorations.Strikethrough;
            body.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        }
        var renderedBody = inlineBlock?.Invoke(body, content, MarkdownRenderModes.Full);
        if (renderedBody == null) AddInlineContent(body.Inlines, content, openExternal);
        Grid.SetColumn(renderedBody ?? body, 1);
        grid.Children.Add(renderedBody ?? body);
        return grid;
    }

    private static Border BuildCodeBlock(string code)
    {
        var text = NewTextBlock(string.Empty, NoteTypography.CodeFontSize);
        text.FontFamily = NoteTypography.CodeFontFamily;
        text.Inlines.Add(new Run(code));
        var host = new Border
        {
            Child = text
        };
        host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        return host;
    }

    private static TextBlock NewTextBlock(string text, double fontSize)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = NoteTypography.FontFamily,
            FontSize = fontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Language = NoteTypography.Language,
            TextWrapping = TextWrapping.Wrap,
            // AvalonEdit uses natural TextFormatter metrics; do not add a second line-height
            // policy or per-source-line vertical margin on the lightweight preview.
            LineHeight = double.NaN
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        NoteTypography.ApplyTextRendering(block);
        return block;
    }

    private static double HeadingFontSize(int level) => level switch
    {
        1 => NoteTypography.Heading1FontSize,
        2 => NoteTypography.Heading2FontSize,
        3 => NoteTypography.Heading3FontSize,
        _ => NoteTypography.FontSize
    };

    private static void ApplyStrongTypography(DependencyObject target)
    {
        target.SetValue(TextElement.FontFamilyProperty, AppTypography.FontFamilyFor(content: true, bold: true));
        target.SetValue(TextElement.FontWeightProperty, AppTypography.UsesCustomBoldFace(true)
            ? AppTypography.FontWeightFor(true)
            : NoteTypography.HeadingFontWeight);
    }

    private static void ApplyTextZoom(DependencyObject element, double zoom)
    {
        if (element is MarkdownEdgePreviewParagraph) return;
        // Compose per-paper zoom with the unrounded global size once, just as MarkdownTextBox
        // does. Only local font sizes are scaled: inherited inline sizes must not be scaled twice.
        if (element.ReadLocalValue(TextElement.FontSizeProperty) is double size)
        {
            element.SetValue(TextElement.FontSizeProperty, Math.Round(size * zoom, 1));
        }
        switch (element)
        {
            case TextBlock text:
                foreach (Inline inline in text.Inlines) ApplyTextZoom(inline, zoom);
                break;
            case Span span:
                foreach (Inline inline in span.Inlines) ApplyTextZoom(inline, zoom);
                break;
            case Panel panel:
                foreach (UIElement child in panel.Children) ApplyTextZoom(child, zoom);
                break;
            case Decorator { Child: { } child }:
                ApplyTextZoom(child, zoom);
                break;
        }
    }

    private static IEnumerable<PreviewLine> NormalizeLines(string? markdown)
    {
        markdown ??= string.Empty;
        var lineStart = 0;
        while (lineStart <= markdown.Length)
        {
            var lineEnd = lineStart;
            var scanEnd = lineStart + Math.Min(
                MaximumBlockCharacters,
                markdown.Length - lineStart);
            while (lineEnd < scanEnd &&
                markdown[lineEnd] is not ('\r' or '\n'))
            {
                lineEnd++;
            }

            var truncated = lineEnd < markdown.Length &&
                markdown[lineEnd] is not ('\r' or '\n');
            yield return new PreviewLine(
                markdown[lineStart..SafePrefixEnd(markdown, lineEnd)],
                truncated);
            if (truncated)
            {
                yield break;
            }
            if (lineEnd >= markdown.Length)
            {
                yield break;
            }

            lineStart = lineEnd + 1;
            if (markdown[lineEnd] == '\r' &&
                lineStart < markdown.Length &&
                markdown[lineStart] == '\n')
            {
                lineStart++;
            }
        }
    }

    private static bool AppendCodeLine(StringBuilder target, string line, bool hasPreviousLine)
    {
        // StringBuilder.Length cannot distinguish no line from an empty first line.
        var separatorLength = hasPreviousLine ? 1 : 0;
        var remaining = MaximumCodeCharacters - target.Length - separatorLength;
        if (remaining <= 0)
        {
            return true;
        }

        var value = LimitText(line, remaining, out var truncated);
        if (separatorLength > 0)
        {
            target.Append('\n');
        }
        target.Append(value);
        return truncated;
    }

    private static string PrepareInlineTextForMeasurement(string text, PreviewInlineCache cache) =>
        cache.Get(text, MarkdownRenderModes.Full).VisibleText;
    private static string CompactText(string value) =>
        LimitText(value, MaximumBlockCharacters, out _);

    private static string LimitText(string value, int maximumLength, out bool truncated)
    {
        maximumLength = Math.Max(0, maximumLength);
        truncated = value.Length > maximumLength;
        if (!truncated)
        {
            return value;
        }
        if (maximumLength == 0)
        {
            return string.Empty;
        }
        if (maximumLength == 1)
        {
            return "…";
        }
        return value[..SafePrefixEnd(value, maximumLength - 1)] + "…";
    }

    // Character budgets use UTF-16 units, but never split a valid surrogate pair at the edge.
    private static int SafePrefixEnd(string value, int end) =>
        end > 0 && end < value.Length &&
        char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end]) ? end - 1 : end;

    private static string StripBlockPrefix(string line)
    {
        var trimmed = line.Trim();
        var heading = HeadingPattern.Match(trimmed);
        if (heading.Success)
        {
            return heading.Groups[2].Value;
        }
        var task = TaskListPattern.Match(trimmed);
        if (task.Success)
        {
            return task.Groups[2].Value;
        }
        var ordered = OrderedListPattern.Match(trimmed);
        if (ordered.Success)
        {
            return $"{ordered.Groups[1].Value}. {ordered.Groups[2].Value}";
        }
        var unordered = UnorderedListPattern.Match(trimmed);
        if (unordered.Success)
        {
            return unordered.Groups[1].Value;
        }
        return trimmed.StartsWith(">", StringComparison.Ordinal)
            ? trimmed[1..].TrimStart()
            : trimmed;
    }
}