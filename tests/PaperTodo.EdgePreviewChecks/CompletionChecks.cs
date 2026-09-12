using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static MarkdownPreviewArtifactSurface PublishedBody(MarkdownEdgeCapsulePreviewViewport viewport)
    {
        UntilReview(() => viewport.Opacity == 1 && viewport.IsHitTestVisible && viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(),
            "the complete artifact is published");
        viewport.UpdateLayout();
        return viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Single();
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Glyphs(child)) yield return nested;
    }

    private static string PreviewText(DependencyObject element)
    {
        if (element is MarkdownPreviewArtifactSurface surface)
            return string.Concat(Glyphs(surface.Artifact.Drawing).Select(g =>
                new string(g.GlyphRun.Characters?.ToArray() ?? Array.Empty<char>()))).Replace("\u200B", "");
        if (element is TextBlock text) return new TextRange(text.ContentStart, text.ContentEnd).Text;
        return string.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element))
            .Select(i => PreviewText(VisualTreeHelper.GetChild(element, i))));
    }

    // Test-only completed-task adapter. Product has no synchronous renderer or iterator seam.
    private static bool RenderForCheck(Panel target, string source, Action<string> openExternal,
        string mode = MarkdownRenderModes.Full, Size? viewport = null, double textZoom = 1)
    {
        var size = viewport ?? new Size(double.IsFinite(target.Width) ? target.Width : 420, 10000);
        var artifact = AwaitWorkerCheck(MarkdownEdgeCapsulePreviewRenderer.PrepareArtifactAsync(target,
            MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source, mode), size, textZoom, false, default));
        Require(artifact != null, "stable input builds an artifact");
        target.Children.Clear();
        target.Children.Add(new MarkdownPreviewArtifactSurface(artifact!, openExternal));
        target.UpdateLayout();
        return artifact!.Truncated;
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
            Require(viewport.Opacity == 0 && !viewport.IsHitTestVisible, "unprepared body is inert");
            var window = new Window { Content = view, Width = 420, Height = 220,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); var body = PublishedBody(viewport);
                Require(viewport.IsHitTestVisible && body.Artifact.Drawing.IsFrozen, "one complete immutable result publishes");
                Require(!PreviewText(body).Contains("第40行"), "off-budget text is never realized");
                Require(!Elements(view).Any(e => e is ScrollViewer or ScrollBar), "no scrolling controls");
                Require(viewport.Children.OfType<TextBlock>().Single().Opacity == 1, "omitted tail is indicated");
                var top = body.TranslatePoint(new Point(), viewport);
                foreach (var delta in new[] { -120, 120 })
                    body.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = Mouse.MouseWheelEvent });
                body.BringIntoView(); Pump();
                Require(body.TranslatePoint(new Point(), viewport) == top && paper.Content == source,
                    "wheel and bring-into-view neither scroll nor edit the note");
                if (mode != MarkdownRenderModes.Off)
                {
                    var link = body.Children.OfType<Button>().First();
                    Require(EdgeCapsulePreviewInteraction.GetConsumesPointer(link), "links retain host input routing");
                    link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(opened == "https://example.com/", "link invokes the existing callback");
                }
                descriptor.SetVisibility?.Invoke(false);
                Require(!viewport.IsHitTestVisible, "retract immediately disables links");
                descriptor.SetVisibility?.Invoke(true); Pump();
                Require(ReferenceEquals(body, PublishedBody(viewport)), "unchanged resume reuses its own surface");
                source = "更新后的正文"; paper.TextZoom = 0.8;
                invalidation.Invalidate();
                UntilReview(() => PreviewText(viewport).Contains(source) && viewport.IsHitTestVisible, "live edit replaces content");
                body = PublishedBody(viewport);
                Require(Glyphs(body.Artifact.Drawing).All(g => Math.Abs(g.GlyphRun.FontRenderingEmSize -
                    Math.Round(NoteTypography.FontSize * 0.8, 1)) < 0.01), "fresh result uses current zoom");
                source = ""; invalidation.Invalidate();
                UntilReview(() => PreviewText(viewport).Contains("—") && viewport.IsHitTestVisible, "empty state publishes");
                viewport.UpdateLayout();
                Require(viewport.Children.OfType<TextBlock>().Single().Opacity == 0, "empty state clears overflow");
                source = "卸载后的新正文";
                window.Content = null; Pump(); invalidation.Invalidate(); window.Content = view;
                UntilReview(() => PreviewText(viewport).Contains(source) && viewport.IsHitTestVisible, "reattach reads current source");
                Console.WriteLine("PASS production artifact preview " + mode);
            }
            finally { window.Close(); Pump(); }
        }
        CheckReuseAndInvalidation();
        CheckThemeInvalidation();
        CheckHostPublication();
        CheckPendingBoundaries();
    }

    private static void CheckReuseAndInvalidation()
    {
        var viewport = new MarkdownEdgeCapsulePreviewViewport();
        var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent("**当前内容**", MarkdownRenderModes.Full);
        viewport.SetContent(content, _ => { });
        var window = new Window { Content = viewport, Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false };
        void WaitForNew(MarkdownPreviewArtifactSurface old) => UntilReview(() => viewport.IsHitTestVisible &&
            viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(p => !ReferenceEquals(old, p)), "invalidated result is replaced");
        try
        {
            window.Show(); var body = PublishedBody(viewport);
            for (var i = 0; i < 5; i++) { viewport.SetPreviewActive(false); Pump(); viewport.SetPreviewActive(true); Pump(); }
            Require(ReferenceEquals(body, PublishedBody(viewport)), "five unchanged resumes do not rebuild");
            viewport.Visibility = Visibility.Hidden; Pump(); viewport.Visibility = Visibility.Visible; Pump();
            Require(ReferenceEquals(body, PublishedBody(viewport)), "visibility-only cycle reuses completed result");
            viewport.SetPreviewActive(false); viewport.SetContent(content, _ => { }); Pump();
            Require(ReferenceEquals(body, viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Single()) && !viewport.IsHitTestVisible,
                "inactive invalidation does not run preparation or enable stale input");
            viewport.SetPreviewActive(true); WaitForNew(body); body = PublishedBody(viewport);
            window.Width += 70; WaitForNew(body); body = PublishedBody(viewport);
            typeof(MarkdownEdgeCapsulePreviewViewport).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewport, new object[] { new DpiScale(1, 1), new DpiScale(1.5, 1.5) });
            WaitForNew(body); body = PublishedBody(viewport);
            window.Content = null; Pump();
            Require(body.Parent == null && !viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(), "unload releases the mounted surface");
            window.Content = viewport; WaitForNew(body);
            Console.WriteLine("PASS same-view reuse; content/width/DPI/unload revoke it");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckPendingBoundaries()
    {
        foreach (var boundary in new[] { "replace", "retract", "unload", "clear-cache" })
        {
            var viewport = new MarkdownEdgeCapsulePreviewViewport();
            var window = new Window { Content = viewport, Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false };
            void Set(string text) => viewport.SetContent(MarkdownEdgeCapsulePreviewRenderer.CaptureContent(text, MarkdownRenderModes.Full), _ => { });
            try
            {
                Set("previous"); window.Show(); var old = PublishedBody(viewport);
                using (HoldWorker(MarkdownLayoutWorker.Shared))
                {
                    Set("obsolete " + new string('文', 1000));
                    UntilReview(() => MarkdownLayoutWorker.OutstandingRequests > 0, "cold demand awaits the real STA");
                    Require(ReferenceEquals(old, viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Single()) && !viewport.IsHitTestVisible,
                        "pending result has no partial UI or stale interactive links");
                    if (boundary == "replace") Set("replacement");
                    if (boundary == "retract") viewport.SetPreviewActive(false);
                    if (boundary == "unload") window.Content = null;
                    if (boundary == "clear-cache") MarkdownEdgePreviewPreload.For(viewport.Dispatcher).Clear();
                    Pump();
                }
                if (boundary == "replace")
                    UntilReview(() => PreviewText(viewport).Contains("replacement") && viewport.IsHitTestVisible, "replacement wins");
                else if (boundary == "clear-cache")
                    UntilReview(() => PreviewText(viewport).Contains("obsolete") && viewport.IsHitTestVisible, "optional cache clear does not cancel demand ownership");
                else
                {
                    UntilReview(() => MarkdownLayoutWorker.OutstandingRequests == 0, "cancelled consumers drain");
                    Require(!PreviewText(viewport).Contains("obsolete") && !viewport.IsHitTestVisible, "late result is never published");
                    Set("recovered"); viewport.SetPreviewActive(true); window.Content = viewport;
                    UntilReview(() => PreviewText(viewport).Contains("recovered") && viewport.IsHitTestVisible, "next active demand recovers");
                }
            }
            finally { window.Close(); Pump(); }
        }
        Console.WriteLine("PASS pending replace/retract/unload/cache-clear boundaries");
    }

    private static void CheckThemeInvalidation()
    {
        var viewport = new MarkdownEdgeCapsulePreviewViewport();
        var window = new Window { Content = viewport, Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false };
        var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent("普通短行", MarkdownRenderModes.Full);
        var brush = new SolidColorBrush(Colors.DarkRed);
        window.Resources["TextBrushKey"] = brush;
        try
        {
            window.Show();
            using (HoldWorker(MarkdownLayoutWorker.Shared))
            {
                viewport.SetContent(content, _ => { });
                UntilReview(() => MarkdownLayoutWorker.OutstandingRequests > 0, "ordinary short text also awaits STA");
                brush.Color = Colors.DarkBlue;
            }
            var body = PublishedBody(viewport);
            Require(Glyphs(body.Artifact.Drawing).Any(g => g.ForegroundBrush is SolidColorBrush b && b.Color == Colors.DarkBlue),
                "resource mutation discards stale short-row work and publishes current color");
            Require(!brush.IsFrozen, "caller resource remains mutable");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckHostPublication()
    {
        using var host = NewHost();
        var context = new EdgeCapsulePreviewContext(new(), () => "host", false, () => new string('文', 500),
            () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var view = descriptor.CreateContent(new(420, 300));
        Require(host.StagePreviewContent(view, 398, 300), "real host stages the preview");
        var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
        Require(viewport.Opacity == 0 && !viewport.IsHitTestVisible, "staging never formats or exposes partial body");
        descriptor.SetVisibility?.Invoke(false); host.ClearPreviewContent(); Pump();
        Require(!viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(), "clearing unready host leaves no result");
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
                panel.Resources["PaperBorderBrushKey"] = Brushes.Gray;
                var window = new Window { Content = panel, Width = 500, Height = 420, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); Pump();
                    RenderForCheck(panel, fixtures[i], _ => { }, mode, new Size(420, 320), zoom);
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
