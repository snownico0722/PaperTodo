using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// 完全渲染面板(ScrollViewer + StackPanel)的图片渲染上下文。
/// 承载 NoteImageStore、目标宽度与 DPI 缩放,供渲染器在解析块级图片引用时
/// 调用 NoteImageStore.GetBitmapSource 取得真实位图。
/// 设计为可空/可单例——旧调用点没有 imageStore 时降级为 ▧ 占位符。
/// </summary>
internal sealed class FullRenderImageContext
{
    public NoteImageStore? ImageStore { get; }
    public string NoteId { get; }
    public double TargetWidthDip { get; }
    public double DpiScale { get; }

    public bool HasImageStore => ImageStore != null && !string.IsNullOrEmpty(NoteId);

    public FullRenderImageContext(
        NoteImageStore? store,
        string noteId,
        double targetWidthDip,
        double dpiScale)
    {
        ImageStore = store;
        NoteId = noteId ?? "";
        TargetWidthDip = Math.Max(80, targetWidthDip);
        DpiScale = double.IsNaN(dpiScale) || double.IsInfinity(dpiScale) || dpiScale <= 0
            ? 1.0
            : dpiScale;
    }

    /// <summary>
    /// 空上下文: 用于未配置 NoteImageStore 的调用点,所有图片降级为 ▧ 占位符。
    /// </summary>
    public static FullRenderImageContext Empty { get; } = new(
        store: null,
        noteId: "",
        targetWidthDip: 240,
        dpiScale: 1.0);
}

/// <summary>
/// 在完全渲染模式 Border.Tag / Image.Tag 中保存足够信息,使得窗口 resize 时的
/// "就地更新图片宽度"可以在不重新解析 markdown 的前提下重算 displayWidth。
/// 对应编辑模式的 MarkdownTextBox.ImageBlockTag(私有 record)。
/// </summary>
internal sealed record FullRenderImageBlockTag(
    string ImageId,
    MarkdownImageDisplayOptions DisplayOptions,
    int NaturalWidth);

internal sealed class MarkdownEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static MarkdownEdgeCapsulePreviewProvider Instance { get; } = new();

    private MarkdownEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var text = context.ReadMarkdownText();
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            MarkdownEdgeCapsulePreviewRenderer.MeasureText(text),
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 460);
        var lines = MarkdownEdgeCapsulePreviewRenderer.EstimateVisualLines(
            text,
            Math.Max(72, width - 36));
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
    private readonly ScrollViewer _scrollViewer;

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

        _body = new StackPanel
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        _scrollViewer = new ScrollViewer
        {
            Content = _body,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        Grid.SetRow(_scrollViewer, 1);
        Children.Add(_scrollViewer);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var offset = _scrollViewer.VerticalOffset;
        var title = Context.Title;
        _title.Text = title;
        _title.ToolTip = title;
        MarkdownEdgeCapsulePreviewRenderer.RenderInto(
            _body,
            Context.ReadMarkdownText(),
            Context.OpenExternal);
        Dispatcher.BeginInvoke(
            (Action)(() => _scrollViewer.ScrollToVerticalOffset(offset)),
            DispatcherPriority.Loaded);
    }
}

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    // The preview is a navigation surface, not a second document renderer. Bound both visual
    // nodes and source text so one pathological note cannot stall the hover transition.
    private const int MaximumMeasuredLines = 24;
    private const int MaximumRenderedBlocks = 12;
    private const int MaximumRenderedCharacters = 4096;
    private const int MaximumBlockCharacters = 512;
    private const int MaximumCodeCharacters = 2048;
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

    public static string MeasureText(string? markdown)
    {
        var measured = new List<string>();
        var fencedCodeState = default(MarkdownFencedCodeState);
        foreach (var previewLine in NormalizeLines(markdown).Take(MaximumMeasuredLines))
        {
            var original = previewLine.Text;
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

    public static int EstimateVisualLines(string? markdown, double widthDip)
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
                var measurementText = wasInsideFence
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

    public static void RenderInto(
        Panel target,
        string? markdown,
        Action<string> openExternal)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            AddEmptyState(target);
            return;
        }

        var code = new StringBuilder();
        var fencedCodeState = default(MarkdownFencedCodeState);
        var renderedBlocks = 0;
        var renderedCharacters = 0;
        var truncated = false;
        foreach (var previewLine in NormalizeLines(markdown))
        {
            if (renderedBlocks >= MaximumRenderedBlocks ||
                renderedCharacters >= MaximumRenderedCharacters)
            {
                truncated = true;
                break;
            }

            var sourceLine = previewLine.Text.TrimEnd();
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
            if (fenceKind == MarkdownFenceLineKind.Opening)
            {
                code.Clear();
            }
            else if (fenceKind == MarkdownFenceLineKind.Closing)
            {
                target.Children.Add(BuildCodeBlock(code.ToString()));
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
                target.Children.Add(BuildBlock(line, openExternal));
                renderedBlocks++;
            }

            if (lineTruncated || truncated)
            {
                truncated = true;
                break;
            }
        }
        if ((fencedCodeState.IsInside || code.Length > 0) &&
            renderedBlocks < MaximumRenderedBlocks)
        {
            target.Children.Add(BuildCodeBlock(code.ToString()));
            renderedBlocks++;
        }
        else if (code.Length > 0)
        {
            truncated = true;
        }
        if (truncated)
        {
            AddTruncationState(target);
        }
        if (target.Children.Count == 0)
        {
            AddEmptyState(target);
        }
    }

    /// <summary>
    /// 完整渲染 Markdown 到 StackPanel，支持行内样式，无内容限制
    /// </summary>
    /// <param name="target">目标 StackPanel</param>
    /// <param name="markdown">Markdown 文本</param>
    /// <param name="openExternal">打开外部链接的回调</param>
    /// <param name="zoom">文字缩放比例，默认为 1.0</param>
    /// <param name="imageContext">可选的图片渲染上下文(承载 NoteImageStore 与目标宽度等)。
    /// 当 imageContext 为 null 或 HasImageStore 为 false 时,块级图片退化为 ▧ 占位符(向后兼容)。</param>
    public static void RenderFullMarkdownToStackPanel(
        StackPanel target,
        string? markdown,
        Action<string> openExternal,
        double zoom = 1.0,
        FullRenderImageContext? imageContext = null)
    {
        target.Children.Clear();
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        imageContext ??= FullRenderImageContext.Empty;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                target.Children.Add(new Border { Height = 8 });
                continue;
            }

            var trimmed = line.Trim();

            // 块级图片引用: ![alt](i:123) 单独成行,走真实位图渲染(可走 NoteImageStore)
            if (MarkdownImageReferences.TryParseReferenceLine(
                    trimmed,
                    out var imageReference) &&
                imageContext.HasImageStore &&
                imageContext.ImageStore!.TryGetAsset(
                    imageReference.ImageId,
                    out var imageAsset))
            {
                target.Children.Add(BuildImageBlock(imageReference, imageAsset, imageContext));
                continue;
            }

            // 一级标题 # 文字
            if (trimmed.StartsWith("# "))
            {
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    FontSize = Scaled(17, zoom),
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 8, 4, 4)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed.Substring(2), openExternal, zoom);
                target.Children.Add(textBlock);
                continue;
            }

            // 二级标题 ## 文字
            if (trimmed.StartsWith("## "))
            {
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    FontSize = Scaled(15, zoom),
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 6, 4, 4)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed.Substring(3), openExternal, zoom);
                target.Children.Add(textBlock);
                continue;
            }

            // 三级标题 ### 文字
            if (trimmed.StartsWith("### "))
            {
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    FontSize = Scaled(14, zoom),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 4, 4)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed.Substring(4), openExternal, zoom);
                target.Children.Add(textBlock);
                continue;
            }

            // 无序列表 - 文字
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var grid = new Grid { Margin = new Thickness(4, 4, 4, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bullet = new System.Windows.Controls.TextBlock
                {
                    Text = "•",
                    FontSize = Scaled(14, zoom),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                bullet.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                Grid.SetColumn(bullet, 0);
                grid.Children.Add(bullet);

                var textBlock = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                    FontSize = Scaled(NoteTypography.FontSize, zoom)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed.Substring(2), openExternal, zoom);
                Grid.SetColumn(textBlock, 1);
                grid.Children.Add(textBlock);
                target.Children.Add(grid);
                continue;
            }

            // 引用 > 文字
            if (trimmed.StartsWith("> "))
            {
                var border = new Border
                {
                    Margin = new Thickness(4, 4, 4, 4),
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(4)
                };
                border.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");

                var textBlock = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                    Opacity = 0.85,
                    FontSize = Scaled(NoteTypography.FontSize, zoom)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed.Substring(2), openExternal, zoom);
                border.Child = textBlock;
                target.Children.Add(border);
                continue;
            }

            // 删除线 ~~文字~~
            if (trimmed.StartsWith("~~") && trimmed.EndsWith("~~") && trimmed.Length > 4)
            {
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 4, 4),
                    FontWeight = FontWeights.Bold,
                    TextDecorations = System.Windows.TextDecorations.Strikethrough,
                    FontSize = Scaled(NoteTypography.FontSize, zoom)
                };
                textBlock.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
                AddInlineContent(textBlock.Inlines, trimmed[2..^2].ToString(), openExternal, zoom);
                target.Children.Add(textBlock);
                continue;
            }

            // 普通文本行（带行内样式）
            var textBlock2 = new System.Windows.Controls.TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 4),
                FontSize = Scaled(NoteTypography.FontSize, zoom)
            };
            textBlock2.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
            AddInlineContent(textBlock2.Inlines, trimmed, openExternal, zoom);
            target.Children.Add(textBlock2);
        }
    }

    // 按视觉尺寸系数与外层注入的缩放系数一起计算字号,与 MarkdownTextBox 的 ScaledFontSize 保持一致
    private static double Scaled(double baseSize, double zoom)
    {
        return AppTypography.Scale(baseSize) * zoom;
    }

    /// <summary>
    /// 在完全渲染面板中渲染一块真实位图。
    /// 复用 MarkdownTextBox.ResolveImageDisplayWidth 保持与编辑模式一致的尺寸规则,
    /// 调用 NoteImageStore.GetBitmapSource(protectInViewport:true) 走 LRU + 视口保护。
    /// 每个 Image 上挂 Tag=imageId,供外部扫描以维护视口保护集合。
    /// </summary>
    private static FrameworkElement BuildImageBlock(
        MarkdownImageReference reference,
        NoteImageAsset asset,
        FullRenderImageContext imageContext)
    {
        var targetWidth = imageContext.TargetWidthDip;
        var displayWidth = MarkdownTextBox.ResolveImageDisplayWidth(
            reference.DisplayOptions,
            asset,
            targetWidth);
        // 解码像素宽度按当前 DPI 缩放,避免高 DPI 下渲染模糊;
        // NoteImageStore.DecodePixelWidth 内部还会把值 clamp 到 [32, asset.Width]。
        var decodePixelWidth = Math.Max(
            1,
            (int)Math.Ceiling(Math.Min(targetWidth, displayWidth) * imageContext.DpiScale));

        var bitmap = imageContext.ImageStore!.GetBitmapSource(
            asset.Id,
            decodePixelWidth,
            allowDecodeUpgrade: true,
            protectInViewport: true);

        var host = new Border
        {
            Padding = new Thickness(0, 6, 0, 6),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 24,
            Width = targetWidth,
            ToolTip = asset.OriginalName,
            // Tag 缓存必要信息,让窗口 resize 时 PaperWindow.Note.cs 能就地重算
            // displayWidth(对应编辑模式 MarkdownTextBox.ImageBlockTag 的设计)。
            Tag = new FullRenderImageBlockTag(
                reference.ImageId,
                reference.DisplayOptions,
                asset.Width)
        };

        if (bitmap == null)
        {
            // 位图缺失 / 解码失败:与 MarkdownTextBox.CreateImageBlock 同样的占位框
            var fallback = new Border
            {
                Width = Math.Max(120, Math.Min(targetWidth, displayWidth)),
                Height = 42,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var fallbackText = new System.Windows.Controls.TextBlock
            {
                FontSize = AppTypography.Scale(11.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fallbackText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                "WeakTextBrushKey");
            fallbackText.Text = imageContext.ImageStore.IsImageCorrupted(reference.ImageId)
                ? "⚠ image unavailable"
                : "▧ image missing";
            fallback.Child = fallbackText;
            host.Child = fallback;
            return host;
        }

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = displayWidth,
            // 显式计算 Height:仅设置 Width + Stretch=Uniform 在某些布局路径下 WPF
            // 不会按 source 宽高比自动算出 Height(尤其是 Border 作为父级时)。
            // 这里用 bitmap 的实际像素宽高比 + displayWidth 算出 DIPs 高度,避免渲染为 0 高。
            Height = asset.Height > 0 && bitmap.PixelWidth > 0
                ? Math.Round(displayWidth * (double)bitmap.PixelHeight / bitmap.PixelWidth, 1)
                : displayWidth,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            // 与 host 同 Tag,便于就地更新时遍历查找
            Tag = new FullRenderImageBlockTag(
                reference.ImageId,
                reference.DisplayOptions,
                asset.Width)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        host.Child = image;
        return host;
    }

    private static void AddEmptyState(Panel target)
    {
        var empty = NewTextBlock("—", AppTypography.Scale(16));
        empty.Margin = new Thickness(4, 18, 4, 4);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        target.Children.Add(empty);
    }

    private static void AddTruncationState(Panel target)
    {
        var more = NewTextBlock("…", AppTypography.Scale(14));
        more.Margin = new Thickness(4, 6, 4, 2);
        more.HorizontalAlignment = HorizontalAlignment.Center;
        more.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        target.Children.Add(more);
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
        => AddInlineContent(target, text, openExternal, depth: 0, zoom: 1.0);

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal,
        double zoom)
        => AddInlineContent(target, text, openExternal, depth: 0, zoom: zoom);

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal,
        int depth)
        => AddInlineContent(target, text, openExternal, depth, zoom: 1.0);

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal,
        int depth,
        double zoom)
    {
        if (depth >= MaximumInlineDepth)
        {
            target.Add(new Run(MarkdownInlineSyntax.Unescape(text)));
            return;
        }

        var scan = MarkdownInlineSyntax.MaskEscapedPunctuation(text);
        var cursor = 0;
        foreach (Match match in InlinePattern.Matches(scan))
        {
            if (match.Index > cursor)
            {
                target.Add(new Run(MarkdownInlineSyntax.Unescape(text[cursor..match.Index])));
            }

            string Group(int index)
            {
                var group = match.Groups[index];
                return text.Substring(group.Index, group.Length);
            }

            if (match.Groups[1].Success)
            {
                var label = MarkdownInlineSyntax.Unescape(Group(1));
                var image = new Span(new Run(string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}"));
                image.SetResourceReference(TextElement.ForegroundProperty, "WeakTextBrushKey");
                target.Add(image);
            }
            else if (match.Groups[3].Success)
            {
                target.Add(CreateLink(Group(3), Group(4), openExternal, depth, zoom));
            }
            else if (match.Groups[5].Success || match.Groups[6].Success)
            {
                var group = match.Groups[5].Success ? 5 : 6;
                var span = new Span
                {
                    FontWeight = FontWeights.Bold,
                    FontStyle = FontStyles.Italic
                };
                AddInlineContent(span.Inlines, Group(group), openExternal, depth + 1, zoom);
                target.Add(span);
            }
            else if (match.Groups[7].Success || match.Groups[8].Success)
            {
                var group = match.Groups[7].Success ? 7 : 8;
                var bold = new Bold();
                AddInlineContent(bold.Inlines, Group(group), openExternal, depth + 1, zoom);
                target.Add(bold);
            }
            else if (match.Groups[9].Success)
            {
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlineContent(strike.Inlines, Group(9), openExternal, depth + 1, zoom);
                target.Add(strike);
            }
            else if (match.Groups[10].Success)
            {
                var code = new Span(new Run(Group(10)))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = Scaled(NoteTypography.CodeFontSize, zoom)
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "HoverBrushKey");
                target.Add(code);
            }
            else
            {
                var group = match.Groups[11].Success ? 11 : 12;
                var italic = new Italic();
                AddInlineContent(italic.Inlines, Group(group), openExternal, depth + 1, zoom);
                target.Add(italic);
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            target.Add(new Run(MarkdownInlineSyntax.Unescape(text[cursor..])));
        }
    }

    private static Inline CreateLink(
        string label,
        string value,
        Action<string> openExternal,
        int depth,
        double zoom)
    {
        var normalizedValue = MarkdownInlineSyntax.Unescape(value);
        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            var fallback = new Span();
            AddInlineContent(fallback.Inlines, label, openExternal, depth + 1, zoom);
            return fallback;
        }

        var link = new Hyperlink
        {
            NavigateUri = uri,
            Cursor = Cursors.Hand
        };
        AddInlineContent(link.Inlines, label, openExternal, depth + 1, zoom);
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
