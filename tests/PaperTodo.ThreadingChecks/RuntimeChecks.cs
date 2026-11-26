using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private sealed class RuntimeFixture : IDisposable
    {
        public const string Provider = "test.threading";
        public readonly PaperData Paper = new() { Id = "paper-1", Type = PaperTypes.Note, BodyProviderId = Provider };
        public readonly AppController Controller;
        public readonly PaperPluginRuntimePapersApi Api;
        public int Active = 1;

        public RuntimeFixture(string apiVersion = "2.3")
        {
            // Only the in-memory controller state required by the real presentation methods.
            // Bypass startup because its constructor loads user files and opens the native store.
            Controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            SetField(Controller, "<State>k__BackingField", new AppState { Papers = [Paper] });
            SetField(Controller, "_windows", new Dictionary<string, PaperWindow>());
            SetField(Controller, "_pluginRuntimePresentationCache", new Dictionary<string, Dictionary<string, PaperCapsulePresentation>>());
            Api = new PaperPluginRuntimePapersApi(
                Controller, Provider, () => Volatile.Read(ref Active) != 0, apiVersion);
        }
        public void Dispose() => Api.Dispose();
    }

    private static PaperCapsulePresentation Capsule(string text) => new()
    {
        PlainText = text,
        Components = [new PaperCapsuleComponent { Kind = PaperCapsuleComponentKind.Text, Text = text }]
    };

    private static void CheckExpiredRuntimeCalls()
    {
        Action<PaperPluginRuntimePapersApi>[] operations =
        [
            api => { _ = api.List(); },
            api => { _ = api.Get("paper-1"); },
            api => api.SetTitle("paper-1", "stale"),
            api => api.SetHeaderText("paper-1", "stale"),
            api => api.SetCapsulePresentation("paper-1", Capsule("stale")),
            api => api.SetCapsuleView("paper-1", Capsule("stale")),
            api => { _ = api.PostToBody("paper-1", JsonSerializer.SerializeToElement("stale")); },
            api => api.ResetWebDocumentPresentation()
        ];
        foreach (var operation in operations)
        {
            using var fixture = new RuntimeFixture("2.4");
            var pending = QueueWorker(() => operation(fixture.Api));
            Volatile.Write(ref fixture.Active, 0);
            try
            {
                Drain(pending);
                throw new InvalidOperationException("expired queued Runtime call was accepted");
            }
            catch (PaperTodoPluginException ex) when (ex.Code == "runtime_closed") { }
            Assert(string.IsNullOrEmpty(fixture.Paper.BodyHeaderText), "expired header reached the model");
            Assert(string.IsNullOrEmpty(fixture.Paper.BodyCapsuleText), "expired capsule reached the model");
        }
    }

    private static void CheckRuntimeListHasNoPresentationSideEffects()
    {
        using var fixture = new RuntimeFixture();
        fixture.Paper.BodyHeaderText = "retained-header";
        fixture.Paper.BodyCapsuleText = "retained-capsule";
        var retained = ReadField<Dictionary<string, Dictionary<string, PaperCapsulePresentation>>>(
            fixture.Controller,
            "_pluginRuntimePresentationCache");
        retained[RuntimeFixture.Provider] = new Dictionary<string, PaperCapsulePresentation>(StringComparer.Ordinal)
        {
            [fixture.Paper.Id] = Capsule("retained-capsule")
        };

        var papers = fixture.Api.List();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);

        Assert(papers.Count == 1 && papers[0].PaperId == fixture.Paper.Id, "runtime list did not return owned paper");
        Assert(fixture.Paper.BodyHeaderText == "retained-header", "runtime list cleared retained header");
        Assert(fixture.Paper.BodyCapsuleText == "retained-capsule", "runtime list cleared retained capsule text");
        Assert(retained.TryGetValue(RuntimeFixture.Provider, out var providerCache) &&
            providerCache.TryGetValue(fixture.Paper.Id, out var presentation) &&
            presentation.PlainText == "retained-capsule",
            "runtime list cleared retained rich capsule presentation");

        var addedBeforeStartupSnapshot = new PaperData
        {
            Id = "paper-2",
            Type = PaperTypes.Note,
            BodyProviderId = RuntimeFixture.Provider
        };
        fixture.Controller.State.Papers.Add(addedBeforeStartupSnapshot);
        var events = new List<PaperPluginRuntimeEvent>();
        using var subscription = fixture.Api.Subscribe(events.Add);

        var ordinaryList = fixture.Api.List();
        fixture.Api.Reconcile();
        Assert(ordinaryList.Any(paper => paper.PaperId == addedBeforeStartupSnapshot.Id) &&
            events.Count == 1 &&
            events[0].Kind == PaperPluginRuntimeEventKind.PaperAdded &&
            events[0].PaperId == addedBeforeStartupSnapshot.Id,
            "ordinary runtime list unexpectedly changed the event baseline");

        events.Clear();
        var addedDuringStartup = new PaperData
        {
            Id = "paper-3",
            Type = PaperTypes.Note,
            BodyProviderId = RuntimeFixture.Provider
        };
        fixture.Controller.State.Papers.Add(addedDuringStartup);
        var startupSnapshot = fixture.Api.CaptureStartupSnapshot();
        fixture.Api.Reconcile();
        Assert(startupSnapshot.Any(paper => paper.PaperId == addedDuringStartup.Id) &&
            events.Count == 0,
            "startup snapshot paper was reported again as PaperAdded");

        var addedAfterStartup = new PaperData
        {
            Id = "paper-4",
            Type = PaperTypes.Note,
            BodyProviderId = RuntimeFixture.Provider
        };
        fixture.Controller.State.Papers.Add(addedAfterStartup);
        fixture.Api.Reconcile();
        Assert(events.Count == 1 &&
            events[0].Kind == PaperPluginRuntimeEventKind.PaperAdded &&
            events[0].PaperId == addedAfterStartup.Id,
            "post-startup paper did not produce PaperAdded");
    }

    private static void CheckCapsulePublication()
    {
        using var fixture = new RuntimeFixture();
        fixture.Api.SetCapsulePresentation("paper-1", Capsule("initial"));
        var first = QueueWorker(() => fixture.Api.SetCapsulePresentation("paper-1", Capsule("A")));
        var second = QueueWorker(() => fixture.Api.SetCapsulePresentation("paper-1", Capsule("B")));
        var wasInitial = fixture.Api.TryGetCapsulePresentation("paper-1", out var before) && before?.PlainText == "initial";
        var modelWasInitial = fixture.Paper.BodyCapsuleText == "initial";
        Drain(Task.WhenAll(first, second));
        Assert(wasInitial && modelWasInitial, "queued requests published before their UI commit");
        Assert(fixture.Api.TryGetCapsulePresentation("paper-1", out var live), "missing live capsule");
        Assert(live?.PlainText == "B" && fixture.Paper.BodyCapsuleText == "B", "model/live cache ordering differs");
        var retained = ReadField<Dictionary<string, Dictionary<string, PaperCapsulePresentation>>>(fixture.Controller, "_pluginRuntimePresentationCache");
        Assert(retained[RuntimeFixture.Provider]["paper-1"].PlainText == "B", "retained cache ordering differs");
        fixture.Api.SetCapsulePresentation("paper-1", null);
        Assert(!fixture.Api.TryGetCapsulePresentation("paper-1", out _), "cleared capsule remains cached");
        Assert(fixture.Paper.BodyCapsuleText == string.Empty && !retained.ContainsKey(RuntimeFixture.Provider), "clear did not reach both stores");
    }

    private static void CheckInvalidOwner()
    {
        using var fixture = new RuntimeFixture();
        fixture.Controller.State.Papers.Add(new PaperData { Id = "other-paper", Type = PaperTypes.Todo });
        foreach (var publish in new Action[]
        {
            () => fixture.Api.SetHeaderText("other-paper", "invalid"),
            () => fixture.Api.SetCapsulePresentation("other-paper", Capsule("invalid"))
        })
        {
            try { publish(); throw new InvalidOperationException("invalid paper was accepted"); }
            catch (PaperTodoPluginException ex) when (ex.Code == "paper_not_owned") { }
        }
        Assert(!fixture.Api.TryGetCapsulePresentation("other-paper", out _), "unowned paper entered the cache");
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride("other-paper") == null,
            "old protocol published a workspace override");
    }

    private static void CheckWorkspaceCapsuleOwnership()
    {
        using var fixture = new RuntimeFixture("2.4");
        var todo = new PaperData { Id = "todo", Type = PaperTypes.Todo };
        var markdown = new PaperData { Id = "note", Type = PaperTypes.Note, BodyProviderId = PaperBodyProviderIds.Markdown };
        var other = new PaperData
        {
            Id = "other",
            Type = PaperTypes.Note,
            BodyProviderId = "test.other",
            BodyCapsuleText = "original"
        };
        fixture.Controller.State.Papers.AddRange([todo, markdown, other]);
        using var otherRuntime = new PaperPluginRuntimePapersApi(
            fixture.Controller, other.BodyProviderId, () => true, "2.4");

        fixture.Api.SetCapsulePresentation(todo.Id, Capsule("7 remaining"));
        fixture.Api.SetCapsulePresentation(other.Id, Capsule("override"));
        fixture.Api.SetCapsuleView(markdown.Id, Capsule("120 words"), _ => new System.Windows.Controls.TextBlock());
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(todo.Id)?.PlainText == "7 remaining",
            "a built-in Todo did not accept the presentation");
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(markdown.Id)?.CreateView != null,
            "a built-in Markdown Note did not accept the publishing Runtime's view factory");
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(other.Id)?.PlainText == "override" &&
            other.BodyCapsuleText == "original" && fixture.Api.Get(other.Id) == null,
            "a workspace override changed the target's own presentation or Runtime membership");

        otherRuntime.SetCapsulePresentation(todo.Id, Capsule("other Runtime"));
        fixture.Api.SetCapsulePresentation(todo.Id, null);
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(todo.Id)?.PlainText == "other Runtime",
            "an older publisher cleared another Runtime's override");

        markdown.BodyProviderId = RuntimeFixture.Provider;
        fixture.Api.SetCapsulePresentation(markdown.Id, null);
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(markdown.Id) == null,
            "after changing the target to its own paper, the Runtime could not clear its workspace override");
        fixture.Api.ClearWebDocumentCapsuleOverrides();
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(other.Id) == null &&
            other.BodyCapsuleText == "original", "ending a Web document did not restore the target presentation");

        fixture.Api.SetCapsulePresentation(other.Id, Capsule("temporary"));
        fixture.Api.Dispose();
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(other.Id) == null &&
            fixture.Controller.GetWorkspaceCapsuleOverride(todo.Id)?.PlainText == "other Runtime",
            "Runtime teardown did not revoke only the contributions it owns");
        otherRuntime.Dispose();
        Assert(fixture.Controller.GetWorkspaceCapsuleOverride(todo.Id) == null,
            "last publisher's teardown left a workspace override");
    }
}
