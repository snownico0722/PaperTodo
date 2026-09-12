using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static T AwaitPreload<T>(Task<T> task)
    {
        var until = Stopwatch.StartNew();
        while (!task.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(12)) Pump();
        Require(task.IsCompleted, "optional preload completes/cancels rather than hanging");
        return task.GetAwaiter().GetResult();
    }

    private static void PreloadChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.DarkRed;
        root.Resources["WeakTextBrushKey"] = Brushes.Gray;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        root.Resources["HoverBrushKey"] = Brushes.LightGray;
        root.Resources["PaperBorderBrushKey"] = Brushes.Gray;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        var size = new EdgeCapsulePreviewSize(460, 410);
        var text = string.Concat(Enumerable.Repeat("**加粗** *italic* `code` [链接](https://example.com) 文 ", 80));
        var source = new EdgeCapsulePreviewInvalidationSource();
        var mode = MarkdownRenderModes.Full;
        EdgeCapsulePreviewContext Context(EdgeCapsulePreviewInvalidationSource token, Func<string>? read = null) =>
            new(new PaperData(), () => "预载测试", false, read ?? (() => text), () => mode,
                (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, token);
        var a = Context(source);
        var b = Context(new());
        bool WarmAt(EdgeCapsulePreviewContext context, EdgeCapsulePreviewSize bounds) =>
            AwaitPreload(cache.WarmLayoutAsync(new(context, root, bounds, () => true)));
        bool Warm(EdgeCapsulePreviewContext context) => WarmAt(context, size);
        Border Demand(EdgeCapsulePreviewContext context, EdgeCapsulePreviewSize? bounds = null)
        {
            cache.BeginDemand();
            var actual = bounds ?? size;
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = descriptor.CreateContent(actual);
            var border = new Border { Width = actual.ContentSize.Width, Height = actual.ContentSize.Height, Child = view };
            root.Children.Add(border);
            ((EdgeCapsuleLivePreviewView)view).PrepareForFirstDisplay();
            border.Measure(new Size(border.Width, border.Height));
            border.Arrange(new Rect(0, 0, border.Width, border.Height));
            UntilReview(() => Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single().Opacity == 1, "demand publishes complete preview");
            return border;
        }
        void Release(Border border) { root.Children.Remove(border); border.Child = null; Pump(); }
        try
        {
            window.Show(); Pump();
            var light = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
                string.Join('\n', Enumerable.Range(1, 12).Select(i => $"第{i}行普通正文 **加粗** 与 `code`")),
                MarkdownRenderModes.Full);
            var heavy = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(text, MarkdownRenderModes.Full);
            var shortDense = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
                string.Join('\n', Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)), 12)),
                MarkdownRenderModes.Full);
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(light), "200+ multi-row styled text qualifies under the style-count rule");
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(heavy), "long dense row is classified high-load");
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(shortDense), "short style-dense rows are classified high-load");
            Console.WriteLine("PASS content-cost artifact preload classifier");
            var exactly400Plain = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(new string('文', 400), MarkdownRenderModes.Full);
            var over400Plain = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(new string('文', 401), MarkdownRenderModes.Full);
            var exactly100Styled = MarkdownEdgeCapsulePreviewRenderer.CaptureContent("**" + new string('a', 100) + "** " + new string('文', 110), MarkdownRenderModes.Full);
            var over100Styled = MarkdownEdgeCapsulePreviewRenderer.CaptureContent("**" + new string('a', 101) + "** " + new string('文', 110), MarkdownRenderModes.Full);
            Require(!MarkdownEdgePreviewPreload.IsClearlyHighLoad(exactly400Plain), "400 plain characters stay cold at the strict boundary");
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(over400Plain), "401 total characters qualify for preload");
            Require(!MarkdownEdgePreviewPreload.IsClearlyHighLoad(exactly100Styled), "200+ source characters with exactly 100 styled characters stay cold");
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(over100Styled), "200+ source characters with more than 100 styled characters qualify");
            var threeStyles = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
                "**a** " + "*b* " + "`c` " + new string('文', 205), MarkdownRenderModes.Full);
            var fourStyles = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
                "**a** " + "*b* " + "`c` " + "~~d~~ " + new string('文', 205), MarkdownRenderModes.Full);
            Require(!MarkdownEdgePreviewPreload.IsClearlyHighLoad(threeStyles), ">200 characters with exactly three styled pieces stay cold");
            Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(fourStyles), ">200 characters with more than three styled pieces qualify");
            Console.WriteLine("PASS artifact preload total/style thresholds");

            Require(Warm(a) && Warm(b) && cache.ArtifactCount == 2, "two complete heavy artifacts warm without opening a preview");
            Require(root.Children.Count == 0, "prewarm leaves no hidden holder or mounted preview tree");
            var hits = cache.ArtifactHits;
            var firstA = Demand(a);
            var firstSurface = Elements(firstA).OfType<MarkdownPreviewArtifactSurface>().Single();
            Require(firstSurface.Children.OfType<Button>().Count() == firstSurface.Artifact.Links.Count && firstSurface.Artifact.Drawing.IsFrozen,
                "artifact hit mounts one frozen drawing surface with native link hits");
            Release(firstA); Release(Demand(b)); Release(Demand(a));
            Require(cache.ArtifactHits == hits + 3, "A-B-A reuses immutable artifacts while materializing fresh surfaces");
            Console.WriteLine("PASS artifact preload A-B-A and direct drawing surface mount");

            var mixedBlocks = "# Heading **bold**\n> quote [q](https://example.com/q)\n- [x] done `code`\n12) ordered *italic*\n- bullet ~~strike~~\n---\n![图](i:asset)\n```\n\nliteral **code**\n```\n" + new string('文', 450);
            var pixelFixtures = new[] {
                string.Concat(Enumerable.Repeat("plain **strong** *italic* `code` [link](https://example.com) ", 20)),
                string.Concat(Enumerable.Repeat("**粗体** ~~删除~~ `code` [链接](https://example.com) 文 ", 80)),
                "## " + new string('文', 320) + "\n> " + new string('a', 300) + "\n```\n" + new string('c', 300) + "\n```",
                mixedBlocks,
                "first\n\nlast\n```\n\n\n```\n" + new string('文', 450)
            };
            var pixelCases = 0;
            var exactCases = 0;
            var maximumDifference = 0;
            byte[] Pixels(FrameworkElement element)
            {
                element.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(element);
                int width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
                int height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
                var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var bytes = new byte[width * height * 4];
                bitmap.CopyPixels(bytes, width * 4, 0);
                return bytes;
            }
            foreach (var sharp in new[] { false, true })
            foreach (var testMode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            foreach (var zoom in new[] { 0.7, 1.3 })
            foreach (var fixture in pixelFixtures)
            {
                AppTypography.Configure(sharp ? UiFontPresets.YaHei : UiFontPresets.Default,
                    sharp ? 1.25 : 1, textRenderingProfile: sharp ? TextRenderingProfiles.Sharp : TextRenderingProfiles.Standard);
                NoteTypography.Configure(sharp ? VisualTextSizes.Large : VisualTextSizes.Medium, sharp);
                mode = testMode; text = fixture; cache.Clear();
                var context = Context(new()); context.Paper.TextZoom = zoom;
                Require(MarkdownEdgePreviewPreload.IsClearlyHighLoad(cache.Capture(context)), "pixel fixture qualifies for whole-artifact preload");
                Require(Warm(context), "pixel reference artifact is actually preloaded");
                var previousHits = cache.ArtifactHits;
                var hot = Demand(context); var hotPixels = Pixels(hot); Release(hot);
                Require(cache.ArtifactHits == previousHits + 1, "pixel comparison traverses the artifact-hit path");
                cache.Clear();
                var cold = Demand(context); var coldPixels = Pixels(cold); Release(cold);
                Require(hotPixels.Length == coldPixels.Length, "artifact preserves rendered pixel dimensions");
                var differences = hotPixels.Zip(coldPixels).Select(pair => Math.Abs(pair.First - pair.Second)).ToArray();
                maximumDifference = Math.Max(maximumDifference, differences.Max());
                if (hotPixels.SequenceEqual(coldPixels)) exactCases++;
                Console.WriteLine($"ARTIFACT_PIXEL_CASE sharp={sharp} mode={testMode} zoom={zoom} fixture={Array.IndexOf(pixelFixtures, fixture)} max={differences.Max()}");
                Require(differences.All(delta => delta <= 32), "artifact preserves visible text/decoration pixels");
                pixelCases++;
            }
            AppTypography.Configure(UiFontPresets.Default);
            NoteTypography.Configure(VisualTextSizes.Medium, false);
            Console.WriteLine($"ARTIFACT_PIXELS cases={pixelCases} exact={exactCases} maximumChannelDifference={maximumDifference}");

            mode = MarkdownRenderModes.Full;
            cache.Clear();
            text = string.Concat(Enumerable.Repeat("**heavy** *before* `code` ", 30));
            var descriptorBeforeEdit = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(a);
            text = string.Concat(Enumerable.Repeat("**edited** *between* `mount` ", 30));
            source.Invalidate();
            var editedView = descriptorBeforeEdit.CreateContent(size);
            var editedBorder = new Border { Width = size.ContentSize.Width, Height = size.ContentSize.Height, Child = editedView };
            root.Children.Add(editedBorder);
            ((EdgeCapsuleLivePreviewView)editedView).PrepareForFirstDisplay();
            UntilReview(() => Elements(editedView).OfType<MarkdownEdgeCapsulePreviewViewport>().Single().Opacity == 1, "edited demand publishes");
            Require(PreviewText(editedView).Contains("edited"), "deferred first display never binds an old excerpt to the new version");
            Release(editedBorder);

            Require(Warm(a), "prepare an artifact key candidate");
            var binding = cache.Bind(a, cache.Capture(a), a.Paper.TextZoom)!;
            var width = MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(size);
            var key = MarkdownEdgePreviewPreload.MakeKey(binding, root, new Size(width, 0))!;
            Require(!cache.TryCreateSurface(key with { Dpi = new DpiScale(key.Dpi.DpiScaleX * 1.5, key.Dpi.DpiScaleY * 1.5) }, out _),
                "different DPI cannot reuse old drawing");
            Require(cache.TryCreateSurface(key, out var matching, demand: false) &&
                    matching is { } matchingSurface &&
                    matchingSurface.Children.OfType<Button>().Count() == matchingSurface.Artifact.Links.Count,
                "matching DPI materializes one direct artifact surface");
            Console.WriteLine("PASS first-display generation, direct artifact mount and DPI-key rejection");

            hits = cache.ArtifactHits;
            text = string.Concat(Enumerable.Repeat("**新内容** *不允许* `旧正文` ", 30));
            var changed = Demand(a);
            Require(cache.ArtifactHits == hits && PreviewText(changed).Contains("新内容"), "fresh bounded content comparison rejects stale artifact");
            Release(changed);
            source.Invalidate();
            hits = cache.ArtifactHits; Release(Demand(a));
            Require(cache.ArtifactHits == hits, "version invalidation rejects even equal-text artifacts");
            Require(Warm(a), "prepare a resource-key candidate");
            hits = cache.ArtifactHits;
            Release(Demand(a, new EdgeCapsulePreviewSize(size.WidthDip, 300)));
            Require(cache.ArtifactHits == hits + 1, "same-width smaller-height demand reuses the whole artifact and clips locally");
            root.Resources["TextBrushKey"] = Brushes.DarkBlue;
            hits = cache.ArtifactHits; Release(Demand(a));
            Require(cache.ArtifactHits == hits, "theme replacement without notification cannot hit frozen old drawing");
            hits = cache.ArtifactHits; Release(Demand(a, new(350, 300)));
            Require(cache.ArtifactHits == hits, "changed width cannot reuse old layout");
            a.Paper.TextZoom = 1.3;
            hits = cache.ArtifactHits; Release(Demand(a, new(350, 300)));
            Require(cache.ArtifactHits == hits, "changed zoom cannot reuse old layout");
            mode = MarkdownRenderModes.Enhanced;
            hits = cache.ArtifactHits; Release(Demand(a, new(350, 300)));
            Require(cache.ArtifactHits == hits, "changed render mode cannot reuse old layout");
            Console.WriteLine("PASS source, version, theme, height reuse, width, zoom and mode invalidation");

            cache.Clear();
            text = string.Concat(Enumerable.Repeat("**complex** *value* `code` ", 100));
            using (var cancellation = new CancellationTokenSource())
            {
                var task = cache.WarmLayoutAsync(new(a, root, size, () => true), cancellation.Token);
                cancellation.Cancel();
                try { AwaitPreload(task); } catch (OperationCanceledException) { }
            }
            Pump();
            Require(cache.ArtifactCount == 0 && root.Children.Count == 0, "cancelled work does not publish or retain scratch controls");
            var stale = cache.WarmLayoutAsync(new(a, root, size, () => true));
            source.Invalidate();
            Require(!AwaitPreload(stale) && cache.ArtifactCount == 0, "late generation cannot populate artifact cache");
            for (var i = 0; i < 12; i++) Require(Warm(Context(new())), "warm unlimited heavy candidate");
            Require(cache.ArtifactCount == 12, "all clearly heavy notes retain immutable artifacts without count eviction");
            cache.Clear(); Pump();
            Require(cache.ArtifactCount == 0 && cache.ExcerptCount == 0 && cache.PendingCount == 0, "clear releases artifacts, excerpts and pending jobs");
            Console.WriteLine("PASS artifact cancellation, stale publication, no-count-eviction and cleanup");
            var completions = cache.WarmCompletions;
            cache.RequestLayout(source, () => new(a, root, size, () => true));
            var watch = Stopwatch.StartNew();
            while ((cache.WarmCompletions == completions || cache.PendingCount > 0) && watch.Elapsed < TimeSpan.FromSeconds(4)) Pump();
            Require(cache.WarmCompletions > completions && cache.PendingCount == 0,
                "one-shot artifact preload queue drains without periodic idle polling");
            cache.Clear();
        }
        finally
        {
            window.Close(); Pump(); cache.SetEnabledForChecks(true);
            AppTypography.Configure(UiFontPresets.Default);
            NoteTypography.Configure(VisualTextSizes.Medium, false);
        }
    }

    private static void PreloadMemory()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var contexts = Enumerable.Range(0, 100).Select(i =>
        {
            var text = ("note " + i + " " + string.Concat(Enumerable.Repeat("**a** *b* `c` [link](https://example.com) 文 ", 110))).PadRight(6000, '文');
            return new EdgeCapsulePreviewContext(new PaperData(), () => "Memory", false, () => text,
                () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        }).ToArray();
        foreach (var step in MarkdownEdgeCapsulePreviewRenderer.WarmInlineSteps(cache.Capture(contexts[0]))) { }
        cache.Clear(); Pump();
        var before = GC.GetTotalMemory(true);
        foreach (var context in contexts)
            foreach (var step in MarkdownEdgeCapsulePreviewRenderer.WarmInlineSteps(cache.Capture(context))) { }
        var pure = GC.GetTotalMemory(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.Black;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var beforeArtifacts = GC.GetTotalMemory(true);
            for (var i = 0; i < contexts.Length; i++) Require(AwaitPreload(cache.WarmLayoutAsync(
                new(contexts[i], root, new(460, 410), () => true))), "memory artifact prewarm succeeds");
            Pump();
            var withArtifacts = GC.GetTotalMemory(true);
            Console.WriteLine("PRELOAD_MEMORY " + JsonSerializer.Serialize(new
            { excerpts = cache.ExcerptCount, charactersPerNote = 6000, artifacts = cache.ArtifactCount,
                retainedExcerptsKiB = (pure - before) / 1024.0,
                additionalHundredArtifactsAndWpfCachesKiB = (withArtifacts - beforeArtifacts) / 1024.0,
                note = "managed live-heap deltas after GC; excludes source fixtures, not a private/native working-set measurement" }));
            Require(cache.ExcerptCount == 100 && cache.ArtifactCount == 100,
                "one current artifact is retained for every clearly heavy note without count eviction");
            for (var i = 0; i < 200; i++) cache.Capture(new EdgeCapsulePreviewContext(new PaperData(), () => "light", false,
                () => "普通短文本", () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
                () => new Style(), () => "", _ => { }, new()));
            Require(cache.ExcerptCount == 100 && cache.ArtifactCount == 100,
                "light notes do not enter or evict the heavy artifact set");
        }
        finally { window.Close(); Pump(); cache.Clear(); GC.KeepAlive(contexts); }
    }

    private static void ProfilePreload(bool reverse)
    {
        foreach (var mode in new[] { MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        foreach (var fixture in new[] {
            (Name: "plain", Text: string.Join('\n', Enumerable.Range(1,12).Select(i=>$"第{i}行 **内容** `code`"))),
            (Name: "dense", Text: string.Concat(Enumerable.Repeat("**加粗** *italic* ~~删除~~ `code` [链接](https://example.com) 文 ", 80))),
            (Name: "short-dense", Text: string.Join('\n',Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)),12))) })
        foreach (var preparation in reverse ? new[] { "layout", "text", "cold" } : new[] { "cold", "text", "layout" })
        {
            for (var i = 0; i < 9; i++)
            {
                var values = ProfileOne(fixture.Text, mode, preparation);
                Console.WriteLine("PRELOAD_SAMPLE " + JsonSerializer.Serialize(new
                { reverse, fixture = fixture.Name, mode, preparation, iteration = i, metrics = values }));
            }
        }
        MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher).SetEnabledForChecks(true);
    }
}
