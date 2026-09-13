from pathlib import Path
import sys
root=Path(sys.argv[1]);p=root/'tests/PaperTodo.LifecycleChecks/Program.cs';t=p.read_text(encoding='utf-8-sig')
def one(a,b):
 global t
 if t.count(a)!=1:raise RuntimeError(f'anchor {t.count(a)} {a[:80]}')
 t=t.replace(a,b,1)
one('"scripts", "real-exit"];','"scripts", "real-exit", "pre-shell-cache", "real-exit-held-thread"];')
one('if (name == "real-exit")','if (name.StartsWith("real-exit"))') if t.count('if (name == "real-exit")')==1 else None
# Both parent verification and child execution include the surviving-thread fixture.
t=t.replace('if (name == "real-exit")','if (name.StartsWith("real-exit"))')
one('''                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());''','''                if (name == "real-exit-held-thread")
                    new Thread(() => Thread.Sleep(60_000)) { IsBackground = false }.Start();
                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());''')
one('''        try
        {
            await controller.StartAsync(createDefaultPaper: false);''','''        try
        {
            if (name == "pre-shell-cache")
            {
                await VerifyPreShellCache(controller, windows, cache);
                return;
            }
            await controller.StartAsync(createDefaultPaper: false);''')
one('    private static object Field(object target, string name) =>','''    private static async Task VerifyPreShellCache(AppController controller,
        Dictionary<string, PaperWindow> windows, MarkdownEdgePreviewPreload cache)
    {
        // Exercise real edge hosts without starting the optional full-shell drain. No timing
        // thresholds: prove that an existing artifact survives the first editor attachment.
        controller.State.EnableAnimations = false;
        var create = typeof(AppController).GetMethod("GetOrCreatePaperWindow", Private)!;
        foreach (var paper in controller.State.Papers)
            create.Invoke(controller, [paper, true]);
        controller.ArrangeDeepCapsules(animate: false, flushInitialPresentations: true);
        var window = windows["fixture-0"];
        Require(!window.IsShellBuilt, "edge host unexpectedly built the paper shell");
        var read = typeof(PaperWindow).GetMethod("ReadMarkdownPreloadTarget", Private)!;
        MarkdownEdgePreviewPreload.ReadResult Read() =>
            (MarkdownEdgePreviewPreload.ReadResult)read.Invoke(window, null)!;
        await Until(() => Read().State == MarkdownEdgePreviewPreload.Readiness.Ready, "shell-less preview host");
        var target = Read().Target!;
        Require(await cache.WarmLayoutAsync(target), "shell-less preview did not prepare");
        var version = target.Context.InvalidationSource.Version;
        var completions = cache.WarmCompletions;
        MarkdownEdgePreviewPreload.Key Key() => MarkdownEdgePreviewPreload.MakeKey(
            cache.Bind(target.Context, cache.Capture(target.Context), target.Context.Paper.TextZoom),
            target.Anchor, new Size(MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(target.Size), 0))!;
        Require(cache.TryGetArtifact(Key(), out var before), "prepared preview not reusable");
        window.EnsureShellBuilt();
        Require(window.IsShellBuilt && target.StillEligible(), "initial shell retired a valid preview reader");
        Require(target.Context.InvalidationSource.Version == version, "initial shell invalidated unchanged content");
        Require(cache.TryGetArtifact(Key(), out var after) && ReferenceEquals(before, after),
            "initial shell discarded or mismatched its existing artifact");
        Require(await cache.WarmLayoutAsync(target) && cache.WarmCompletions == completions,
            "initial shell required duplicate artifact preparation");

        var editor = Field(window, "_noteBox");
        editor.GetType().GetProperty("Text")!.SetValue(editor, "edited after delayed shell");
        window.CommitPendingNoteContentForSave();
        Require(target.Context.InvalidationSource.Version > version, "real edit no longer invalidates preview");
        var edited = Read().Target!;
        Require(await cache.WarmLayoutAsync(edited), "edited preview did not rebuild");
        typeof(PaperWindow).GetMethod("ReloadCurrentPaperBody", Private)!.Invoke(window, null);
        Require(!target.StillEligible(), "real body replacement retained an obsolete generation-0 reader");

        var demand = windows["fixture-1"];
        Require(!demand.IsShellBuilt, "demand fixture already has a shell");
        demand.ActivateFromEdgeShortcut();
        Require(demand.IsShellBuilt && !controller.State.Papers[1].IsCollapsed,
            "early expansion failed to build its paper on demand");
        Console.WriteLine("PASS pre-shell artifact reuse, edit invalidation, body replacement and early expansion");
    }

    private static object Field(object target, string name) =>''')
p.write_text(t,encoding='utf-8',newline='\n')
print('Added behavioral lifecycle checks')
