using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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

        _viewport = new MarkdownEdgeCapsulePreviewViewport()
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        Grid.SetRow(_viewport, 1);
        Children.Add(_viewport);

        InitializeLiveContent();
    }

    internal void SetPreviewActive(bool active) => _viewport.SetPreviewActive(active);

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
        _viewport.SetContent(content, Context.OpenExternal, textZoom,
            MarkdownEdgePreviewPreload.For(Dispatcher).Bind(Context, content, textZoom));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewViewport : Panel
{
    private MarkdownPreviewArtifactSurface? _body;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _bodyClip = new();
    private MarkdownEdgeCapsulePreviewRenderer.PreviewContent? _content;
    private Action<string> _openExternal = _ => { };
    private double _textZoom;
    private bool _previewActive = true;
    private CancellationTokenSource? _buildCancellation;
    private Size? _renderedSize;
    private Size? _publishedSize;
    private long _renderVersion;
    private MarkdownEdgePreviewPreload.Binding? _preloadBinding;

    public MarkdownEdgeCapsulePreviewViewport()
    {
        ClipToBounds = true;
        Opacity = 0;
        IsHitTestVisible = false;
        _overflowIndicator = new TextBlock
        {
            Text = "…", FontFamily = NoteTypography.FontFamily,
            FontSize = AppTypography.Scale(14), TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        _overflowIndicator.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Children.Add(_overflowIndicator);
        Loaded += (_, _) => InvalidateArrange();
        Unloaded += (_, _) =>
        {
            if (_body != null) Children.Remove(_body);
            _body = null;
            Opacity = 0;
            InvalidateContentBuild();
        };
        IsVisibleChanged += (_, _) => CancelPendingBuild();
    }

    internal void SetContent(MarkdownEdgeCapsulePreviewRenderer.PreviewContent content,
        Action<string> openExternal, double textZoom = 1,
        MarkdownEdgePreviewPreload.Binding? preloadBinding = null)
    {
        _content = content;
        _openExternal = openExternal;
        _textZoom = textZoom;
        _preloadBinding = preloadBinding;
        InvalidateContentBuild();
    }

    internal void SetPreviewActive(bool active)
    {
        if (_previewActive == active) return;
        _previewActive = active;
        IsHitTestVisible = active && _publishedSize != null;
        // Only this mounted view may reuse its completed surface on a brief retract/resume.
        CancelPendingBuild();
    }

    private void InvalidateContentBuild()
    {
        _publishedSize = null;
        IsHitTestVisible = false;
        CancelPendingBuild();
    }

    private void CancelPendingBuild()
    {
        _buildCancellation?.Cancel();
        _renderVersion++;
        _renderedSize = _publishedSize;
        InvalidateArrange();
    }

    // Demand owns its generation independently of the optional preload cache. Clearing a cache
    // must not strand an active preview; content changes arrive through the live view's owner.
    private bool IsBuildCurrent(long version) => version == _renderVersion && _previewActive &&
        IsLoaded && IsVisible && !Dispatcher.HasShutdownStarted;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateContentBuild();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new Size(availableSize.Width, double.PositiveInfinity);
        _body?.Measure(natural);
        _overflowIndicator.Measure(natural);
        return new Size(Math.Min(availableSize.Width, Math.Max(_body?.DesiredSize.Width ?? 0,
            _overflowIndicator.DesiredSize.Width)), Math.Min(availableSize.Height, _body?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_content != null && _previewActive && IsLoaded && IsVisible &&
            _renderedSize != finalSize && finalSize.Width > 0 && finalSize.Height > 0)
        {
            _buildCancellation?.Cancel();
            var version = ++_renderVersion;
            _renderedSize = finalSize;
            IsHitTestVisible = false;
            var key = MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, finalSize);
            if (key != null && key.Binding.Owner.TryGetArtifact(key, out var artifact))
                Publish(artifact!, finalSize);
            else
                BuildContentAsync(finalSize, version);
        }
        var height = _body?.DesiredSize.Height ?? 0;
        var overflow = _body?.Artifact.Truncated == true || height > finalSize.Height;
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _overflowIndicator.DesiredSize.Height) : 0;
        var visibleHeight = finalSize.Height - indicatorHeight;
        _bodyClip.Rect = new Rect(0, 0, finalSize.Width, visibleHeight);
        _body?.Arrange(new Rect(0, 0, finalSize.Width, height));
        _overflowIndicator.Opacity = overflow ? 1 : 0;
        _overflowIndicator.Arrange(new Rect(0, visibleHeight, finalSize.Width, indicatorHeight));
        return finalSize;
    }

    private void Publish(MarkdownPreviewArtifact artifact, Size size)
    {
        // Cache hits and freshly built results have the same single publication boundary.
        var surface = new MarkdownPreviewArtifactSurface(artifact, _openExternal) { Clip = _bodyClip };
        if (_body != null) Children.Remove(_body);
        _body = surface;
        Children.Insert(0, surface);
        surface.Measure(new Size(size.Width, double.PositiveInfinity));
        _publishedSize = _renderedSize = size;
        Opacity = 1;
        IsHitTestVisible = _previewActive;
        InvalidateMeasure();
    }

    private async void BuildContentAsync(Size size, long version)
    {
        using var cancellation = new CancellationTokenSource();
        _buildCancellation = cancellation;
        var started = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var published = false;
        try
        {
            // Never format during shell Measure/Arrange, even for a single ordinary short row.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!IsBuildCurrent(version)) return;
            var artifact = await MarkdownEdgeCapsulePreviewRenderer.PrepareArtifactAsync(
                this, _content!, size, _textZoom, speculative: false, cancellation.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsBuildCurrent(version)) return;
                if (artifact == null) { InvalidateContentBuild(); return; } // appearance changed during work
                Publish(artifact, size);
                published = true;
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Keep an already displayed result inert on failure; no timer/retry loop. A normal
            // content, visibility or size event permits a fresh attempt.
            Trace.TraceWarning("Edge note preview rendering failed: {0}", ex.GetType().Name);
        }
        finally
        {
            // Dispatcher shutdown may abort this cleanup operation; never let async-void
            // completion turn an orderly exit into an unhandled cancellation.
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_buildCancellation, cancellation)) _buildCancellation = null;
                }, DispatcherPriority.Send);
            }
            catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted) { }
            EdgeCapsulePerformanceDiagnostics.Trace($"markdown.prepare version={version} published={published} " +
                $"elapsedMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(started):F3}");
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