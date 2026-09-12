using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void WorkerPreloadLifetimeChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var foreground = new SolidColorBrush(Colors.Black);
        var root = new Grid();
        root.Resources["TextBrushKey"] = foreground;
        var window = new Window { Content = root, Width = 550, Height = 500,
            ShowActivated = false, ShowInTaskbar = false };
        var source = new EdgeCapsulePreviewInvalidationSource();
        var context = new EdgeCapsulePreviewContext(new(), () => "resource restart", false,
            () => new string('文', 900), () => MarkdownRenderModes.Full,
            (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, source);
        using var cancellation = new CancellationTokenSource();
        try
        {
            window.Show(); Pump();
            Task<bool> warming;
            using (HoldWorker(MarkdownLayoutWorker.Shared))
            {
                warming = cache.WarmLayoutAsync(new(context, root, new(460, 410), () => true), cancellation.Token);
                UntilReview(() => MarkdownLayoutWorker.OutstandingRequests > 0,
                    "preload reaches the real pending paragraph worker");
                // No source-version notification: the paragraph's resource observer detects this
                // change and invalidates its own build when the old worker result arrives.
                foreground.Color = Colors.Red;
            }
            UntilReview(() => warming.IsCompleted, "resource-invalidated preload settles");
            Require(warming.GetAwaiter().GetResult(),
                "preload must survive stale generation completion");
            Require(cache.BodyCount == 1 && root.Children.Count == 0,
                "the replacement generation is cached once and the hidden holder is removed");
            Require(!foreground.IsFrozen && source.Version == 0,
                "retry does not freeze the host brush or mutate the source version");
            Console.WriteLine("PASS worker preload ignores superseded completion and caches the current resource generation");
        }
        finally { cancellation.Cancel(); cache.Clear(); window.Close(); Pump(); }
    }
}
