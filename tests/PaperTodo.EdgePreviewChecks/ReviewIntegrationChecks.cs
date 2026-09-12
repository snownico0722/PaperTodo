using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void UntilReview(Func<bool> completed, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!completed() && timer.Elapsed < TimeSpan.FromSeconds(12)) Pump();
        Require(completed(), message);
    }

    private static void ReviewIntegrationChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        using var host = NewHost(EdgeCapsuleLayout.WindowChromeMargin);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "review monitor");
        var paper = new PaperData();
        var markdown = string.Concat(Enumerable.Repeat("**正文** [链接](https://example.com) `code` 中文 ", 45));
        var context = new EdgeCapsulePreviewContext(paper, () => "预热首次显示", false,
            () => markdown, () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
            () => new Style(), () => "", _ => { }, new());
        var initial = EdgeCapsuleModel.Initial with
        {
            State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
            Placement = new EdgeCapsulePlacement(0, 0, 1)
        };
        try
        {
            foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
            foreach (var size in new[] { new EdgeCapsulePreviewSize(460, 410), new EdgeCapsulePreviewSize(350, 300) })
            foreach (var zoom in new[] { 0.7, 1.3 })
            {
                cache.Clear(); paper.TextZoom = zoom;
                var layout = new EdgeCapsuleLayoutSnapshot(monitor, edge, 40, 0, 100, 22, 40,
                    size.WidthDip + 8, size.HeightDip + 8, false, 1, null, 480, 440);
                Require(host.Apply(EdgeCapsuleTargetPlanner.Calculate(initial, layout).Docked.ToFrame()), "real bounded host starts docked");
                Pump();
                Require(host.MarkdownPreloadAnchor != null, "preload uses the actual live host anchor for resources/DPI only");
                Require(AwaitPreload(cache.WarmLayoutAsync(new(context, host.MarkdownPreloadAnchor!, size, () => true))),
                    "never-opened artifact completes preload");
                Require(cache.ArtifactCount == 1 && host.MarkdownPreloadAnchor!.Children.Count > 0,
                    "preload retains one immutable artifact without mounting a hidden preview child");
                var hits = cache.ArtifactHits;
                var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
                var request = new EdgeCapsulePreviewRequest(size, descriptor.CreateContent(size), descriptor.SetVisibility);
                var contentSize = request.Size.ContentSize;
                Require(contentSize == new Size(size.WidthDip - 22, size.HeightDip - 16),
                    "card contract deducts both horizontal and vertical chrome");
                Require(host.StagePreviewContent(request.Content, contentSize.Width, contentSize.Height), "stage using production content geometry");
                Require(host.Apply(EdgeCapsuleTargetPlanner.Calculate(initial with { Preview = EdgeCapsulePreviewState.Open }, layout).Docked.ToFrame()),
                    "real host displays the staged artifact surface");
                request.SetVisibility?.Invoke(true);
                var viewport = Elements(request.Content).OfType<MarkdownEdgeCapsulePreviewViewport>().Single();
                UntilReview(() => viewport.Opacity == 1 && viewport.IsHitTestVisible, "first real host display publishes");
                Require(cache.ArtifactHits == hits + 1, "FIRST live-host display must take the preloaded artifact, not rebuild it");
                var surface = Elements(viewport).OfType<MarkdownPreviewArtifactSurface>().Single();
                Require(surface.Children.OfType<Button>().Count() == surface.Artifact.Links.Count && surface.Artifact.Drawing.IsFrozen,
                    "live host mounts one frozen drawing surface with native link hits but no WPF block tree");
                request.SetVisibility?.Invoke(false); host.ClearPreviewContent(); Pump();
            }
            Console.WriteLine("PASS first artifact preload-to-live-host geometry/direct mount (2 edges/2 sizes/2 zooms)");

            // A freshly built artifact must use the same native link contract as a cache hit.
            var panel = new StackPanel();
            var opened = new List<string>();
            RenderForCheck(panel,
                "[first](https://example.com/a) [second](https://example.com/b) " + new string('文', 300),
                opened.Add, MarkdownRenderModes.Full, new Size(360, 200));
            var targets = Elements(panel).OfType<FrameworkElement>().Where(EdgeCapsulePreviewInteraction.GetConsumesPointer).ToArray();
            Require(targets.Length >= 2, "cold heavy text provides distinct link targets");
            foreach (var target in targets)
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Require(opened.Count == 0, "unpaired cold releases over A or B must not open either link");
            var buttons = targets.OfType<Button>().ToArray();
            Require(buttons.Length == targets.Length && buttons.All(b => b.ClickMode == ClickMode.Release),
                "cold heavy links retain WPF button capture/release");
            buttons[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(opened.SequenceEqual(new[] { "https://example.com/b" }), "completed cold B click activates B once");
            Console.WriteLine("PASS freshly built artifact retains native link gestures");
            ReviewLinkGestureChecks();
        }
        finally { host.ClearPreviewContent(); cache.Clear(); }
    }
}
