using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--live-fixture")) return RunLiveFixture();
        try
        {
            var checks = new (string Name, Action Run)[]
            {
                ("explicit request accepts external snapshot; no revision metadata", ExplicitImport),
                ("ordinary saves may overwrite unrequested file edits", NormalSaveWins),
                ("invalid or missing input never falls back to backup", InvalidInput),
                ("old async writes cannot replace an accepted import", SaveFence),
                ("model/list references survive accepted reload", ModelIdentities),
                ("opt-in three-way merge combines independent changes", MergeIndependentEdits),
                ("conflicts group by paper and archive input", MergePaperConflict),
                ("delete/edit conflicts honor both choices for notes and todos", MergePaperDeletionConflicts),
                ("stale external source cannot overwrite a newer edit", MergeRejectChangedSource),
                ("source changed after flush is rejected before replacement", MergeRejectDuringReplace),
                ("source changed during replace retry is also rejected", MergeRejectDuringRetry),
                ("large text diff avoids quadratic allocations", LargeConflictDiff)
            };
            foreach (var check in checks)
            {
                check.Run();
                Console.WriteLine("PASS " + check.Name);
            }
            RunIsolatedLiveChecks();
            Console.WriteLine($"PASS explicit reload: {checks.Length} storage groups and isolated WPF/API/MCP fixture");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static string Str(JsonNode? node) => node!.GetValue<string>();

    private static AppState NewState() => new()
    {
        TelemetryEnabled = false, UseCapsuleMode = false, EnableAnimations = false,
        Papers =
        [
            new() { Id = "note-a", Type = PaperTypes.Note, Content = "original", X = 120, Y = 120 },
            new() { Id = "todo", Type = PaperTypes.Todo,
                Items = [new() { Id = "t1", Text = "one" }, new() { Id = "t2", Text = "two", Order = 1 }] }
        ]
    };

    private sealed class Scope : IDisposable
    {
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "PaperTodo.DataReloadChecks", Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public AppState Memory { get; }
        public Scope()
        {
            Directory.CreateDirectory(DirectoryPath);
            Store = new StateStore(DirectoryPath, DurableAtomicFileWriter.Shared);
            Store.SaveJsonSync(Store.SerializeState(NewState()), 1);
            Memory = Store.Load();
        }
        public JsonObject Read() => J(File.ReadAllText(Store.FilePath));
        public void Write(JsonObject document) => File.WriteAllText(Store.FilePath, document.ToJsonString());
        public void Dispose() { try { Directory.Delete(DirectoryPath, recursive: true); } catch { } }
    }

    private static void ExplicitImport()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Content = "unsaved local";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external accepted";
        disk["papers"]![1]!["items"]![0]!["text"] = "external todo";
        // Legacy revisions are ignored, not adopted or tracked.
        disk["$paperTodoRevision"] = "legacy-history";
        s.Write(disk);
        var originalBytes = File.ReadAllBytes(s.Store.FilePath);
        Require(s.Memory.Papers[0].Content == "unsaved local", "editing file changed in-memory state");
        var commit = s.Store.ReloadExternalState(2);
        Require(commit.Result.Applied && commit.Result.Outcome == "applied" && commit.State != null,
            "explicit file import was rejected");
        Require(commit.State!.Papers[0].Content == "external accepted" &&
            commit.State.Papers[1].Items[0].Text == "external todo", "disk should win over local memory");
        Require(File.ReadAllBytes(s.Store.FilePath).SequenceEqual(originalBytes), "import rewrote the external file");
        s.Store.SaveJsonSync(s.Store.SerializeState(commit.State), 3);
        Require(!File.ReadAllText(s.Store.FilePath).Contains("$paperTodoRevision", StringComparison.Ordinal),
            "ordinary save retained a revision history protocol");
    }

    private static void NormalSaveWins()
    {
        using var s = new Scope();
        var disk = s.Read(); disk["theme"] = "dark"; s.Write(disk);
        Require(s.Memory.Theme != "dark", "disk edits mutated the live model");
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);
        Require(Str(s.Read()["theme"]) == s.Memory.Theme,
            "a normal save did not overwrite unrequested external edits");
    }

    private static void InvalidInput()
    {
        foreach (string text in new[]
        {
            "{", "null", "{}", "{\"papers\":null}",
            "{\"papers\":[{\"id\":\"a\"},{\"id\":\"a\"}]}",
            "{\"papers\":[{\"id\":\"a\",\"items\":[{\"id\":\"x\"},{\"id\":\"x\"}]}]}"
        })
        {
            using var s = new Scope();
            Require(s.Store.TryRefreshBackupFromPrimary(), "backup setup failed");
            File.WriteAllText(s.Store.FilePath, text);
            var result = s.Store.ReloadExternalState(2);
            Require(result.State == null && result.Result.Outcome == "invalid",
                "invalid input accepted: " + text);
            Require(File.ReadAllText(s.Store.FilePath) == text && s.Memory.Papers.Count == 2,
                "invalid input was overwritten during the request");
        }
        using var missing = new Scope();
        File.Delete(missing.Store.FilePath);
        Require(missing.Store.ReloadExternalState(2).Result.Outcome == "invalid" &&
            !File.Exists(missing.Store.FilePath), "missing input used an implicit fallback");
    }

    private static void SaveFence()
    {
        using var s = new Scope();
        var disk = s.Read(); disk["theme"] = "dark"; s.Write(disk);
        var commit = s.Store.ReloadExternalState(3);
        Require(commit.Result.Applied, "import failed");
        var stale = s.Store.SerializeState(s.Memory);
        s.Store.SaveJsonAsync(stale, 2).GetAwaiter().GetResult();
        s.Store.SaveJsonSync(stale, 1);
        Require(Str(s.Read()["theme"]) == "dark", "old autosave overwrote explicitly accepted import");
        s.Store.SaveJsonSync(stale, 4);
        Require(Str(s.Read()["theme"]) == s.Memory.Theme,
            "normal saving did not resume after accepting the external state");
    }

    private static void ModelIdentities()
    {
        var current = NewState();
        var paper = current.Papers[1]; var item = paper.Items[0]; var list = paper.Items;
        var papers = current.Papers;
        var next = StateStore.CopyForReload(current);
        next.Papers[1].Items[0].Text = "changed";
        next.Papers[1].Items[0].RestoreQuickLaunch(null, "C:\\fixture.txt", false);
        next.GlobalHotkeys["fixture"] = "Ctrl+F1";
        next.Papers.Add(new() { Id = "new-identity", Type = PaperTypes.Note });
        StateReloadModels.Apply(current, next);
        Require(ReferenceEquals(papers, current.Papers) && ReferenceEquals(paper, current.Papers[1]) &&
            ReferenceEquals(item, paper.Items[0]) && ReferenceEquals(list, paper.Items),
            "live model references were replaced");
        Require(item.Text == "changed" && item.LinkedPath == "C:\\fixture.txt",
            "item details were not applied");
        current.GlobalHotkeys["fixture"] = "Ctrl+F2";
        Require(next.GlobalHotkeys["fixture"] == "Ctrl+F1", "reconciliation mutated the source snapshot");
    }
}
