using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.Contains("--preload-profile")) ProfilePreload(args.Contains("--reverse"));
            else if (args.Contains("--preload-memory")) PreloadMemory();
            else if (args.Contains("--profile")) Profile();
            else if (args.Contains("--export")) ExportPreviewPixels(args.Last());
            else { SharedPreviewSemanticChecks.Run(); Checks(); PreloadAuditChecks(); PreloadChecks(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }

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

    private static EdgeCapsuleHost NewHost() => EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
        4, 16, 15, 2, 1, 32, 6, 4, "✓", 13, 12, FontWeights.Normal, "Close",
        Brushes.White, Brushes.Gray, Brushes.Blue, Brushes.LightGray, Brushes.Gray, Brushes.Black, Brushes.Gray,
        new FontFamily("Segoe UI"), new FontFamily("Segoe UI Symbol"), XmlLanguage.GetLanguage("en-US"), false, "edge-preview-check"));

    private static void Profile()
    {
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
        foreach (var mode in new[] { MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        foreach (var fixture in fixtures)
        {
            var rows = new List<double[]>();
            for (var i = 0; i < 24; i++)
            {
                var result = ProfileOne(fixture.Text, mode);
                Console.WriteLine("SAMPLE " + JsonSerializer.Serialize(new
                { backend = "bounded", fixture = fixture.Name, mode, iteration = i, metrics = result }));
                if (i >= 3) rows.Add(result);
            }
            var names = new[] { "describeMs", "createMs", "stageMs", "totalReadyMs", "stageToReadyMs",
                "firstShapeMs", "frameGapMaxMs", "applyMaxMs", "allocationKiB" };
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
        using var host = NewHost();
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
        if (preparation == "text")
            foreach (var step in MarkdownEdgeCapsulePreviewRenderer.WarmInlineSteps(preload.Capture(context))) { }
        if (preparation == "layout")
            AwaitPreload(preload.WarmLayoutAsync(new(context, host.MarkdownPreloadAnchor!, fixedSize, () => true)));
        var warmMs = preparation == "cold" ? 0 : Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
        var hitsBefore = preload.BodyHits;
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        IEdgeCapsulePreviewProvider provider = MarkdownEdgeCapsulePreviewProvider.Instance;
        var descriptor = provider.Describe(context);
        var describeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var createStarted = Stopwatch.GetTimestamp();
        var view = descriptor.CreateContent(fixedSize);
        var createMs = Stopwatch.GetElapsedTime(createStarted).TotalMilliseconds;
        var loop = new DispatcherFrame();
        var settled = false; var timedOut = false; var readyAt = 0L;
        bool Published(DependencyObject element)
        {
            if (element is MarkdownEdgeCapsulePreviewViewport old)
                return old.IsArrangeValid && old.Children.OfType<StackPanel>().Any(panel =>
                    panel.Opacity > 0 && panel.Children.Count > 0 && panel.IsArrangeValid);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (Published(VisualTreeHelper.GetChild(element, i))) return true;
            return false;
        }
        void Observe(object? sender, EventArgs e)
        {
            if (readyAt == 0 && Published(view)) readyAt = Stopwatch.GetTimestamp();
            if (readyAt != 0 && settled) loop.Continue = false;
        }
        view.LayoutUpdated += Observe;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => { timedOut = true; timeout.Stop(); loop.Continue = false; };
        try
        {
            var stageStarted = Stopwatch.GetTimestamp();
            Require(host.StagePreviewContent(view, fixedSize.WidthDip - 22, fixedSize.HeightDip), "stage real preview");
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
            Require(!timedOut && settled && readyAt != 0, "profile completes without an extra Rendering driver");
            var gaps = times.Zip(times.Skip(1)).Select(pair => Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds);
            return new[] { describeMs, createMs, stageMs, Stopwatch.GetElapsedTime(started, readyAt).TotalMilliseconds,
                Stopwatch.GetElapsedTime(stageStarted, readyAt).TotalMilliseconds,
                times.Count > 1 ? Stopwatch.GetElapsedTime(motionStarted, times[1]).TotalMilliseconds : 0,
                gaps.DefaultIfEmpty(0).Max(), costs.DefaultIfEmpty(0).Max(),
                (GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024.0, warmMs, preload.BodyHits - hitsBefore };
        }
        finally
        {
            timeout.Stop(); view.LayoutUpdated -= Observe;
            presenter.ClearPresentationSettleNotification();
            presenter.CancelTransition(); presenter.ClearDeferredWork();
            descriptor.SetVisibility?.Invoke(false);
            host.ClearPreviewContent();
            Pump();
        }
    }
}
