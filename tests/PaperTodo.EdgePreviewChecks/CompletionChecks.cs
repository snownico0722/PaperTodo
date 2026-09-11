using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using PaperTodo;

internal static partial class Program
{
    private static StackPanel PublishedBody(MarkdownEdgeCapsulePreviewViewport viewport) =>
        viewport.Children.OfType<StackPanel>().Single(panel => panel.Opacity > 0);

    private static string PreviewText(DependencyObject element)
    {
        if (element is MarkdownEdgePreviewParagraph paragraph) return paragraph.VisibleText;
        if (element is TextBlock text) return new TextRange(text.ContentStart, text.ContentEnd).Text;
        return string.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element))
            .Select(i => PreviewText(VisualTreeHelper.GetChild(element, i))));
    }

    private static IEnumerable<Hyperlink> Links(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink link) yield return link;
            if (inline is Span span)
                foreach (var nested in Links(span.Inlines)) yield return nested;
        }
    }

    private static void Checks()
    {
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic,
            MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            var source = "[入口](https://example.com)\n" + string.Join('\n',
                Enumerable.Range(1, 40).Select(i => $"第{i}行 **粗体** 和 `code`"));
            var paper = new PaperData { Content = source, TextZoom = 1.3 };
            var invalidation = new EdgeCapsulePreviewInvalidationSource();
            string? opened = null;
            var context = new EdgeCapsulePreviewContext(paper, () => "测试笔记", false,
                () => source, () => mode, (_, _) => false, _ => false, () => new Style(),
                () => "", url => opened = url, invalidation);
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
            view.PrepareForFirstDisplay();
            var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
            Require(viewport.Opacity == 0 && !viewport.IsHitTestVisible,
                "unprepared first body is neither visible nor interactive");
            var window = new Window { Content = view, Width = 420, Height = 220,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Pump();
                var body = PublishedBody(viewport);
                Require(viewport.Opacity == 1 && viewport.IsHitTestVisible && body.Children.Count > 0,
                    "first body publishes atomically and becomes interactive");
                var excerpt = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source, mode);
                Require(excerpt.Lines.Count <= 16 &&
                    string.Join('\n', excerpt.Lines.Select(line => line.Text)).Length <= 6000,
                    "same 16-block/6000-character excerpt budget");
                Require(!PreviewText(body).Contains("第40行"), "off-budget content is never realized");
                Require(!Elements(view).Any(e => e is ScrollViewer or ScrollBar), "no scrolling controls");
                Require(viewport.Children.OfType<TextBlock>().Single().Opacity == 1, "omitted tail is indicated");
                var top = body.TranslatePoint(new Point(), viewport);
                foreach (var delta in new[] { -120, 120 })
                    body.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = Mouse.MouseWheelEvent });
                body.BringIntoView(); Pump();
                Require(body.TranslatePoint(new Point(), viewport) == top, "wheel and bring-into-view cannot scroll");
                Require(paper.Content == source, "preview never edits the source note");
                if (mode != MarkdownRenderModes.Off)
                {
                    var link = Elements(body).OfType<TextBlock>().SelectMany(t => Links(t.Inlines)).First();
                    Require(EdgeCapsulePreviewInteraction.GetConsumesPointer(link), "real link retains host input routing");
                    link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "")
                    { RoutedEvent = Hyperlink.RequestNavigateEvent });
                    Require(opened == "https://example.com/", "link invokes the existing callback without opening a browser");
                }
                descriptor.SetVisibility?.Invoke(false);
                Require(!viewport.IsHitTestVisible, "retract immediately disables old links");
                descriptor.SetVisibility?.Invoke(true); Pump();
                Require(ReferenceEquals(body, PublishedBody(viewport)), "unchanged reactivation reuses its completed body");

                source = "更新后的正文"; paper.TextZoom = 0.8;
                invalidation.Invalidate(); Pump();
                body = PublishedBody(viewport);
                Require(PreviewText(body).Contains(source), "live invalidation replaces content");
                var text = body.Children.OfType<TextBlock>().Single();
                Require(Math.Abs(text.FontSize - Math.Round(NoteTypography.FontSize * 0.8, 1)) < 0.01,
                    "new body uses current zoom");
                source = ""; invalidation.Invalidate(); Pump();
                Require(PreviewText(PublishedBody(viewport)).Contains("—") &&
                    viewport.Children.OfType<TextBlock>().Single().Opacity == 0, "empty state clears overflow");
                source = "卸载后的新正文";
                window.Content = null; Pump();
                invalidation.Invalidate();
                window.Content = view; Pump();
                Require(PreviewText(PublishedBody(viewport)).Contains(source), "reattachment rebuilds current content");
                Console.WriteLine("PASS production preview " + mode);
            }
            finally { window.Close(); Pump(); }
        }
        CheckReuseAndInvalidation();
        CheckDenseDispatch();
        CheckThemeInvalidation();
        CheckHostPublication();
    }

    private static void CheckReuseAndInvalidation()
    {
        var viewport = new MarkdownEdgeCapsulePreviewViewport(new StackPanel());
        var window = new Window { Content = viewport, Width = 320, Height = 180,
            ShowActivated = false, ShowInTaskbar = false };
        var builds = 0;
        IEnumerable<bool> Render(Panel target, Size bounds)
        {
            builds++;
            foreach (var step in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(target,
                "**当前内容** [link](https://example.com)", _ => { }, MarkdownRenderModes.Full, bounds)) yield return step;
        }
        viewport.SetContent(Render);
        try
        {
            window.Show(); Pump();
            var body = PublishedBody(viewport); var initialBuilds = builds;
            for (var i = 0; i < 5; i++)
            {
                viewport.SetPreviewActive(false); Pump();
                viewport.SetPreviewActive(true); Pump();
            }
            Require(builds == initialBuilds && ReferenceEquals(body, PublishedBody(viewport)),
                "five retract/resume cycles perform no extra preparation");
            viewport.Visibility = Visibility.Hidden; Pump();
            viewport.Visibility = Visibility.Visible; Pump();
            Require(builds == initialBuilds, "visibility-only cycle reuses the current completed result");

            viewport.SetPreviewActive(false);
            viewport.SetContent(Render); Pump();
            Require(builds == initialBuilds, "inactive content changes do not run background preparation");
            viewport.SetPreviewActive(true); Pump();
            Require(builds == initialBuilds + 1 && !ReferenceEquals(body, PublishedBody(viewport)),
                "content invalidation revokes reuse even when the excerpt text is equal");
            var priorBuilds = builds;
            viewport.SetPreviewActive(false); window.Width += 70; Pump();
            viewport.SetPreviewActive(true); Pump();
            Require(builds == priorBuilds + 1, "new width never reuses old wrapping");
            priorBuilds = builds;
            // Exercise the protected notification boundary, not a pretend mixed-monitor test.
            typeof(MarkdownEdgeCapsulePreviewViewport).GetMethod("OnDpiChanged",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(viewport,
                new object[] { new DpiScale(1, 1), new DpiScale(1.5, 1.5) });
            Pump();
            Require(builds == priorBuilds + 1, "DPI notification invalidates the completed result");
            priorBuilds = builds;
            window.Content = null; Pump(); window.Content = viewport; Pump();
            Require(builds == priorBuilds + 1, "unload revokes reuse and cancels ownership of pending work");
            Console.WriteLine($"PASS same-view reuse: 5 cycles, extra builds=0; content/width/DPI notification/unload each rebuild");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckDenseDispatch()
    {
        var dense = string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd();
        Require(dense.Length < 256, "the density fixture is short source text");
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            var panel = new StackPanel();
            var prepared = 0;
            foreach (var step in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(panel, dense, _ => { }, mode, new Size(160, 90)))
            {
                var count = Elements(panel).OfType<MarkdownEdgePreviewParagraph>().Sum(p => p.FormattedLines);
                Require(count - prepared <= 1, "dense short row yields between real visual lines");
                prepared = count;
            }
            Require(Elements(panel).OfType<MarkdownEdgePreviewParagraph>().Any() && prepared > 0,
                "short dense rows use bounded line preparation");
            var ordinary = new StackPanel();
            MarkdownEdgeCapsulePreviewRenderer.RenderInto(ordinary, new string('a', 150), _ => { }, mode, new Size(160, 90));
            Require(!Elements(ordinary).OfType<MarkdownEdgePreviewParagraph>().Any(),
                "ordinary short rows retain the simple text path");
        }
        Console.WriteLine("PASS short dense paragraphs and plain short rows use appropriate existing paths");
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Glyphs(child)) yield return nested;
    }

    private static void CheckThemeInvalidation()
    {
        var source = string.Concat(Enumerable.Repeat("**内容** plain text ", 70));
        var invalidation = new EdgeCapsulePreviewInvalidationSource();
        var context = new EdgeCapsulePreviewContext(new PaperData(), () => "主题", false,
            () => source, () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
            () => new Style(), () => "", _ => { }, invalidation);
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var view = (EdgeCapsuleLivePreviewView)descriptor.CreateContent(descriptor.Size);
        var window = new Window { Content = view, Width = 420, Height = 220,
            ShowActivated = false, ShowInTaskbar = false };
        window.Resources["TextBrushKey"] = Brushes.DarkRed;
        try
        {
            view.PrepareForFirstDisplay(); window.Show(); Pump();
            var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
            var oldBody = PublishedBody(viewport);
            bool HasColor(Color color) => Elements(PublishedBody(viewport)).OfType<MarkdownEdgePreviewParagraph>()
                .SelectMany(p => Glyphs(VisualTreeHelper.GetDrawing(p)))
                .Any(g => g.ForegroundBrush is SolidColorBrush brush && brush.Color == color);
            Require(HasColor(Colors.DarkRed), "prepared drawing uses host foreground");
            descriptor.SetVisibility?.Invoke(false);
            window.Resources["TextBrushKey"] = Brushes.DarkBlue;
            invalidation.Invalidate(); Pump();
            descriptor.SetVisibility?.Invoke(true); Pump();
            Require(!ReferenceEquals(oldBody, PublishedBody(viewport)) && HasColor(Colors.DarkBlue),
                "theme invalidation while inactive replaces frozen drawing with new resources");
            Console.WriteLine("PASS frozen drawing follows theme invalidation during retraction");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckHostPublication()
    {
        using var host = NewHost();
        var source = string.Concat(Enumerable.Repeat("**a** *b* `c` [link](https://example.com) ", 100));
        var context = new EdgeCapsulePreviewContext(new PaperData(), () => "真实宿主", false,
            () => source, () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
            () => new Style(), () => "", _ => { }, new());
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var view = descriptor.CreateContent(new EdgeCapsulePreviewSize(420, 300));
        Require(host.StagePreviewContent(view, 398, 300), "real host stages production body");
        var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
        Require(viewport.Opacity == 0 && !viewport.IsHitTestVisible,
            "staging host never exposes an unprepared interactive body");
        // ProfileOne additionally exercises visible Host/Presenter expansion without a fake frame driver.
        descriptor.SetVisibility?.Invoke(false);
        host.ClearPreviewContent(); Pump();
        Require(viewport.Children.OfType<StackPanel>().All(p => p.Children.Count == 0),
            "clearing an unready host cancels publication");
        Console.WriteLine("PASS real Host staging/clearing preserves first-publication boundary");
    }

    private static void ExportPreviewPixels(string folder)
    {
        Directory.CreateDirectory(folder);
        var fixtures = new[] {
            "ordinary 中文 e\u0301 العربية 😀 **粗体** *italic* ~~strike~~ `code`",
            "**a *nested* b** <u>under ~~strike~~</u> **`code`**",
            "[a **bold** *italic*](https://example.com)[second](https://example.com)",
            "## Heading\n> 引用 **strong**\n12. list *item*\n- [x] task\n---",
            "```\n\n**literal**\n\n[not-link](https://example.com)\n```",
            string.Concat(Enumerable.Repeat("**加粗** *斜体* ~~删除~~ `code` [a **styled** link](https://example.com) 中文 ", 45)),
            string.Join('\n', Enumerable.Range(1, 16).Select(i => $"第{i}行 **加粗** *italic* `code`")),
            string.Concat(Enumerable.Repeat("[**a** *b*](https://example.com) ", 120)),
            string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd(),
            string.Join('\n', Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd(), 12))
        };
        try
        {
            foreach (var sharp in new[] { false, true })
            foreach (var zoom in new[] { 0.7, 1.3 })
            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            for (var i = 0; i < fixtures.Length; i++)
            {
                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                    textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                var panel = new StackPanel { Width = 420, Height = 320, Background = Brushes.White, ClipToBounds = true };
                panel.Resources["TextBrushKey"] = Brushes.Black;
                panel.Resources["WeakTextBrushKey"] = Brushes.Gray;
                panel.Resources["LinkBrushKey"] = Brushes.Blue;
                panel.Resources["HoverBrushKey"] = Brushes.LightGray;
                var window = new Window { Content = panel, Width = 500, Height = 420, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); Pump();
                    MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel, fixtures[i], _ => { }, mode, new Size(420, 320), zoom);
                    Pump(); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(panel);
                    var width = (int)Math.Ceiling(420 * dpi.DpiScaleX);
                    var height = (int)Math.Ceiling(320 * dpi.DpiScaleY);
                    var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0);
                    File.WriteAllBytes(Path.Combine(folder, $"{sharp}-{zoom}-{mode}-{i}.rgba"), bytes);
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally { AppTypography.Configure(UiFontPresets.Default); }
    }
}
