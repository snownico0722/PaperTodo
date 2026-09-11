using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    // Opt-in executable profile: --profile. Identical fixtures fit both versions' source budgets.
    // This uses the real bounded HWND, preview and Presenter, not a replacement shape Border.
    // It observes application work/latency, not GPU present timestamps or a complete DComp swap.
    private static int RunEdgePreviewProfile()
    {
        var fixtures = new (string Name, string Text)[]
        {
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
            for (var iteration = 0; iteration < 9; iteration++)
            {
                var row = ProfileEdgePreview(fixture.Text, mode);
                if (iteration >= 2) rows.Add(row);
            }
            double Percentile(int column, double rank)
            {
                var values = rows.Select(row => row[column]).Order().ToArray();
                return values[Math.Min(values.Length - 1, (int)Math.Ceiling(rank * values.Length) - 1)];
            }
            var metrics = new[] { "describeMs", "stageMs", "contentReadyMs", "firstShapeMs", "frameGapMaxMs", "applyMaxMs", "handoffBarrierMs", "allocationKiB" };
            Console.WriteLine("EDGE_PROFILE " + JsonSerializer.Serialize(new
            {
                fixture = fixture.Name, mode, characters = fixture.Text.Length, samples = rows.Count,
                p50 = metrics.Select((name, i) => (name, value: Percentile(i, 0.5))).ToDictionary(item => item.name, item => item.value),
                p95 = metrics.Select((name, i) => (name, value: Percentile(i, 0.95))).ToDictionary(item => item.name, item => item.value)
            }));
        }
        return 0;
    }

    private static void DrainProfileDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static double[] ProfileEdgePreview(string text, string mode)
    {
        using var host = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            4, 16, 15, 2, 1, 32, 6, 4, "✓", 13, 12, FontWeights.Normal, "Close",
            Brushes.White, Brushes.Gray, Brushes.Blue, Brushes.LightGray, Brushes.Gray, Brushes.Black, Brushes.Gray,
            new FontFamily("Segoe UI"), new FontFamily("Segoe UI Symbol"), XmlLanguage.GetLanguage("en-US"), false, "profile"));
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Profile monitor");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = new EdgeCapsulePreviewContext(new PaperData(), () => "Profile", false,
            () => text, () => mode, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, new());
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var describedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var view = descriptor.CreateContent(descriptor.Size);
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, 40, 0, 100, 22, 40,
            descriptor.Size.WidthDip + 8, descriptor.Size.HeightDip + 8, false, 1, null, 480, 440);
        var presenter = new EdgeCapsulePresenter();
        Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach profile presenter");
        var times = new List<long>();
        var costs = new List<double>();
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty => presenter.Reconcile(dirty,
            () => layout, () => null, frame => frame, frame =>
            {
                var begin = Stopwatch.GetTimestamp();
                var success = host.Apply(frame);
                times.Add(begin);
                costs.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                return success;
            });
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
        DrainProfileDispatcher();
        times.Clear(); costs.Clear();
        var loop = new DispatcherFrame();
        var settled = false;
        var readyAt = 0L;
        var timedOut = false;
        bool Published(DependencyObject element)
        {
            if (element is MarkdownEdgeCapsulePreviewViewport viewport)
                return viewport.Children.OfType<StackPanel>().Any(panel => panel.Opacity > 0 && panel.Children.Count > 0);
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
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        timeout.Tick += (_, _) => { timedOut = true; timeout.Stop(); loop.Continue = false; };
        try
        {
            var stageStarted = Stopwatch.GetTimestamp();
            Check(host.StagePreviewContent(view, descriptor.Size.WidthDip - 22, descriptor.Size.HeightDip), "Stage profile content");
            var stageMs = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            var motionStarted = Stopwatch.GetTimestamp();
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 160));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
            presenter.NotifyWhenPresentationSettled(success =>
            {
                Check(success, "Profile transition settles");
                settled = true;
                Observe(null, EventArgs.Empty);
            });
            timeout.Start();
            if (loop.Continue) Dispatcher.PushFrame(loop);
            Check(!timedOut && settled && readyAt != 0, "Profile completes without a driving observer");
            var barrier = Stopwatch.GetTimestamp();
            Check(host.PrepareCompositionSourceForHandoff(), "Profile real-host handoff barrier");
            var barrierMs = Stopwatch.GetElapsedTime(barrier).TotalMilliseconds;
            var gaps = times.Zip(times.Skip(1)).Select(pair => Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds).ToArray();
            return new[] { describedMs, stageMs, Stopwatch.GetElapsedTime(stageStarted, readyAt).TotalMilliseconds,
                times.Count > 1 ? Stopwatch.GetElapsedTime(motionStarted, times[1]).TotalMilliseconds : 0,
                gaps.DefaultIfEmpty(0).Max(), costs.DefaultIfEmpty(0).Max(), barrierMs,
                (GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024.0 };
        }
        finally
        {
            timeout.Stop();
            view.LayoutUpdated -= Observe;
            presenter.ClearPresentationSettleNotification();
            presenter.CancelTransition(); presenter.ClearDeferredWork();
            host.ClearPreviewContent();
        }
    }
}
