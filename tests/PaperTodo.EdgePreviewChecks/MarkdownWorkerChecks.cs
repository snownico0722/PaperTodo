using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static T AwaitWorkerCheck<T>(Task<T> task)
    {
        UntilReview(() => task.IsCompleted, "worker finishes without blocking the UI");
        return task.GetAwaiter().GetResult(); // completed task only, test assertion propagation
    }
    private static MarkdownParagraphRequest WorkerRequest(string text, double dpi = 1, double height = 300) =>
        new(new[] { new MarkdownLayoutPiece(text, 0, -1) },
            new[] { new MarkdownRunStyle("Segoe UI", null, FontStyles.Normal, FontWeights.Normal,
                FontStretches.Normal, 14, "en-US", Brushes.Black, null, null) },
            Array.Empty<string>(), new Size(260, height), dpi, TextFormattingMode.Display);

    private static byte[] DrawingPixels(MarkdownParagraphResult result)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawDrawing(result.Drawing);
        var width = Math.Max(1, (int)Math.Ceiling(result.Size.Width));
        var height = Math.Max(1, (int)Math.Ceiling(result.Size.Height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0);
        return bytes;
    }

    // A deterministic test gate on the worker Dispatcher, not a production pause/scheduling API.
    // It allows checking demand priority, coalescing and UI animation while formatting is pending.
    private static IDisposable HoldWorker(MarkdownLayoutWorker worker)
    {
        AwaitWorkerCheck(worker.PrepareAsync(WorkerRequest("initialize"), false, default));
        var ready = (TaskCompletionSource<Dispatcher>)typeof(MarkdownLayoutWorker)
            .GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        var dispatcher = ready.Task.GetAwaiter().GetResult();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var operation = dispatcher.InvokeAsync(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test gate not released");
        }, DispatcherPriority.Send);
        UntilReview(() => entered.IsSet, "test gate enters worker");
        return new WorkerGate(() =>
        {
            release.Set(); UntilReview(() => operation.Task.IsCompleted, "worker gate releases");
            operation.Task.GetAwaiter().GetResult(); entered.Dispose(); release.Dispose();
        });
    }
    private sealed class WorkerGate(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private static void MarkdownWorkerChecks()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        using var worker = new MarkdownLayoutWorker();
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var request = WorkerRequest("中文 abc العربية 😀 " + new string('文', 900), dpi);
            var sync = MarkdownParagraphLayout.Prepare(request).Last(result => result != null)!;
            var result = AwaitWorkerCheck(worker.PrepareAsync(request, false, default));
            Require(result.FormattingThreadId != uiThread, "actual formatter executes off the UI thread");
            Require(result.Drawing.IsFrozen && result.Size == sync.Size && result.VisibleText == sync.VisibleText &&
                result.FormattedLines == sync.FormattedLines && result.Truncated == sync.Truncated,
                "worker retains exact dimensions, visible lines and truncation");
            Require(DrawingPixels(result).SequenceEqual(DrawingPixels(sync)), "worker/local kernel pixels are byte-identical");
        }
        var mutable = new SolidColorBrush(Colors.Black);
        var rejected = false;
        try
        {
            _ = new MarkdownParagraphRequest(new[] { new MarkdownLayoutPiece("x", 0, -1) },
                new[] { new MarkdownRunStyle("Segoe UI", null, FontStyles.Normal, FontWeights.Normal,
                    FontStretches.Normal, 14, "en-US", mutable, null, null) }, Array.Empty<string>(),
                new Size(300, 200), 1, TextFormattingMode.Display);
        }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && !mutable.IsFrozen, "request refuses live resources without freezing its caller's brush");

        Task<MarkdownParagraphResult> first, second, demand;
        var requestPair = WorkerRequest(new string('文', 1500));
        Task<MarkdownParagraphResult>[] speculative;
        using (HoldWorker(worker))
        {
            first = worker.PrepareAsync(requestPair, false, default);
            second = worker.PrepareAsync(WorkerRequest(new string('文', 1500)), false, default);
        }
        Require(ReferenceEquals(AwaitWorkerCheck(first), AwaitWorkerCheck(second)),
            "equal queued requests share one immutable layout result");
        using (HoldWorker(worker))
        {
            speculative = Enumerable.Range(0, 16).Select(i => worker.PrepareAsync(
                WorkerRequest(i + new string('文', 5000), height: 2000), true, default)).ToArray();
            demand = worker.PrepareAsync(WorkerRequest("current demand"), false, default);
        }
        AwaitWorkerCheck(demand);
        Require(speculative.Any(task => !task.IsCompleted), "current demand is not queued behind all preloads");
        foreach (var task in speculative) AwaitWorkerCheck(task);
        using (var cancellation = new CancellationTokenSource())
        {
            Task<MarkdownParagraphResult> cancelled;
            using (HoldWorker(worker))
            {
                cancelled = worker.PrepareAsync(WorkerRequest(new string('x', 6000)), false, cancellation.Token);
                cancellation.Cancel();
                UntilReview(() => cancelled.IsCompleted, "cancellation releases the UI consumer before formatter resumes");
                Require(cancelled.IsCanceled, "cancelled request cannot publish");
            }
        }
        WorkerPreloadLifetimeChecks();
        WorkerResourceInvalidation();
        WorkerLiveAnimation();
        worker.Dispose(); UntilReview(() => worker.Completion.IsCompleted, "worker shuts down without a UI Join");
        Require(worker.OutstandingCount == 0, "all worker consumers are released");
        Console.WriteLine("PASS worker STA execution, frozen pixels (4 DPI values), coalescing, demand priority, cancellation and shutdown");
    }

    private static void WorkerResourceInvalidation()
    {
        foreach (var replace in new[] { false, true })
        {
            var root = new Grid();
            var brush = new LinearGradientBrush(Colors.Black, Colors.Blue, 0);
            root.Resources["TextBrushKey"] = brush;
            var window = new Window { Content = root, Width = 320, Height = 200, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Pump();
                Task<MarkdownPreviewArtifact?> task;
                using (HoldWorker(MarkdownLayoutWorker.Shared))
                {
                    task = MarkdownEdgeCapsulePreviewRenderer.PrepareArtifactAsync(root,
                        MarkdownEdgeCapsulePreviewRenderer.CaptureContent("short gradient", MarkdownRenderModes.Off),
                        new Size(280, 160), 1, false, default);
                    if (replace) root.Resources["TextBrushKey"] = Brushes.Red;
                    else brush.GradientStops[0].Color = Colors.Red;
                }
                Require(AwaitWorkerCheck(task) == null && !brush.IsFrozen,
                    "in-place or replacement non-cacheable resources reject stale drafts without freezing the host");
            }
            finally { window.Close(); Pump(); }
        }
    }

    private static void WorkerLiveAnimation()
    {
        // The real host/Presenter must settle while the real heavy-preview worker is stopped.
        // No extra Rendering observer drives frames; only the existing settled callback is used.
        using var gate = HoldWorker(MarkdownLayoutWorker.Shared);
        using var host = NewHost();
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "worker-animation monitor");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var size = new EdgeCapsulePreviewSize(460, 410);
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, 40, 0, 100, 22, 40,
            size.WidthDip + 8, size.HeightDip + 8, false, 1, null, 480, 440);
        var presenter = new EdgeCapsulePresenter();
        presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false));
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty => presenter.Reconcile(dirty,
            () => layout, () => null, frame => frame, host.Apply);
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile); Pump();
        var source = new EdgeCapsulePreviewInvalidationSource();
        var text = new string('文', 5500);
        var context = new EdgeCapsulePreviewContext(new(), () => "worker", false, () => text,
            () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, source);
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var view = descriptor.CreateContent(size);
        var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
        try
        {
            Require(host.StagePreviewContent(view, size.ContentSize.Width, size.ContentSize.Height), "stage worker preview");
            descriptor.SetVisibility?.Invoke(true);
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 160));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
            var settled = false;
            presenter.NotifyWhenPresentationSettled(success => { Require(success, "worker-pending animation settles"); settled = true; });
            UntilReview(() => settled && MarkdownLayoutWorker.OutstandingRequests > 0,
                "shell finishes while heavy text is still awaiting its worker");
            Require(viewport.Opacity == 0, "first heavy body stays unpublished while worker is blocked");
            text = "latest " + new string('新', 700); source.Invalidate();
            descriptor.SetVisibility?.Invoke(false); Pump();
            gate.Dispose();
            UntilReview(() => MarkdownLayoutWorker.OutstandingRequests == 0, "retracted preview cancels old worker consumers");
            Require(!viewport.IsHitTestVisible, "retract keeps stale results noninteractive");
            descriptor.SetVisibility?.Invoke(true);
            UntilReview(() => viewport.Opacity == 1 && PreviewText(viewport).Contains("latest"), "resume publishes only current text");
            Require(Elements(viewport).OfType<MarkdownPreviewArtifactSurface>().Count() == 1,
                "live cold demand publishes exactly one artifact after the worker gate releases");
        }
        finally
        {
            presenter.ClearPresentationSettleNotification(); presenter.CancelTransition(); presenter.ClearDeferredWork();
            descriptor.SetVisibility?.Invoke(false); host.ClearPreviewContent(); Pump();
        }
        Console.WriteLine("PASS real-host shell settles with worker blocked; edit/retract/resume never publishes stale text");
    }
}
