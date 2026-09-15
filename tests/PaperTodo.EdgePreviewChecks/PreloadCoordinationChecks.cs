using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void PreloadCoordinationChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        var window = new Window { Content = root, Width = 550, Height = 500,
            ShowActivated = false, ShowInTaskbar = false };
        var source = new EdgeCapsulePreviewInvalidationSource();
        var text = "old " + new string('文', 5500);
        var context = new EdgeCapsulePreviewContext(new(), () => "coordinated preload", false,
            () => text, () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
            () => new Style(), () => "", _ => { }, source);
        var size = new EdgeCapsulePreviewSize(350, 300);
        var oldReads = 0;
        var latestReads = 0;
        MarkdownEdgePreviewPreload.ReadResult Ready() =>
            MarkdownEdgePreviewPreload.ReadResult.Ready(new(context, root, size, () => true, cache.Capture(context)));
        void PumpFor(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds) Pump();
        }
        try
        {
            window.Show(); Pump();
            using (HoldWorker(MarkdownLayoutWorker.Shared))
            {
                cache.RequestLayout(source, () => { oldReads++; return Ready(); });
                cache.StartStartupWork();
                UntilReview(() => MarkdownLayoutWorker.OutstandingRequests > 0,
                    "speculative preload reaches the real worker before suspension");
                cache.SetSuspended(true);
                UntilReview(() => MarkdownLayoutWorker.OutstandingRequests == 0,
                    "suspending optional work cancels its active worker consumer");
                text = "latest " + new string('新', 5500);
                source.Invalidate();
                cache.RequestLayout(source, () => { latestReads++; return Ready(); });
                cache.StartStartupWork();
                cache.BeginDemand();
                PumpFor(650);
                Require(oldReads == 1 && latestReads == 0 && cache.PendingCount == 1 && cache.ArtifactCount == 0,
                    "suspension retains only the latest source reader and blocks debounce/startup/demand from starting optional work");
            }

            var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
            var view = descriptor.CreateContent(size);
            var border = new Border { Width = size.ContentSize.Width, Height = size.ContentSize.Height, Child = view };
            var viewport = Elements(view).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
            using (HoldWorker(MarkdownLayoutWorker.Shared))
            {
                root.Children.Add(border);
                ((EdgeCapsuleLivePreviewView)view).PrepareForFirstDisplay();
                UntilReview(() => MarkdownLayoutWorker.OutstandingRequests > 0,
                    "demand reaches the real worker while optional preparation is suspended");
                cache.SetSuspended(false);
                cache.SetSuspended(true);
                PumpFor(50);
                Require(MarkdownLayoutWorker.OutstandingRequests > 0,
                    "a new coordinator suspension must not cancel an already active demand consumer");
            }
            UntilReview(() => viewport.Opacity == 1 && viewport.IsHitTestVisible,
                "cold demand still publishes and opens input while optional preload remains suspended");
            Require(PreviewText(view).Contains("latest") && cache.ArtifactCount == 0 && latestReads == 0,
                "demand renders the latest version independently of the suspended speculative queue");
            root.Children.Remove(border); border.Child = null; Pump();

            cache.SetSuspended(false);
            UntilReview(() => cache.PendingCount == 0 && cache.ArtifactCount == 1,
                "resumption completes the newest pending request without another lifecycle event");
            var key = MarkdownEdgePreviewPreload.MakeKey(cache.Bind(context, cache.Capture(context), 1), root,
                new Size(MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(size, root), 0))!;
            Require(latestReads == 1 && cache.TryGetArtifact(key, out var artifact, demand: false) &&
                    PreviewText(new MarkdownPreviewArtifactSurface(artifact!, _ => { })).Contains("latest"),
                "resumed work publishes only the current version and drains exactly once");
            PreloadPlacementInvalidationCheck(cache, source, key);
            Console.WriteLine("PASS preload suspension cancels optional work, preserves latest requests and leaves demand runnable");
        }
        finally { cache.Clear(); cache.SetSuspended(false); window.Close(); Pump(); }
    }

    private static void PreloadPlacementInvalidationCheck(MarkdownEdgePreviewPreload cache,
        EdgeCapsulePreviewInvalidationSource source, MarkdownEdgePreviewPreload.Key key)
    {
        // Exercise the production refresh boundary without constructing a second application,
        // StateStore or plugin runtime. The source/cache and the owning WPF Dispatcher are real;
        // an absent regular capsule label is also normal for a deferred paper shell.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var paperWindow = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
        typeof(DispatcherObject).GetFields(fields).Single(field => field.FieldType == typeof(Dispatcher))
            .SetValue(paperWindow, Dispatcher.CurrentDispatcher);
        typeof(PaperWindow).GetField("_edgeCapsulePreviewInvalidationSource", fields)!.SetValue(paperWindow, source);
        var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", fields)!;
        lifecycle.SetValue(paperWindow, Enum.Parse(lifecycle.FieldType, "Closed"));
        var refresh = typeof(PaperWindow).GetMethod("RefreshCapsuleLabel", fields)!;
        var version = source.Version;
        var notifications = 0;
        void Invalidated() => notifications++;
        source.Invalidated += Invalidated;
        try
        {
            for (var i = 0; i < 10; i++) refresh.Invoke(paperWindow, [false]);
            Require(source.Version == version && notifications == 0 && cache.TryGetArtifact(key, out _, demand: false),
                "placement-only label refresh preserves the live source version and prepared artifact");
            // Title/content/style callers retain the default invalidating refresh path.
            refresh.Invoke(paperWindow, [Type.Missing]);
            Require(source.Version == version + 1 && notifications == 1 && !cache.TryGetArtifact(key, out _, demand: false),
                "ordinary label refresh still invalidates the source, notifies the live view and rejects the old artifact");
            Console.WriteLine("PASS placement-only label refresh retains prepared content; ordinary refresh still invalidates");
        }
        finally { source.Invalidated -= Invalidated; }
    }
}
