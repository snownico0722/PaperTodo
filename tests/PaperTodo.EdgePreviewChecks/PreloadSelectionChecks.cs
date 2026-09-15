using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using Tier = PaperTodo.MarkdownEdgePreviewPreload.PreloadTier;

internal static partial class Program
{
    private sealed class SelectionNote(string text)
    {
        internal string Text = text;
        internal bool Eligible = true;
        internal bool HostReady = true;
        internal int SourceReads;
        internal int TargetReads;
        internal Action? OnRead;
        internal readonly EdgeCapsulePreviewInvalidationSource Source = new();
        internal EdgeCapsulePreviewContext Context = null!;
        internal MarkdownEdgeCapsulePreviewRenderer.PreviewContent? Content;
    }

    private static void PreloadSelectionChecks()
    {
        Tier Classify(string text, string mode = MarkdownRenderModes.Full) =>
            MarkdownEdgePreviewPreload.Classify(MarkdownEdgeCapsulePreviewRenderer.CaptureContent(text, mode));
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
        {
            Require(Classify(" \n\t", mode) == Tier.Empty, "blank notes never consume a slot");
            Require(Classify(new string('a', 100), mode) == Tier.AnyNonempty &&
                    Classify(new string('a', 101), mode) == Tier.SecondRelaxation &&
                    Classify(new string('a', 200), mode) == Tier.SecondRelaxation &&
                    Classify(new string('a', 201), mode) == Tier.FirstRelaxation &&
                    Classify(new string('a', 400), mode) == Tier.FirstRelaxation &&
                    Classify(new string('a', 401), mode) == Tier.Heavy, "strict length boundaries across all tiers");
            Require(Classify("**" + new string('a', 9) + "**", mode) == Tier.AnyNonempty &&
                    Classify("**" + new string('a', 10) + "**", mode) == Tier.SecondRelaxation &&
                    Classify("**" + new string('a', 29) + "**", mode) == Tier.SecondRelaxation &&
                    Classify("**" + new string('a', 30) + "**", mode) == Tier.FirstRelaxation,
                "relaxed style coverage is inclusive and has no total-character gate");
            Require(Classify("**a** *b*", mode) == Tier.SecondRelaxation &&
                    Classify("**a** *b* ~~c~~", mode) == Tier.FirstRelaxation,
                "two/three short styled pieces qualify before the nonempty tier");
            Require(Classify("[a](https://example.com) [b](https://example.com)", mode) == Tier.SecondRelaxation,
                "short link labels count as semantic pieces");
            Require(Classify("[a](https://example.com/" + new string('x', 40) + ")", mode) == Tier.AnyNonempty,
                "a URL and syntax fading do not create styled coverage");
            Require(Classify("```\na\nb\n```", mode) == Tier.SecondRelaxation,
                "nonempty code lines count once and fences do not count");
        }
        Require(Classify("**a** *b* ~~c~~", MarkdownRenderModes.Off) == Tier.AnyNonempty &&
                Classify("**" + new string('a', 30) + "**", MarkdownRenderModes.Off) == Tier.AnyNonempty,
            "Off disables relaxed style conditions too");

        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        cache.SetEnabledForChecks(true);
        var root = new Grid();
        root.Resources["TextBrushKey"] = Brushes.Black;
        root.Resources["WeakTextBrushKey"] = Brushes.Gray;
        root.Resources["LinkBrushKey"] = Brushes.Blue;
        root.Resources["HoverBrushKey"] = Brushes.LightGray;
        root.Resources["PaperBorderBrushKey"] = Brushes.Gray;
        var window = new Window { Content = root, Width = 550, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        void Register(SelectionNote note) => cache.RequestLayout(note.Source,
            () => note.Eligible ? note.Context : null,
            content =>
            {
                note.Content = content;
                note.TargetReads++;
                return note.HostReady
                    ? MarkdownEdgePreviewPreload.ReadResult.Ready(new(note.Context, root, new(460, 410), () => note.Eligible, content))
                    : MarkdownEdgePreviewPreload.ReadResult.Deferred;
            });
        SelectionNote Add(string text)
        {
            var note = new SelectionNote(text);
            note.Context = new(new PaperData(), () => "Selection", false,
                () => { note.SourceReads++; note.OnRead?.Invoke(); return note.Text; },
                () => MarkdownRenderModes.Full, (_, _) => false, _ => false, () => new Style(), () => "", _ => { }, note.Source);
            Register(note);
            return note;
        }
        void Drain()
        {
            var task = cache.StartStartupWork();
            UntilReview(() => task.IsCompleted, "selection pass completes");
            task.GetAwaiter().GetResult();
        }
        bool Selected(SelectionNote note) => note.Content != null && cache.Bind(note.Context, note.Content, 1) != null;
        void Edit(SelectionNote note, string text)
        {
            note.Text = text;
            note.Source.Invalidate();
            cache.Invalidate(note.Source);
            Register(note);
        }
        try
        {
            window.Show(); Pump();
            var light = Enumerable.Range(0, 10).Select(i => Add("short " + i)).ToArray();
            var blank = Add(" \n");
            Require(light.All(note => note.SourceReads == 0), "registration does not synchronously read text");
            Drain();
            Require(cache.ArtifactCount == 10 && light.All(Selected) && !Selected(blank), "ten nonempty notes all warm");
            Require(light.All(note => note.SourceReads == 1 && note.TargetReads == 1),
                "selection, measurement and warming share one captured excerpt");
            var completions = cache.WarmCompletions;
            var eleventh = Add("short 10");
            Drain();
            Require(cache.ArtifactCount == 10 && light.All(Selected) && eleventh.TargetReads == 0 &&
                    cache.WarmCompletions == completions, "eleventh light note preserves the ten existing artifacts");
            Register(light[4]); Drain();
            Require(cache.WarmCompletions == completions, "unchanged re-registration preserves content and artifacts");

            light[0].Eligible = false;
            cache.Forget(light[0].Source);
            UntilReview(() => cache.PendingCount == 0 && cache.ArtifactCount == 10, "vacancy automatically fills through the existing debounce");
            Require(Selected(eleventh) && light.Skip(1).All(Selected) && !Selected(light[0]),
                "removal fills exactly one slot from dormant candidates");
            Require(cache.WarmCompletions == completions + 1, "vacancy does not rebuild the other nine artifacts");

            cache.Clear();
            var third = Enumerable.Range(0, 5).Select(i => Add("plain " + i)).ToArray();
            var second = Enumerable.Range(0, 4).Select(_ => Add("**abcdefghij**")).ToArray();
            var first = Enumerable.Range(0, 4).Select(_ => Add("**a** *b* ~~c~~")).ToArray();
            var heavy = Enumerable.Range(0, 3).Select(_ => Add(new string('x', 450))).ToArray();
            Drain();
            Require(heavy.All(Selected) && first.All(Selected) && second.Take(3).All(Selected) &&
                    !Selected(second[3]) && third.All(note => !Selected(note)) && cache.ArtifactCount == 10,
                "all heavy and earlier tiers remain; crossing tier fills only its three vacant slots");
            Edit(second[3], "**a** *b* ~~c~~"); Drain();
            Require(Selected(second[3]) && second.Take(2).All(Selected) && !Selected(second[2]) && cache.ArtifactCount == 10,
                "a higher tier takes priority while same-tier incumbents remain stable");
            Edit(heavy[0], ""); Drain();
            Require(!Selected(heavy[0]) && second.All(Selected) && cache.ArtifactCount == 10,
                "editing a selected note to empty fills its slot");

            cache.Clear();
            heavy = Enumerable.Range(0, 12).Select(_ => Add(new string('x', 450))).ToArray();
            var extra = Add("short"); Drain();
            Require(heavy.All(Selected) && !Selected(extra) && cache.ArtifactCount == 12,
                "the original heavy set is never capped at ten");

            cache.Clear();
            var deferred = Add("late host"); deferred.HostReady = false;
            var ready = Add("ready host"); Drain();
            Require(cache.ArtifactCount == 1 && cache.DeferredCount == 1 && deferred.TargetReads == 1,
                "selected unavailable hosts retain dormant work");
            deferred.HostReady = true; cache.Resume(deferred.Source);
            UntilReview(() => cache.PendingCount == 0 && cache.ArtifactCount == 2, "loaded host resumes its selected note");
            Require(ready.TargetReads == 1, "resuming a host does not rescan or rebuild another target");

            cache.Clear();
            var interrupted = Add("interrupted scan");
            interrupted.OnRead = () => { interrupted.OnRead = null; cache.BeginDemand(); };
            extra = Add("next candidate"); Drain();
            Require(cache.ArtifactCount == 0, "demand interrupts selection before any speculative layout");
            UntilReview(() => cache.PendingCount == 0 && cache.ArtifactCount == 2, "interrupted selection resumes without new mouse activity");
            Console.WriteLine("PASS progressive preload tiers, 10/11 stability, vacancy fill, edits, heavy retention and interrupted/deferred work");
        }
        finally { cache.Clear(); window.Close(); Pump(); }
    }
}
