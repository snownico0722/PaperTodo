using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

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

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new MarkdownEdgeCapsulePreviewView(context, size));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly StackPanel _body;
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

        _body = new StackPanel();
        _viewport = new MarkdownEdgeCapsulePreviewViewport(_body)
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        Grid.SetRow(_viewport, 1);
        Children.Add(_viewport);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var title = Context.Title;
        _title.Text = title;
        _title.ToolTip = title;
        var markdown = Context.ReadMarkdownText();
        var renderMode = Context.ReadMarkdownRenderMode();
        _viewport.SetContent(size => MarkdownEdgeCapsulePreviewRenderer.RenderInto(
            _body, markdown, Context.OpenExternal, renderMode, size));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewViewport : Panel
{
    private readonly StackPanel _body;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _bodyClip = new();
    private bool _sourceTruncated;
    private Func<Size, bool>? _renderContent;
    private Size? _renderedSize;

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
    }

    public void SetContent(Func<Size, bool> renderContent)
    {
        _renderContent = renderContent;
        _renderedSize = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The host keeps the content at its final size during the shell animation. Wait for
        // that real layout constraint, then retain the excerpt until content or size changes.
        if (_renderContent != null && _renderedSize != availableSize)
        {
            _renderedSize = availableSize;
            try
            {
                _sourceTruncated = _renderContent(availableSize);
            }
            catch
            {
                // Keep an optional preview failure out of the WPF layout boundary. Like the
                // live view, retry only on a later invalidation, not on every layout pass.
                _body.Children.Clear();
                _sourceTruncated = true;
            }
        }

        var naturalSize = new Size(availableSize.Width, double.PositiveInfinity);
        _body.Measure(naturalSize);
        _overflowIndicator.Measure(naturalSize);
        return new Size(
            Math.Min(availableSize.Width, Math.Max(_body.DesiredSize.Width, _overflowIndicator.DesiredSize.Width)),
            Math.Min(availableSize.Height, _body.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The card only shows its top excerpt. Use the actual body height so blank source
        // lines cannot consume a fixed line budget and leave usable card space empty.
        var overflow = _sourceTruncated || _body.DesiredSize.Height > finalSize.Height;
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _overflowIndicator.DesiredSize.Height) : 0;
        var visibleHeight = finalSize.Height - indicatorHeight;
        _bodyClip.Rect = new Rect(0, 0, finalSize.Width, visibleHeight);
        _body.Arrange(new Rect(0, 0, finalSize.Width, _body.DesiredSize.Height));
        _overflowIndicator.Opacity = overflow ? 1 : 0;
        _overflowIndicator.Arrange(new Rect(0, visibleHeight, finalSize.Width, indicatorHeight));
        return finalSize;
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
        Size? viewportSize = null)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            AddEmptyState(target);
            return false;
        }

        var code = new StringBuilder();
        var fencedCodeState = default(MarkdownFencedCodeState);
        var renderedBlocks = 0;
        var renderedCharacters = 0;
        var renderedHeight = 0.0;
        var truncated = false;

        void AddBlock(FrameworkElement block)
        {
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
        return truncated;
    }

    private static void AddEmptyState(Panel target)
    {
        var empty = NewTextBlock("—", AppTypography.Scale(16));
        empty.Margin = new Thickness(4, 18, 4, 4);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        target.Children.Add(empty);
    }

    // Basic/Enhanced keep the source layout, just as the note body does. Full continues to
    // use the compact preview blocks; Off emits literal text without Markdown decoration.
    private static FrameworkElement BuildSourceBlock(
        string line,
        string renderMode,
        bool wasInsideFence,
        MarkdownFenceLineKind fenceKind,
        Action<string> openExternal)
    {
        var text = NewTextBlock(string.Empty, NoteTypography.FontSize);
        text.Margin = new Thickness(0, 2, 0, 3);
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
            var level = heading.Groups[1].Value.Length;
            text.FontSize = AppTypography.Scale(Math.Max(13, 19 - level));
            text.FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
            prefixLength += heading.Groups[2].Index;
        }
        else if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            text.SetResourceReference(TextBlock.BackgroundProperty, "HoverBrushKey");
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
            return new Border { Height = AppTypography.Scale(6) };
        }

        if (HorizontalRulePattern.IsMatch(trimmed))
        {
            var rule = new Border
            {
                Height = 1,
                Margin = new Thickness(2, 7, 2, 7)
            };
            rule.SetResourceReference(Border.BackgroundProperty, "PaperBorderBrushKey");
            return rule;
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
            var level = heading.Groups[1].Value.Length;
            var text = NewTextBlock(
                string.Empty,
                AppTypography.Scale(Math.Max(13, 19 - level)));
            text.Margin = new Thickness(0, 5, 0, 3);
            text.FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
            AddInlineContent(text.Inlines, heading.Groups[2].Value, openExternal);
            return text;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var text = NewTextBlock(string.Empty, AppTypography.Scale(12));
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            AddInlineContent(text.Inlines, trimmed[1..].TrimStart(), openExternal);
            var host = new Border
            {
                Margin = new Thickness(4, 3, 0, 3),
                Padding = new Thickness(8, 4, 5, 4),
                CornerRadius = new CornerRadius(4),
                Child = text
            };
            host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
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
        normal.Margin = new Thickness(0, 2, 0, 3);
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
            Margin = new Thickness(2, 2, 0, 2)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var markerText = NewTextBlock(marker, NoteTypography.FontSize - 2.5);
        markerText.Width = marker.Length > 2 ? AppTypography.Scale(28) : AppTypography.Scale(22);
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
        text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        text.LineHeight = AppTypography.Scale(16);
        var host = new Border
        {
            Margin = new Thickness(1, 4, 1, 4),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(5),
            Child = text
        };
        host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        return host;
    }

    private static TextBlock NewTextBlock(string text, double fontSize) => new()
    {
        Text = text,
        FontFamily = NoteTypography.FontFamily,
        FontSize = fontSize,
        FontWeight = FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Max(fontSize + AppTypography.Scale(4), AppTypography.Scale(17))
    };

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
                    FontWeight = FontWeights.Bold,
                    FontStyle = FontStyles.Italic
                };
                AddInlineContent(span.Inlines, Group(group), openExternal, depth + 1, renderMode);
                target.Add(span);
            }
            else if (match.Groups[7].Success || match.Groups[8].Success)
            {
                var group = match.Groups[7].Success ? 7 : 8;
                var bold = new Bold();
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
                // CodeFontSize 已含全局缩放,直接用作字号:与下方代码块(BuildCodeBlock)及编辑器
                // "行内代码与代码块同字号"约定一致。切勿再套 AppTypography.Scale,否则会二次缩放。
                var code = new Span(new Run(Group(10)))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
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