using System.Runtime.CompilerServices;
using System.Text.Json;
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

        public RuntimeFixture()
        {
            // Only the in-memory controller state required by the real presentation methods.
            // Bypass startup because its constructor loads user files and opens the native store.
            Controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            SetField(Controller, "<State>k__BackingField", new AppState { Papers = [Paper] });
            SetField(Controller, "_windows", new Dictionary<string, PaperWindow>());
            SetField(Controller, "_pluginRuntimePresentationCache", new Dictionary<string, Dictionary<string, PaperCapsulePresentation>>());
            Api = new PaperPluginRuntimePapersApi(Controller, Provider, () => Volatile.Read(ref Active) != 0);
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
            api => { _ = api.PostToBody("paper-1", JsonSerializer.SerializeToElement("stale")); },
            api => api.ResetWebDocumentPresentation()
        ];
        foreach (var operation in operations)
        {
            using var fixture = new RuntimeFixture();
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
            Assert(ReadField<HashSet<string>>(fixture.Api, "_publishedHeaderPaperIds").Count == 0,
                "expired call changed header publication markers");
            Assert(ReadField<HashSet<string>>(fixture.Api, "_publishedCapsulePaperIds").Count == 0,
                "expired call changed capsule publication markers");
        }
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
        Assert(ReadField<HashSet<string>>(fixture.Api, "_publishedHeaderPaperIds").Count == 0, "unowned header marked published");
        Assert(ReadField<HashSet<string>>(fixture.Api, "_publishedCapsulePaperIds").Count == 0, "unowned capsule marked published");
    }
}
