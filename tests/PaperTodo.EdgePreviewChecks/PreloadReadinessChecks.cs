using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void PreloadReadinessChecks()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        var window = new Window { Content = root, Width = 550, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        EdgeCapsulePreviewContext Context(EdgeCapsulePreviewInvalidationSource source) =>
            new(new PaperData(), () => "恢复预热", false, () => new string('文', 500),
                () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, source);
        MarkdownEdgePreviewPreload.ReadResult Ready(EdgeCapsulePreviewContext context) =>
            MarkdownEdgePreviewPreload.ReadResult.Ready(new(context, root, new(460, 410), () => true));
        void Until(Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < 8000) Pump();
            Require(condition(), message);
        }
        void PumpFor(int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds) Pump();
        }
        try
        {
            window.Show(); Pump();
            var source = new EdgeCapsulePreviewInvalidationSource();
            var context = Context(source);
            var blocked = true;
            var reads = 0;
            cache.RequestLayout(source, () =>
            {
                reads++;
                return blocked ? MarkdownEdgePreviewPreload.ReadResult.Deferred : Ready(context);
            });
            Until(() => cache.DeferredCount == 1, "temporary menu/gesture/host blocker suspends an intent");
            Require(cache.PendingCount == 1 && cache.BodyCount == 0, "suspended intent is retained without a body");
            PumpFor(1100);
            Require(reads == 1, "suspended work must not be polled after two debounce intervals");
            var other = new EdgeCapsulePreviewInvalidationSource();
            cache.RequestLayout(other, () => Ready(Context(other)));
            Until(() => cache.BodyCount == 1 && cache.PendingCount == 1, "one suspended source cannot stall unrelated ready work");
            Require(reads == 1 && cache.DeferredCount == 1, "ready work does not re-read a suspended source");
            blocked = false;
            cache.Resume(source);
            Until(() => cache.BodyCount == 2 && cache.PendingCount == 0, "eligibility recovery completes preload without content edits");
            Require(reads == 2 && cache.DeferredCount == 0, "one recovery performs exactly one new read");
            cache.Clear();
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Deferred);
            Until(() => cache.DeferredCount == 1, "suspend before replacement");
            var replacements = 0;
            cache.RequestLayout(source, () => { replacements++; return Ready(context); });
            Until(() => cache.PendingCount == 0 && cache.BodyCount == 1, "new content supersedes a dormant reader");
            Require(replacements == 1, "replacement is coalesced");
            cache.Clear();
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Deferred);
            Until(() => cache.DeferredCount == 1, "suspend before retirement");
            cache.Forget(source); cache.Resume(source); PumpFor(550);
            Require(cache.PendingCount == 0 && cache.DeferredCount == 0 && cache.BodyCount == 0,
                "a late recovery cannot revive a retired source");
            cache.RequestLayout(source, () => MarkdownEdgePreviewPreload.ReadResult.Discard);
            Until(() => cache.PendingCount == 0, "permanent ineligibility discards the reader");
            cache.Resume(source);
            Require(cache.PendingCount == 0, "discarded reader is not an implicit retry");
            cache.Clear();
            var reentrantReads = 0;
            cache.RequestLayout(source, () =>
            {
                if (++reentrantReads == 1)
                {
                    cache.Resume(source); // Readiness restored before the old reader returns Deferred.
                    return MarkdownEdgePreviewPreload.ReadResult.Deferred;
                }
                return Ready(context);
            });
            Until(() => cache.BodyCount == 1 && cache.PendingCount == 0,
                "a ready event cannot be lost before Deferred registration");
            Require(reentrantReads == 2, "one readiness event produces one replacement attempt");
            Console.WriteLine("PASS preload Deferred/Ready/Discard, no polling, independent progress, event resume, replacement, retirement and in-flight wake");
        }
        finally { cache.Clear(); window.Close(); Pump(); }
    }
}
