using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class MarkdownEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static MarkdownEdgeCapsulePreviewProvider Instance { get; } = new();

    private MarkdownEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var text = context.ReadMarkdownText();
        var renderMode = context.ReadMarkdownRenderMode();
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            MarkdownEdgeCapsulePreviewRenderer.MeasureText(text, renderMode),
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 460);
        var lines = MarkdownEdgeCapsulePreviewRenderer.EstimateVisualLines(
            text,
            Math.Max(72, width - 36),
            renderMode);
        var empty = string.IsNullOrWhiteSpace(text);
        var height = empty
            ? 120
            : Math.Clamp(
                74 + Math.Min(15, lines) * AppTypography.Scale(22),
                150,
                410);
        if (empty)
        {
            width = Math.Max(130, width);
        }

        MarkdownEdgeCapsulePreviewView? view = null;
        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => view = new MarkdownEdgeCapsulePreviewView(context, size),
            visible => view?.SetPreviewActive(visible));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly MarkdownEdgeCapsulePreviewViewport _viewport;

    public MarkdownEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
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

    protected override void RebuildContent()
    {
        var title = Context.Title;
        _title.Text = title;
        _title.ToolTip = title;
        // Capture once on the owning Dispatcher. Deferred work never rereads a different paper
        // or mutable editor halfway through a build, and never touches WPF on a worker thread.
        var markdown = Context.ReadMarkdownText();
        var renderMode = Context.ReadMarkdownRenderMode();
        var textZoom = Context.Paper.TextZoom;
        _viewport.SetContent((target, size) => MarkdownEdgeCapsulePreviewRenderer.RenderSteps(
            target, markdown, Context.OpenExternal, renderMode, size, textZoom));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewViewport : Panel
{
    private StackPanel _body;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _bodyClip = new();
    private bool _sourceTruncated;
    private bool _previewActive = true;
    private Func<Panel, Size, IEnumerable<bool>>? _renderContent;
    private Size? _renderedSize;
    private long _renderVersion;

    public MarkdownEdgeCapsulePreviewViewport(StackPanel body)
    {
        ClipToBounds = true;
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
        Unloaded += (_, _) => InvalidateContentBuild();
        IsVisibleChanged += (_, _) => InvalidateContentBuild();
    }

    public void SetContent(Func<Panel, Size, IEnumerable<bool>> renderContent)
    {
        _renderContent = renderContent;
        InvalidateContentBuild();
    }

    internal void SetPreviewActive(bool active)
    {
        if (_previewActive == active)
        {
            return;
        }
        _previewActive = active;
        // The host can still be visible while the old card retracts. Stop its pending work at
        // the existing preview visibility boundary, rather than waiting for WPF Unloaded.
        InvalidateContentBuild();
    }

    private void InvalidateContentBuild()
    {
        _renderVersion++;
        _renderedSize = null;
        InvalidateArrange();
    }

    private bool IsBuildCurrent(long version) =>
        version == _renderVersion && _previewActive && IsLoaded && IsVisible &&
        !Dispatcher.HasShutdownStarted;

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
        Func<Panel, Size, IEnumerable<bool>> renderContent,
        Size size,
        long version)
    {
        StackPanel? staging = null;
        try
        {
            // Yield even before creating the iterator: shell layout/Render and input have higher
            // priority. Moving one monolithic RenderInto to Background would still block them.
            await Dispatcher.Yield(DispatcherPriority.Background);
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
            using (var steps = renderContent(staging, size).GetEnumerator())
            {
                while (IsBuildCurrent(version) && steps.MoveNext())
                {
                    if (!IsBuildCurrent(version))
                    {
                        return;
                    }
                    truncated = steps.Current;
                    // This is a cooperative budget, not a hard deadline: one bounded paragraph
                    // can still take longer. Yield inside long code fences too, one source line
                    // per step, instead of accumulating an entire fence in a single batch.
                    if (++batchSteps >= 4 || Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds >= 2)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        batchSteps = 0;
                        batchStarted = Stopwatch.GetTimestamp();
                    }
                }
            }
            if (!IsBuildCurrent(version))
            {
                return;
            }

            // Child blocks have already been measured at this exact width. Keep this root
            // attached when publishing so inherited resources/DPI do not invalidate that work.
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
            InvalidateMeasure();
        }
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
            if (staging != null)
            {
                Children.Remove(staging);
            }
        }
    }
}

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // The preview is a navigation surface, not a second document renderer. Bound both visual
    // nodes and source text so one pathological note cannot stall the hover transition.
    private const int MaximumMeasuredLines = 24;
    // Empty source lines also produce blocks. A twelve-block budget could end an ordinary
    // note before the card was filled; these are safety limits, not a visible line count.
    private const int MaximumRenderedBlocks = 128;
    private const int MaximumRenderedCharacters = 16384;
    private const int MaximumBlockCharacters = 4096;
    private const int MaximumCodeCharacters = 8192;
    private const int MaximumInlineDepth = 6;

    private readonly record struct PreviewLine(string Text, bool Truncated);

    private static readonly Regex InlinePattern = new(
        @"!\[([^\]]*)\]\(([^)]+)\)|\[([^\]]+)\]\(([^)]+)\)|\*\*\*(.+?)\*\*\*|___(.+?)___|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|`([^`]+)`|\*(.+?)\*|_([^_]+)_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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

    public static string MeasureText(string? markdown, string renderMode)
    {
        var measured = new List<string>();
        var fencedCodeState = default(MarkdownFencedCodeState);
        foreach (var previewLine in NormalizeLines(markdown).Take(MaximumMeasuredLines))
        {
            var original = previewLine.Text;
            if (renderMode != MarkdownRenderModes.Full)
            {
                measured.Add(CompactText(original));
                continue;
            }
            var wasInsideFence = fencedCodeState.IsInside;
            var fenceKind = MarkdownFencedCodeScanner.ClassifyLine(
                original,
                fencedCodeState,
                out fencedCodeState);
            if (fenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing ||
                string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var text = wasInsideFence
                ? original.TrimEnd()
                : PrepareInlineTextForMeasurement(StripBlockPrefix(original));
            measured.Add(CompactText(text));
        }

        return string.Join(Environment.NewLine, measured);
    }

    public static int EstimateVisualLines(string? markdown, double widthDip, string renderMode)
    {
        var estimate = 0;
        var measuredCharacters = 0;
        var fencedCodeState = default(MarkdownFencedCodeState);
        foreach (var previewLine in NormalizeLines(markdown).Take(MaximumMeasuredLines))
        {
            var original = previewLine.Text;
            var wasInsideFence = fencedCodeState.IsInside;
            var fenceKind = MarkdownFencedCodeScanner.ClassifyLine(
                original,
                fencedCodeState,
                out fencedCodeState);
            var raw = LimitText(
                original,
                Math.Min(
                    MaximumBlockCharacters,
                    MaximumRenderedCharacters - measuredCharacters),
                out var limitedLine);
            var lineTruncated = previewLine.Truncated || limitedLine;
            measuredCharacters += raw.Length + 1;
            var trimmed = raw.Trim();
            if (fenceKind is MarkdownFenceLineKind.Opening or MarkdownFenceLineKind.Closing)
            {
                estimate += 1;
            }
            else if (trimmed.Length == 0 ||
                     (!wasInsideFence && HorizontalRulePattern.IsMatch(trimmed)))
            {
                estimate += 1;
            }
            else
            {
                var measurementText = wasInsideFence || renderMode != MarkdownRenderModes.Full
                    ? raw.TrimEnd()
                    : PrepareInlineTextForMeasurement(StripBlockPrefix(trimmed));
                var lines = EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                    measurementText,
                    widthDip);
                estimate += wasInsideFence ? Math.Min(3, lines) : Math.Min(4, lines);
            }

            if (lineTruncated || measuredCharacters >= MaximumRenderedCharacters)
            {
                break;
            }
        }
        return Math.Max(1, estimate);
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
        double textZoom = 1.0)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            AddEmptyState(target);
            yield return false;
            yield break;
        }

        var zoom = double.IsFinite(textZoom) ? Math.Clamp(textZoom, 0.5, 1.5) : 1.0;
        var code = new StringBuilder();
        var fencedCodeState = default(MarkdownFencedCodeState);
        var renderedBlocks = 0;
        var renderedCharacters = 0;
        var renderedHeight = 0.0;
        var truncated = false;

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

        foreach (var previewLine in NormalizeLines(markdown))
        {
            // Include the block crossing the bottom edge. Measuring actual wrapped heights
            // avoids the old fixed-block cutoff without building the invisible document tail.
            if ((viewportSize is { } size && renderedHeight > size.Height) ||
                renderedBlocks >= MaximumRenderedBlocks ||
                renderedCharacters >= MaximumRenderedCharacters)
            {
                truncated = true;
                break;
            }

            var sourceLine = renderMode == MarkdownRenderModes.Full
                ? previewLine.Text.TrimEnd()
                : previewLine.Text;
            var wasInsideFence = fencedCodeState.IsInside;
            var fenceKind = MarkdownFencedCodeScanner.ClassifyLine(
                sourceLine,
                fencedCodeState,
                out fencedCodeState);
            var line = LimitText(
                sourceLine,
                Math.Min(
                    MaximumBlockCharacters,
                    MaximumRenderedCharacters - renderedCharacters),
                out var limitedLine);
            var lineTruncated = previewLine.Truncated || limitedLine;
            renderedCharacters += line.Length + 1;
            if (renderMode != MarkdownRenderModes.Full)
            {
                AddBlock(BuildSourceBlock(
                    line, renderMode, wasInsideFence, fenceKind, openExternal));
                renderedBlocks++;
            }
            else if (fenceKind == MarkdownFenceLineKind.Opening)
            {
                code.Clear();
            }
            else if (fenceKind == MarkdownFenceLineKind.Closing)
            {
                AddBlock(BuildCodeBlock(code.ToString()));
                renderedBlocks++;
                code.Clear();
            }
            else if (wasInsideFence)
            {
                var codeLineTruncated = AppendCodeLine(code, line);
                if (codeLineTruncated)
                {
                    truncated = true;
                }
            }
            else
            {
                AddBlock(BuildBlock(line, openExternal));
                renderedBlocks++;
            }

            if (lineTruncated || truncated)
            {
                truncated = true;
                break;
            }
            yield return false;
        }
        if (renderMode == MarkdownRenderModes.Full &&
            (fencedCodeState.IsInside || code.Length > 0) &&
            renderedBlocks < MaximumRenderedBlocks)
        {
            AddBlock(BuildCodeBlock(code.ToString()));
            renderedBlocks++;
        }
        else if (code.Length > 0)
        {
            truncated = true;
        }
        if (target.Children.Count == 0)
        {
            AddEmptyState(target);
        }
        yield return truncated;
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
        Action<string> openExternal)
    {
        var text = NewTextBlock(string.Empty, NoteTypography.FontSize);
        if (renderMode == MarkdownRenderModes.Off)
        {
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
        AddInlineContent(text.Inlines, line[prefixLength..], openExternal, 0, renderMode);
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
        Action<string> openExternal)
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
            AddInlineContent(text.Inlines, heading.Groups[2].Value, openExternal);
            return text;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var text = NewTextBlock(string.Empty, NoteTypography.FontSize);
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            AddInlineContent(text.Inlines, trimmed[1..].TrimStart(), openExternal);
            var host = new Border
            {
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 0, 5, 0),
                Child = text
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
                done);
        }

        var ordered = OrderedListPattern.Match(trimmed);
        if (ordered.Success)
        {
            return BuildListRow(
                $"{ordered.Groups[1].Value}.",
                ordered.Groups[2].Value,
                openExternal,
                done: false);
        }

        var unordered = UnorderedListPattern.Match(trimmed);
        if (unordered.Success)
        {
            return BuildListRow(
                "•",
                unordered.Groups[1].Value,
                openExternal,
                done: false);
        }

        var normal = NewTextBlock(string.Empty, NoteTypography.FontSize);
        AddInlineContent(normal.Inlines, trimmed, openExternal);
        return normal;
    }

    private static FrameworkElement BuildListRow(
        string marker,
        string content,
        Action<string> openExternal,
        bool done)
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
        AddInlineContent(body.Inlines, content, openExternal);
        if (done)
        {
            body.TextDecorations = TextDecorations.Strikethrough;
            body.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement BuildCodeBlock(string code)
    {
        var text = NewTextBlock(code, NoteTypography.CodeFontSize);
        text.FontFamily = NoteTypography.CodeFontFamily;
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

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal)
        => AddInlineContent(target, text, openExternal, depth: 0);

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal,
        int depth,
        string renderMode = MarkdownRenderModes.Full)
    {
        string DisplayText(string source) => renderMode == MarkdownRenderModes.Full
            ? MarkdownInlineSyntax.Unescape(source)
            : source;
        if (depth >= MaximumInlineDepth)
        {
            target.Add(new Run(DisplayText(text)));
            return;
        }

        var scan = MarkdownInlineSyntax.MaskEscapedPunctuation(text);
        var cursor = 0;
        foreach (Match match in InlinePattern.Matches(scan))
        {
            if (match.Index > cursor)
            {
                target.Add(new Run(DisplayText(text[cursor..match.Index])));
            }

            string Group(int index)
            {
                var group = match.Groups[index];
                return text.Substring(group.Index, group.Length);
            }

            var contentGroup = match.Groups[Enumerable.Range(1, 12)
                .First(index => match.Groups[index].Success)];
            if (renderMode != MarkdownRenderModes.Full)
            {
                AddSourceSyntax(target, text[match.Index..contentGroup.Index], renderMode);
            }

            if (match.Groups[1].Success)
            {
                var label = DisplayText(Group(1));
                var image = new Span(new Run(renderMode == MarkdownRenderModes.Full
                    ? string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}"
                    : label));
                image.SetResourceReference(TextElement.ForegroundProperty, "WeakTextBrushKey");
                target.Add(image);
            }
            else if (match.Groups[3].Success)
            {
                target.Add(CreateLink(Group(3), Group(4), openExternal, depth, renderMode));
            }
            else if (match.Groups[5].Success || match.Groups[6].Success)
            {
                var group = match.Groups[5].Success ? 5 : 6;
                var span = new Span
                {
                    FontStyle = FontStyles.Italic
                };
                ApplyStrongTypography(span);
                AddInlineContent(span.Inlines, Group(group), openExternal, depth + 1, renderMode);
                target.Add(span);
            }
            else if (match.Groups[7].Success || match.Groups[8].Success)
            {
                var group = match.Groups[7].Success ? 7 : 8;
                var bold = new Bold();
                ApplyStrongTypography(bold);
                AddInlineContent(bold.Inlines, Group(group), openExternal, depth + 1, renderMode);
                target.Add(bold);
            }
            else if (match.Groups[9].Success)
            {
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlineContent(strike.Inlines, Group(9), openExternal, depth + 1, renderMode);
                target.Add(strike);
            }
            else if (match.Groups[10].Success)
            {
                // CodeFontSize already contains global scaling; the block publication step adds
                // only per-paper zoom and final rounding, for both inline and fenced code.
                var code = new Span(new Run(Group(10)))
                {
                    FontFamily = NoteTypography.CodeFontFamily,
                    FontSize = NoteTypography.CodeFontSize
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "HoverBrushKey");
                target.Add(code);
            }
            else
            {
                var group = match.Groups[11].Success ? 11 : 12;
                var italic = new Italic();
                AddInlineContent(italic.Inlines, Group(group), openExternal, depth + 1, renderMode);
                target.Add(italic);
            }

            cursor = match.Index + match.Length;
            if (renderMode != MarkdownRenderModes.Full)
            {
                AddSourceSyntax(target, text[(contentGroup.Index + contentGroup.Length)..cursor], renderMode);
            }
        }

        if (cursor < text.Length)
        {
            target.Add(new Run(DisplayText(text[cursor..])));
        }
    }

    private static Inline CreateLink(
        string label,
        string value,
        Action<string> openExternal,
        int depth,
        string renderMode)
    {
        var normalizedValue = MarkdownInlineSyntax.Unescape(value);
        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            var fallback = new Span();
            AddInlineContent(fallback.Inlines, label, openExternal, depth + 1, renderMode);
            return fallback;
        }

        var link = new Hyperlink
        {
            NavigateUri = uri,
            Cursor = Cursors.Hand
        };
        AddInlineContent(link.Inlines, label, openExternal, depth + 1, renderMode);
        link.SetResourceReference(TextElement.ForegroundProperty, "LinkBrushKey");
        EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
        link.RequestNavigate += (_, e) =>
        {
            openExternal(e.Uri.AbsoluteUri);
            e.Handled = true;
        };
        return link;
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
                markdown[lineStart..lineEnd],
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

    private static bool AppendCodeLine(StringBuilder target, string line)
    {
        var separatorLength = target.Length > 0 ? Environment.NewLine.Length : 0;
        var remaining = MaximumCodeCharacters - target.Length - separatorLength;
        if (remaining <= 0)
        {
            return true;
        }

        var value = LimitText(line, remaining, out var truncated);
        if (separatorLength > 0)
        {
            target.AppendLine();
        }
        target.Append(value);
        return truncated;
    }

    private static string PrepareInlineTextForMeasurement(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = MarkdownInlineSyntax.IndexOfUnescaped(text, '`', cursor);
            if (start < 0)
            {
                builder.Append(MarkdownInlineSyntax.Unescape(text[cursor..]));
                break;
            }

            var end = MarkdownInlineSyntax.IndexOfUnescaped(text, '`', start + 1);
            if (end < 0)
            {
                builder.Append(MarkdownInlineSyntax.Unescape(text[cursor..]));
                break;
            }

            builder.Append(MarkdownInlineSyntax.Unescape(text[cursor..start]));
            builder.Append(text.AsSpan(start + 1, end - start - 1));
            cursor = end + 1;
        }

        return builder.ToString();
    }

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
        return value[..(maximumLength - 1)] + "…";
    }

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
            return ordered.Groups[2].Value;
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
