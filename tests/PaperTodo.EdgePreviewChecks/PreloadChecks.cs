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
        while (!task.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(8)) Pump();
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
        bool Warm(EdgeCapsulePreviewContext context) => AwaitPreload(cache.WarmLayoutAsync(new(context, root, size, () => true)));
        Border Demand(EdgeCapsulePreviewContext context, EdgeCapsulePreviewSize? bounds = null)
        {
            cache.BeginDemand();
            var actual = bounds ?? size;
            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = descriptor.CreateContent(actual);
            var border = new Border { Width = actual.WidthDip - 22, Height = actual.HeightDip, Child = view };
            root.Children.Add(border);
            ((EdgeCapsuleLivePreviewView)view).PrepareForFirstDisplay();
            border.Measure(new Size(border.Width, border.Height));
            border.Arrange(new Rect(0, 0, border.Width, border.Height));
            Pump();
            Require(Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single().Opacity == 1, "demand publishes complete body");
            return border;
        }
        void Release(Border border) { root.Children.Remove(border); border.Child = null; Pump(); }
        try
        {
            window.Show(); Pump();
            Require(Warm(a) && Warm(b) && cache.BodyCount == 2, "two complete bodies are warmed without opening a preview");
            Require(root.Children.Count == 0, "prewarm leaves no hidden holder or second mounted preview tree");
            var hits = cache.BodyHits;
            Release(Demand(a)); Release(Demand(b)); Release(Demand(a));
            Require(cache.BodyHits == hits + 3, "A-B-A uses independently owned completed bodies three times");
            Console.WriteLine("PASS preload A-B-A and detached return: three demand hits");
            var pixelFixtures = new[] {
                "plain **strong** *italic* `code` [link](https://example.com)",
                string.Concat(Enumerable.Repeat("**粗体** ~~删除~~ `code` [链接](https://example.com) 文 ", 80)),
                "## heading\n- [x] **done**\n> quote *text*\n```\n\ncode\n```"
            };
            var pixelCases = 0;
            var exactCases = 0;
            var maximumDifference = 0;
            byte[] Pixels(FrameworkElement element)
            {
                var dpi = VisualTreeHelper.GetDpi(element);
                int width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
                int height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
                var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var bytes = new byte[width * height * 4];
                bitmap.CopyPixels(bytes, width * 4, 0);
                return bytes;
            }
            foreach (var testMode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            foreach (var zoom in new[] { 0.7, 1.3 })
            foreach (var fixture in pixelFixtures)
            {
                mode = testMode; text = fixture; cache.Clear();
                var context = Context(new()); context.Paper.TextZoom = zoom;
                Require(Warm(context), "pixel reference is actually preloaded");
                var previousHits = cache.BodyHits;
                var hot = Demand(context); var hotPixels = Pixels(hot); Release(hot);
                Require(cache.BodyHits == previousHits + 1, "pixel comparison traverses the cached-body path");
                cache.Clear();
                var cold = Demand(context); var coldPixels = Pixels(cold); Release(cold);
                Require(hotPixels.Length == coldPixels.Length, "cache preserves pixel dimensions");
                var differences = hotPixels.Zip(coldPixels).Select(pair => Math.Abs(pair.First - pair.Second)).ToArray();
                maximumDifference = Math.Max(maximumDifference, differences.Max());
                if (hotPixels.SequenceEqual(coldPixels)) exactCases++;
                Require(differences.All(delta => delta <= 32), "cache preserves visible text/decoration pixels");
                pixelCases++;
            }
            Console.WriteLine($"PRELOAD_PIXELS cases={pixelCases} exact={exactCases} maximumChannelDifference={maximumDifference}");
            mode = MarkdownRenderModes.Full;
            cache.Clear();
            var descriptorBeforeEdit = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(a);
            text = "edited between describe and mount";
            source.Invalidate();
            var editedView = descriptorBeforeEdit.CreateContent(size);
            var editedBorder = new Border { Width = size.WidthDip - 22, Height = size.HeightDip, Child = editedView };
            root.Children.Add(editedBorder);
            ((EdgeCapsuleLivePreviewView)editedView).PrepareForFirstDisplay();
            Pump();
            Require(PreviewText(editedView).Contains(text), "deferred first display never binds an old excerpt to the new version");
            Release(editedBorder);
            var binding = cache.Bind(a, cache.Capture(a), a.Paper.TextZoom)!;
            var key = MarkdownEdgePreviewPreload.MakeKey(binding, root, new Size(200, 100))!;
            Require(cache.Store(new(key, new StackPanel(), false)), "cache key fixture retained");
            Require(!cache.TryTake(key with { Dpi = new DpiScale(key.Dpi.DpiScaleX * 1.5, key.Dpi.DpiScaleY * 1.5) }, out _), "different DPI cannot reuse old drawing");
            Require(cache.TryTake(key, out _, demand: false), "matching DPI key remains usable");
            Console.WriteLine("PASS first-display generation and DPI-key rejection");
            hits = cache.BodyHits;
            text = "新内容，不允许用旧正文";
            var changed = Demand(a);
            Require(cache.BodyHits == hits && PreviewText(changed).Contains("新内容"), "fresh bounded content comparison rejects stale text");
            Release(changed);
            source.Invalidate();
            hits = cache.BodyHits; Release(Demand(a));
            Require(cache.BodyHits == hits, "version invalidation rejects even equal text bodies");
            Require(Warm(a), "prepare a resource-key candidate");
            root.Resources["TextBrushKey"] = Brushes.DarkBlue;
            hits = cache.BodyHits; Release(Demand(a));
            Require(cache.BodyHits == hits, "theme replacement without notification cannot hit frozen old drawing");
            hits = cache.BodyHits; Release(Demand(a, new(350, 300)));
            Require(cache.BodyHits == hits, "changed width/height cannot reuse old layout");
            a.Paper.TextZoom = 1.3;
            hits = cache.BodyHits; Release(Demand(a, new(350, 300)));
            Require(cache.BodyHits == hits, "changed zoom cannot reuse old layout");
            mode = MarkdownRenderModes.Enhanced;
            hits = cache.BodyHits; Release(Demand(a, new(350, 300)));
            Require(cache.BodyHits == hits, "changed render mode cannot reuse old layout");
            Console.WriteLine("PASS source, version, colors, viewport, zoom and mode invalidation");
            cache.Clear();
            text = string.Concat(Enumerable.Repeat("**complex** *value* `code` ", 100));
            using (var cancellation = new CancellationTokenSource())
            {
                var task = cache.WarmLayoutAsync(new(a, root, size, () => true), cancellation.Token);
                cancellation.Cancel();
                try { AwaitPreload(task); } catch (OperationCanceledException) { }
            }
            Pump();
            Require(cache.BodyCount == 0 && root.Children.Count == 0, "cancelled work does not publish or retain scratch controls");
            var stale = cache.WarmLayoutAsync(new(a, root, size, () => true));
            source.Invalidate();
            Require(!AwaitPreload(stale) && cache.BodyCount == 0, "late generation cannot populate cache");
            for (var i = 0; i < 7; i++) Require(Warm(Context(new())), "warm eviction candidate");
            Require(cache.BodyCount == MarkdownEdgePreviewPreload.MaximumBodies, "complete body retention is capped at four");
            cache.Clear(); Pump();
            Require(cache.BodyCount == 0 && cache.ExcerptCount == 0 && cache.PendingCount == 0, "clear releases cache and pending jobs");
            Console.WriteLine("PASS cancellation, stale publication, LRU bound and cleanup");
            var completions = cache.WarmCompletions;
            cache.RequestText(source, () => a);
            cache.RequestLayout(source, () => new(a, root, size, () => true));
            var watch = Stopwatch.StartNew();
            while ((cache.WarmCompletions == completions || cache.PendingCount > 0) && watch.Elapsed < TimeSpan.FromSeconds(4)) Pump();
            Require(cache.WarmCompletions > completions && cache.PendingCount == 0,
                "one-shot preload queue drains without periodic idle polling");
            cache.Clear();
        }
        finally { window.Close(); Pump(); cache.SetEnabledForChecks(true); }
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
            var beforeBodies = GC.GetTotalMemory(true);
            for (var i = 0; i < 4; i++) Require(AwaitPreload(cache.WarmLayoutAsync(
                new(contexts[i], root, new(460, 410), () => true))), "memory prewarm succeeds");
            Pump();
            var withBodies = GC.GetTotalMemory(true);
            Console.WriteLine("PRELOAD_MEMORY " + JsonSerializer.Serialize(new
            { excerpts = cache.ExcerptCount, charactersPerNote = 6000, bodies = cache.BodyCount,
                retainedExcerptsKiB = (pure - before) / 1024.0,
                additionalBodiesAndWpfCachesKiB = (withBodies - beforeBodies) / 1024.0,
                note = "managed live-heap deltas after GC; excludes source fixtures, not a private/native working-set measurement" }));
            for (var i = 0; i < 200; i++) cache.Capture(new EdgeCapsulePreviewContext(new PaperData(), () => "cap", false,
                () => "bounded", () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
                () => new Style(), () => "", _ => { }, new()));
            Require(cache.ExcerptCount <= 128 && cache.BodyCount <= 4, "memory state is bounded under many owners");
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
