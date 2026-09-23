using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static EdgeCapsuleHost NewHost(double chromeMargin = 4) => EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
        chromeMargin, 16, 15, 2, 1, 32, 6, 4, "✓", 13, 12, FontWeights.Normal, "Close",
        Brushes.White, Brushes.Gray, Brushes.Blue, Brushes.LightGray, Brushes.Gray, Brushes.Black, Brushes.Gray,
        new FontFamily("Segoe UI"), new FontFamily("Segoe UI Symbol"), XmlLanguage.GetLanguage("en-US"), false, "edge-preview-check"));

    private static T AwaitPreload<T>(Task<T> task)
    {
        var until = Stopwatch.StartNew();
        while (!task.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(12)) Pump();
        Require(task.IsCompleted, "optional preload completes/cancels rather than hanging");
        return task.GetAwaiter().GetResult();
    }
    private static bool RenderForSample(Panel target, string source, Action<string> openExternal,
        string mode = MarkdownRenderModes.Full, Size? viewport = null, double textZoom = 1)
    {
        var size = viewport ?? new Size(double.IsFinite(target.Width) ? target.Width : 420, 10000);
        var artifact = AwaitPreload(MarkdownEdgeCapsulePreviewRenderer.PrepareArtifactAsync(target,
            MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source, mode), size, textZoom, false, default));
        Require(artifact != null, "stable input builds an artifact");
        target.Children.Clear();
        target.Children.Add(new MarkdownPreviewArtifactSurface(artifact!, openExternal));
        target.UpdateLayout();
        return artifact!.Truncated;
    }
    private static void Profile()
    {
        var names = new[] { "describeMs", "createMs", "stageMs", "totalReadyMs", "stageToReadyMs",
            "firstShapeMs", "frameGapMaxMs", "applyMaxMs", "allocationKiB", "warmMs",
            "artifactHits", "stageToInteractiveMs" };
        var fixtures = new (string Name, string Text)[]
        {
            ("short-dense", string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd()),
            ("short-dense-rows", string.Join('\n', Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd(), 12))),
            ("short-rows", string.Join('\n', Enumerable.Repeat("普通正文 **加粗** 与 `code`", 12))),
            ("distinct-rows", string.Join('\n', Enumerable.Range(1, 12).Select(i => $"第{i}行普通正文 **加粗** 与 `code`"))),
            ("dense-inline", string.Concat(Enumerable.Repeat("**加粗** *斜体* ~~删除~~ `code` [a **styled** link](https://example.com) 中文 ", 45))),
            ("long-code", "```\n" + new string('文', 5500) + "\n```"),
            ("many-links", string.Concat(Enumerable.Repeat("[**a** *b*](https://example.com) ", 120)))
        };
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
        foreach (var fixture in fixtures)
        {
            var rows = new List<double[]>();
            for (var i = 0; i < 24; i++)
            {
                var result = ProfileOne(fixture.Text, mode);
                Console.WriteLine("SAMPLE " + JsonSerializer.Serialize(new
                { backend = "bounded", fixture = fixture.Name, mode, iteration = i,
                    metrics = names.Select((name, index) => (name, value: result[index])).ToDictionary(x => x.name, x => x.value) }));
                if (i >= 3) rows.Add(result);
            }
            Console.WriteLine("EDGE_PROFILE " + JsonSerializer.Serialize(new
            {
                backend = "bounded", fixture = fixture.Name, mode,
                characters = fixture.Text.Length, samples = rows.Count, viewport = "460x410 fixed card",
                median = names.Select((name, i) => (name, value: rows.Select(row => row[i]).Order().ElementAt(rows.Count / 2)))
                    .ToDictionary(x => x.name, x => x.value),
                maximum = names.Select((name, i) => (name, value: rows.Max(row => row[i])))
                    .ToDictionary(x => x.name, x => x.value)
            }));
        }
    }
    private static double[] ProfileOne(string text, string mode, string preparation = "cold")
    {
        using var host = NewHost(EdgeCapsuleLayout.WindowChromeMargin);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "profile monitor");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var fixedSize = new EdgeCapsulePreviewSize(460, 410);
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, 40, 0, 100, 22, 40,
            fixedSize.WidthDip + 8, fixedSize.HeightDip + 8, false, 1, null, 480, 440);
        var presenter = new EdgeCapsulePresenter();
        Require(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false)).Accepted, "attach presenter");
        var times = new List<long>();
        var costs = new List<double>();
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty => presenter.Reconcile(dirty,
            () => layout, () => null, frame => frame, frame =>
            {
                var start = Stopwatch.GetTimestamp();
                var success = host.Apply(frame);
                times.Add(start); costs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                return success;
            });
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
        Pump(); times.Clear(); costs.Clear();
        var context = new EdgeCapsulePreviewContext(new PaperData(), () => "Profile", false,
            () => text, () => mode, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        var preload = MarkdownEdgePreviewPreload.For(dispatcher);
        preload.SetEnabledForChecks(preparation != "cold");
        var warmStarted = Stopwatch.GetTimestamp();
        if (preparation == "layout")
            AwaitPreload(preload.WarmLayoutAsync(new(context, host.MarkdownPreloadAnchor!, fixedSize, () => true, preload.Capture(context))));
        var warmMs = preparation == "cold" ? 0 : Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
        var hitsBefore = preload.ArtifactHits;
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        IEdgeCapsulePreviewProvider provider = MarkdownEdgeCapsulePreviewProvider.Instance;
        var descriptor = provider.Describe(context);
        var describeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var createStarted = Stopwatch.GetTimestamp();
        var view = descriptor.CreateContent(fixedSize);
        var createMs = Stopwatch.GetElapsedTime(createStarted).TotalMilliseconds;
        var loop = new DispatcherFrame();
        var settled = false; var timedOut = false; var readyAt = 0L; var interactiveAt = 0L;
        var profileViewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
        // Layout readiness is local to the published body. Effective input also depends on
        // the ancestor host's animation gate; measure it separately, not as formatting cost.
        bool Published(DependencyObject element)
        {
            if (element is MarkdownEdgeCapsulePreviewViewport viewport)
                return viewport.IsArrangeValid && viewport.Opacity > 0 &&
                    viewport.Children.OfType<MarkdownPreviewArtifactSurface>().Any(surface => surface.IsArrangeValid);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (Published(VisualTreeHelper.GetChild(element, i))) return true;
            return false;
        }
        void Observe(object? sender, EventArgs e)
        {
            if (readyAt == 0 && Published(view)) readyAt = Stopwatch.GetTimestamp();
            if (interactiveAt == 0 && readyAt != 0 && profileViewport.IsHitTestVisible)
                interactiveAt = Stopwatch.GetTimestamp();
            if (readyAt != 0 && interactiveAt != 0 && settled) loop.Continue = false;
        }
        DependencyPropertyChangedEventHandler inputChanged = (_, _) => Observe(null, EventArgs.Empty);
        view.LayoutUpdated += Observe;
        profileViewport.IsHitTestVisibleChanged += inputChanged;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => { timedOut = true; timeout.Stop(); loop.Continue = false; };
        try
        {
            var stageStarted = Stopwatch.GetTimestamp();
            Require(host.StagePreviewContent(view, fixedSize.ContentSize.Width, fixedSize.ContentSize.Height), "stage real preview");
            var stageMs = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            descriptor.SetVisibility?.Invoke(true);
            var motionStarted = Stopwatch.GetTimestamp();
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 160));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
            presenter.NotifyWhenPresentationSettled(success =>
            { Require(success, "transition settles"); settled = true; Observe(null, EventArgs.Empty); });
            timeout.Start();
            if (loop.Continue) Dispatcher.PushFrame(loop);
            Require(!timedOut && settled && readyAt != 0 && interactiveAt != 0, "profile observes both layout and input without an extra Rendering driver");
            var gaps = times.Zip(times.Skip(1)).Select(pair => Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds);
            return new[] { describeMs, createMs, stageMs, Stopwatch.GetElapsedTime(started, readyAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(stageStarted, readyAt).TotalMilliseconds,
                times.Count > 1 ? Stopwatch.GetElapsedTime(motionStarted, times[1]).TotalMilliseconds : 0,
                gaps.DefaultIfEmpty(0).Max(), costs.DefaultIfEmpty(0).Max(),
                (GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024.0, warmMs, preload.ArtifactHits - hitsBefore,
                Stopwatch.GetElapsedTime(stageStarted, interactiveAt).TotalMilliseconds };
        }
        finally
        {
            timeout.Stop(); view.LayoutUpdated -= Observe;
            profileViewport.IsHitTestVisibleChanged -= inputChanged;
            presenter.ClearPresentationSettleNotification();
            presenter.CancelTransition(); presenter.ClearDeferredWork();
            descriptor.SetVisibility?.Invoke(false);
            host.ClearPreviewContent();
            Pump();
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
        cache.Clear(); Pump();
        var before = GC.GetTotalMemory(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.Black;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            for (var i = 0; i < contexts.Length; i++) Require(AwaitPreload(cache.WarmLayoutAsync(
                new(contexts[i], root, new(460, 410), () => true, cache.Capture(contexts[i])))), "memory artifact prewarm succeeds");
            Pump();
            var withArtifacts = GC.GetTotalMemory(true);
            Console.WriteLine("PRELOAD_MEMORY " + JsonSerializer.Serialize(new
            { requestedNotes = contexts.Length, excerpts = cache.ExcerptCount, charactersPerNote = 6000, artifacts = cache.ArtifactCount,
                retainedExcerptsArtifactsAndWpfCachesKiB = (withArtifacts - before) / 1024.0,
                note = "managed live-heap deltas after GC; excludes source fixtures, not a private/native working-set measurement" }));
        }
        finally { window.Close(); Pump(); cache.Clear(); GC.KeepAlive(contexts); }
    }
    private static void ProfilePreload(bool reverse)
    {
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
        foreach (var fixture in new[] {
            (Name: "plain", Text: string.Join('\n', Enumerable.Range(1,12).Select(i=>$"第{i}行 **内容** `code`"))),
            (Name: "dense", Text: string.Concat(Enumerable.Repeat("**加粗** *italic* ~~删除~~ `code` [链接](https://example.com) 文 ", 80))),
            (Name: "short-dense", Text: string.Join('\n',Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)),12))) })
        foreach (var preparation in reverse ? new[] { "layout", "cold" } : new[] { "cold", "layout" })
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
    private static void ProfilePlainInlineAllocation()
    {
        foreach (var length in new[] { 300, 6000 })
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
        {
            var text = new string('文', length);
            long Sample()
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var pieces = MarkdownEdgeCapsulePreviewRenderer.InlinePieces(text, mode).ToArray();
                GC.KeepAlive(pieces);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (var i = 0; i < 5; i++) Sample();
            var bytes = Enumerable.Range(0, 21).Select(_ => Sample()).Order().ToArray();
            Console.WriteLine($"INLINE_ALLOC length={length} mode={mode} medianBytes={bytes[bytes.Length/2]}");
        }
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
            foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
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
                    RenderForSample(panel, fixtures[i], _ => { }, mode, new Size(420, 320), zoom);
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
