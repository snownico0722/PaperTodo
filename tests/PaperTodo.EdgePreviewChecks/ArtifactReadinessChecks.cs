using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void ArtifactReadinessChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        var window = new Window { Content = root, Width = 550, Height = 500,
            ShowActivated = false, ShowInTaskbar = false };
        EdgeCapsulePreviewContext Context(EdgeCapsulePreviewInvalidationSource source) =>
            new(new(), () => "readiness", false, () => new string('文', 450),
                () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
                () => new Style(), () => "", _ => { }, source);
        MarkdownEdgePreviewPreload.ReadResult Ready(EdgeCapsulePreviewInvalidationSource source) =>
            MarkdownEdgePreviewPreload.ReadResult.Ready(new(Context(source), root, new(460, 410), () => true));
        void PumpFor(int milliseconds)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds) Pump();
        }
        try
        {
            window.Show(); Pump();
            var source = new EdgeCapsulePreviewInvalidationSource();
            var blocked = true;
            var reads = 0;
            cache.RequestLayout(source, () =>
            {
                reads++;
                return blocked ? MarkdownEdgePreviewPreload.ReadResult.Deferred : Ready(source);
            });
            UntilReview(() => cache.DeferredCount == 1, "temporarily unavailable source is retained dormant");
            PumpFor(650);
            Require(reads == 1 && cache.PendingCount == 1 && cache.ArtifactCount == 0,
                "a deferred reader is not polled while its host is unavailable");
            var other = new EdgeCapsulePreviewInvalidationSource();
            cache.RequestLayout(other, () => Ready(other));
            UntilReview(() => cache.ArtifactCount == 1 && cache.PendingCount == 1,
                "unavailable source does not stall unrelated work");
            Require(reads == 1, "unrelated requests do not wake dormant readers");
            blocked = false;
            var peer = new EdgeCapsulePreviewInvalidationSource();
            var peerReads = 0;
            cache.RequestLayout(peer, () =>
            {
                peerReads++;
                cache.Resume(source);
                return Ready(peer);
            });
            UntilReview(() => cache.ArtifactCount == 3 && cache.PendingCount == 0,
                "resumed work continues after the current peer");
            Require(reads == 2 && peerReads == 1 && cache.DeferredCount == 0,
                "resuming A does not cancel and rerun B");

            cache.Clear();
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Deferred);
            UntilReview(() => cache.DeferredCount == 1, "dormant request precedes replacement");
            cache.RequestLayout(source, () => Ready(source));
            UntilReview(() => cache.ArtifactCount == 1 && cache.PendingCount == 0,
                "new content replaces a dormant reader");
            cache.Clear();
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Deferred);
            UntilReview(() => cache.DeferredCount == 1, "dormant request precedes retirement");
            cache.Forget(source); cache.Resume(source); PumpFor(650);
            Require(cache.PendingCount == 0 && cache.DeferredCount == 0 && cache.ArtifactCount == 0,
                "late host recovery cannot revive a retired source");
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Discard);
            UntilReview(() => cache.PendingCount == 0, "permanent ineligibility discards the reader");
            cache.Resume(source);
            Require(cache.PendingCount == 0, "discard does not create an implicit retry");

            var reentrantReads = 0;
            cache.RequestLayout(source, () =>
            {
                if (++reentrantReads == 1)
                {
                    cache.Resume(source);
                    return MarkdownEdgePreviewPreload.ReadResult.Deferred;
                }
                return Ready(source);
            });
            UntilReview(() => cache.PendingCount == 0 && cache.ArtifactCount == 1,
                "recovery before Deferred registration is not lost");
            Require(reentrantReads == 2, "early recovery re-reads only the affected source");
            Require(root.Children.Count == 0, "readiness recovery never mounts a hidden body");
        }
        finally { cache.Clear(); window.Close(); Pump(); }

        using var host = NewHost(EdgeCapsuleLayout.WindowChromeMargin);
        var anchor = host.MarkdownPreloadLifecycleAnchor!;
        var hostSource = new EdgeCapsulePreviewInvalidationSource();
        RoutedEventHandler loaded = (_, _) => cache.Resume(hostSource);
        DependencyPropertyChangedEventHandler visible = (_, e) => { if (e.NewValue is true) cache.Resume(hostSource); };
        anchor.Loaded += loaded;
        anchor.IsVisibleChanged += visible;
        try
        {
            cache.RequestLayout(hostSource, () => !anchor.IsLoaded || !anchor.IsVisible
                ? MarkdownEdgePreviewPreload.ReadResult.Deferred
                : MarkdownEdgePreviewPreload.ReadResult.Ready(new(Context(hostSource), anchor, new(460, 410), () => true)));
            UntilReview(() => cache.DeferredCount == 1, "real hidden host defers without guessing its DPI");
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "readiness monitor");
            var model = EdgeCapsuleModel.Initial with
            {
                State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                    EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
                Placement = new EdgeCapsulePlacement(0, 0, 1)
            };
            var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                40, 0, 100, 22, 40, 468, 418, false, 1, null, 480, 440);
            Require(host.Apply(EdgeCapsuleTargetPlanner.Calculate(model, layout).Docked.ToFrame()), "show real host");
            UntilReview(() => cache.ArtifactCount == 1 && cache.PendingCount == 0,
                "real host Loaded/Visible recovery warms without another content edit");
            Console.WriteLine("PASS artifact readiness: no polling, peer progress, replacement/retirement, early wake and real host recovery");
        }
        finally
        {
            anchor.Loaded -= loaded;
            anchor.IsVisibleChanged -= visible;
            cache.Clear();
        }
    }
}
