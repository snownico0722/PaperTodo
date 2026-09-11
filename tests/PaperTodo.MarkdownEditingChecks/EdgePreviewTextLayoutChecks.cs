using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void RunEdgePreviewTextLayoutChecks(Action<string, Action> check)
    {
        check("Sixteen admitted rows reserve their actual height and one overflow indicator", () =>
        {
            try
            {
                foreach (var sharp in new[] { false, true })
                    foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
                        foreach (var zoom in new[] { 0.5, 1.0, 1.5 })
                        {
                            AppTypography.Configure(UiFontPresets.Default, textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                            var source = string.Join('\n', Enumerable.Range(1, 128).Select(i => $"第{i}行"));
                            var context = new EdgeCapsulePreviewContext(new PaperData { TextZoom = zoom }, () => "笔记", false,
                                () => source, () => mode, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
                            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
                            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
                            view.PrepareForFirstDisplay();
                            var host = new Border
                            {
                                Padding = new Thickness(0, 0, 22, 0),
                                Width = descriptor.Size.WidthDip,
                                Height = descriptor.Size.HeightDip,
                                Child = view,
                                HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Top
                            };
                            var window = new Window { Content = host, Width = 600, Height = 600, ShowActivated = false, ShowInTaskbar = false };
                            try
                            {
                                window.Show(); Pump();
                                var viewport = EdgePreviewElements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
                                var body = viewport.Children.OfType<StackPanel>().Single();
                                if (descriptor.Size.HeightDip < 410)
                                {
                                    Equal(16, body.Children.Count, "all sixteen admitted rows are present");
                                    var gap = body.Clip.Bounds.Height - body.DesiredSize.Height;
                                    Require(gap >= -0.1 && gap <= 2, $"no unused source rows below the excerpt: {gap:F3} DIP");
                                }
                                Equal(1.0, viewport.Children.OfType<TextBlock>().Single().Opacity, "the omitted tail is indicated");
                            }
                            finally { window.Close(); Pump(); }
                        }
            }
            finally { AppTypography.Configure(UiFontPresets.Default); }
        });

        check("Value inline runs retain the existing preview grammar", () =>
        {
            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
                foreach (var text in new[] { "**粗体** `code` [链接](https://example.com)", "***both*** ~~strike~~ __bold__ ![图](i:asset)",
                @"\*literal\* **a *nested* b** [**label**](https://example.com/a\*b)", "`a\\*b` [bad](javascript:no) ![](i:asset)" })
                {
                    var panel = new StackPanel();
                    MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, text, _ => { }, mode);
                    Equal(string.Concat(EdgePreviewElements(panel).OfType<TextBlock>().Select(t => InlineText(t.Inlines))), string.Concat(MarkdownEdgeCapsulePreviewRenderer.InlinePieces(text, mode).Select(p => p.Text)),
                        "optimized layout does not change visible syntax or labels");
                }
        });

        check("Long paragraph layout preserves visible pixels and link activation", () =>
        {
            try
            {
                foreach (var sharp in new[] { false, true })
                    foreach (var zoom in new[] { 0.7, 1.0, 1.3 })
                        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
                            foreach (var prefix in mode == MarkdownRenderModes.Full ? new[] { "", "## ", "> ", "- [x] " } : new[] { "", "## ", "> ", "- [x] ", "```\n" })
                            {
                                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                                    sharp ? 1.2 : 1.0, textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                                NoteTypography.Configure(sharp ? VisualTextSizes.Large : VisualTextSizes.Medium, sharp);
                                var source = prefix + string.Concat(Enumerable.Repeat("**粗体** `code` [链接](https://example.com) 中文 ", 12)).TrimEnd();
                                var eager = new StackPanel { Background = Brushes.White }; var bounded = new StackPanel { Background = Brushes.White };
                                var host = new Grid(); host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) }); host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
                                host.Resources["TextBrushKey"] = Brushes.Black;
                                host.Resources["WeakTextBrushKey"] = Brushes.Gray;
                                host.Resources["LinkBrushKey"] = Brushes.Blue;
                                host.Resources["HoverBrushKey"] = Brushes.LightGray;
                                host.Children.Add(eager); Grid.SetColumn(bounded, 1); host.Children.Add(bounded);
                                var window = new Window { Content = host, Width = 850, Height = 600, ShowInTaskbar = false, ShowActivated = false };
                                string? opened = null;
                                try
                                {
                                    window.Show(); Pump();
                                    MarkdownEdgeCapsulePreviewRenderer.RenderInto(eager, source, _ => { }, mode, textZoom: zoom);
                                    MarkdownEdgeCapsulePreviewRenderer.RenderInto(bounded, source, uri => opened = uri, mode, new Size(eager.ActualWidth, 180), zoom);
                                    Pump(); host.UpdateLayout();
                                    var paragraph = EdgePreviewElements(bounded).OfType<MarkdownEdgePreviewParagraph>().Single();
                                    Require(EdgePreviewElements(eager).OfType<TextBlock>().Any(text => InlineText(text.Inlines).StartsWith(paragraph.VisibleText, StringComparison.Ordinal)), "formatted text is a visible prefix");
                                    var first = CapturePreviewPixels(eager, 120, mode + "-expected"); var second = CapturePreviewPixels(bounded, 120, mode + "-actual");
                                    var changed = first.Zip(second).Count(pair => Math.Abs(pair.First - pair.Second) > 32);
                                    Console.WriteLine($"  Edge paragraph pixels {mode} sharp={sharp} zoom={zoom} prefix={prefix}: changed={changed}/{first.Length}, lines={paragraph.FormattedLines}");
                                    if (changed >= first.Length * 0.02)
                                    {
                                        IEnumerable<GlyphRun> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing g ? new[] { g.GlyphRun }
                                            : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : Enumerable.Empty<GlyphRun>();
                                        foreach (var element in new Visual[] { EdgePreviewElements(eager).OfType<TextBlock>().Last(), paragraph })
                                            Console.WriteLine("  glyphs " + string.Join("; ", Glyphs(VisualTreeHelper.GetDrawing(element)).Take(15)
                                                .Select(g => $"{g.FontRenderingEmSize:F2}dpi{g.PixelsPerDip:F2}@{g.BaselineOrigin.X:F3},{g.BaselineOrigin.Y:F3}")));
                                    }
                                    Require(changed < first.Length * 0.02, "line positions and visible styles match the TextBlock reference");
                                    var preparedLines = paragraph.FormattedLines;
                                    paragraph.InvalidateMeasure(); paragraph.InvalidateVisual(); host.UpdateLayout(); Pump();
                                    Equal(preparedLines, paragraph.FormattedLines, "layout and repaint reuse prepared lines");
                                    if (mode != MarkdownRenderModes.Off && prefix != "```\n")
                                    {
                                        var link = paragraph.Children.OfType<Border>().First();
                                        link.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                                        Equal("https://example.com/", opened, "visible link retains its action");
                                    }
                                }
                                finally { window.Close(); Pump(); }
                            }
            }
            finally
            {
                AppTypography.Configure(UiFontPresets.Default);
                NoteTypography.Configure(VisualTextSizes.Medium, false);
            }
        });
    }

    private static string InlineText(InlineCollection inlines) => string.Concat(inlines.Cast<Inline>().Select(inline => inline switch
    {
        Run run => run.Text,
        Span span => InlineText(span.Inlines),
        LineBreak => "\n",
        _ => ""
    }));

    private static byte[] CapturePreviewPixels(FrameworkElement element, int height, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var widthPixels = (int)Math.Floor(element.ActualWidth * dpi.DpiScaleX);
        var heightPixels = (int)Math.Floor(height * dpi.DpiScaleY);
        // Render the shared parent and crop each column. A VisualBrush independently normalizes
        // overflowing content bounds and can scale/shift the two captures differently.
        var root = (FrameworkElement)VisualTreeHelper.GetParent(element);
        var origin = element.TranslatePoint(new Point(), root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling((height + origin.Y) * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Round(origin.X * dpi.DpiScaleX),
            (int)Math.Round(origin.Y * dpi.DpiScaleY), widthPixels, heightPixels));
        if (Environment.GetEnvironmentVariable("PAPERTODO_EDGE_CHECK_IMAGES") is { Length: > 0 } folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(stream);
        }
        var pixels = new byte[widthPixels * heightPixels * 4]; cropped.CopyPixels(pixels, widthPixels * 4, 0);
        return pixels;
    }
}
