using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void PreloadAuditChecks()
    {
        bool Eligible(string text, string mode) => MarkdownEdgePreviewPreload.IsClearlyHighLoad(
            MarkdownEdgeCapsulePreviewRenderer.CaptureContent(text, mode));
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            Require(!Eligible(new string('a', 200), mode), "200 source characters do not qualify");
            Require(!Eligible(new string('a', 400), mode) && Eligible(new string('a', 401), mode), "strict 400/401 boundary");
            Require(!Eligible("**" + new string('a', 100) + "** " + new string('b', 110), mode), "exactly 100 styled characters stay cold");
            Require(Eligible("**" + new string('a', 101) + "** " + new string('b', 110), mode), "101 styled characters qualify");
            Require(!Eligible("**a** *b* `c` " + new string('x', 205), mode), "three content pieces: syntax markers do not count");
            Require(Eligible("**a** *b* `c` ~~d~~ " + new string('x', 205), mode), "four content pieces qualify");
            Require(!Eligible("[a](https://example.com/" + new string('x', 220) + ")", mode), "hidden destination is not styled label coverage");
            Require(!Eligible("***" + new string('x', 100) + "*** " + new string('a', 110), mode), "nested styles do not double-count coverage");
        }
        Require(!Eligible("**a** *b* `c` ~~d~~ " + new string('x', 205), MarkdownRenderModes.Off), "Off ignores style thresholds");
        Require(Eligible(new string('x', 401), MarkdownRenderModes.Off), "Off still obeys total-length rule");
        Console.WriteLine("PASS audited thresholds in three modes: syntax, links, nested coverage and Off");

        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.Black;
        root.Resources["WeakTextBrushKey"] = Brushes.Gray;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        root.Resources["HoverBrushKey"] = Brushes.LightGray;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        EdgeCapsulePreviewContext Context(EdgeCapsulePreviewInvalidationSource source, Func<string>? text = null) =>
            new(new PaperData(), () => "Audit", false, text ?? (() => new string('x', 450)),
                () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, source);
        MarkdownEdgePreviewPreload.Target Target(EdgeCapsulePreviewContext context) => new(context, root, new(460, 410), () => true);
        void Until(Func<bool> condition, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.ElapsedMilliseconds < 8000) Pump();
            Require(condition(), message);
        }
        try
        {
            var source = new EdgeCapsulePreviewInvalidationSource();
            var context = Context(source);
            var calls = 0;
            var requestedAt = Stopwatch.GetTimestamp();
            cache.RequestLayout(source, () => { calls++; return Target(context); });
            Require(calls == 0, "queuing does not read or parse content synchronously");
            window.Show(); Pump();
            Until(() => cache.ArtifactCount == 1 && cache.PendingCount == 0, "startup request completes without mouse activity");
            Require(calls == 1 && Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds >= 490,
                "shared half-second debounce precedes startup prelayout");
            var first = cache.Capture(context);
            Require(ReferenceEquals(first, cache.Capture(context)), "unchanged excerpt reuses prepared semantics");
            var binding = cache.Bind(context, first, 1)!;
            var key = MarkdownEdgePreviewPreload.MakeKey(binding, root, new Size(200, 100))!;
            cache.Forget(source);
            Require(!binding.Current && !cache.TryCreateSurface(key, out _), "forgotten artifact cannot create a late mount");
            Require(cache.ArtifactCount == 0 && cache.ExcerptCount == 0, "retirement releases both retained layers");
            Console.WriteLine("PASS startup-like late host, deferred readers, semantic reuse and retirement");

            calls = 0;
            cache.RequestLayout(source, () =>
            {
                calls++;
                if (calls == 1) cache.BeginDemand();
                return Target(context);
            });
            Until(() => calls == 2 && cache.ArtifactCount == 1 && cache.PendingCount == 0,
                "demand interruption retains and resumes the active item without a mouse retry");
            cache.Clear();
            var oldReads = 0;
            var newReads = 0;
            var replacementAt = 0L;
            var replacementReadAt = 0L;
            cache.RequestLayout(source, () =>
            {
                oldReads++;
                replacementAt = Stopwatch.GetTimestamp();
                cache.RequestLayout(source, () =>
                {
                    newReads++;
                    replacementReadAt = Stopwatch.GetTimestamp();
                    return Target(context);
                });
                return Target(context);
            });
            Until(() => newReads == 1 && cache.ArtifactCount == 1 && cache.PendingCount == 0,
                "replacement request survives the old drain and eventually completes");
            Require(oldReads == 1 && Stopwatch.GetElapsedTime(replacementAt, replacementReadAt).TotalMilliseconds >= 490,
                "an already-running drain does not bypass the replacement's 500ms debounce");
            cache.Clear();
            cache.RequestLayout(source, () =>
            {
                cache.Forget(source);
                return Target(context);
            });
            Until(() => cache.PendingCount == 0, "removed active item retires");
            Pump();
            Require(cache.ArtifactCount == 0 && root.Children.Count == 0, "removed active work cannot leave hidden controls or cache");
            Console.WriteLine("PASS queue interruption, replacement debounce and active Forget");
            cache.RequestLayout(new(), () => throw new InvalidOperationException("expected optional failure"));
            cache.RequestLayout(source, () => Target(context));
            Until(() => cache.ArtifactCount == 1 && cache.PendingCount == 0, "one failed target does not stall the queue");
            cache.Clear();
            var light = Context(new(), () => "short text");
            Require(!AwaitPreload(cache.WarmLayoutAsync(Target(light))) && cache.ExcerptCount == 0 && cache.ArtifactCount == 0,
                "light content retains no speculative body or semantic entry");
            Console.WriteLine("PASS failure isolation and light-note exclusion");
        }
        finally { cache.Clear(); window.Close(); Pump(); }
    }
}
