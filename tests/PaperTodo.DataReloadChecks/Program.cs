using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
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
                ("four rules, identical writes and absent versus null", MergeRules),
                ("entity identity, reorder and delete-versus-modify", IdentityAndDeletion),
                ("stale editor uses its original revision, not the latest save", StaleEditor),
                ("complete byte-exact conflict pair and partial merge", ConflictArchive),
                ("invalid/missing documents do not restore backup or overwrite input", InvalidDocuments),
                ("older asynchronous saves cannot overwrite an imported version", SaveFence),
                ("external edit during flush and replace retry is not overwritten", ReplaceRace),
                ("archive/primary failures leave the external input recoverable", FailurePreservation),
                ("unknown/evicted baseline conservatively retains memory", UnknownBaseline),
                ("conflict and exit snapshots protect image references", RecoveryImageReferences),
                ("copy retains model/list identities and quick-launch relationships", ModelIdentities)
            };
            foreach (var check in checks)
            {
                check.Run();
                Console.WriteLine("PASS " + check.Name);
            }
            RunIsolatedLiveChecks();
            Console.WriteLine($"PASS data reload: {checks.Length} storage/merge groups and isolated live WPF/API/MCP fixture");
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
    private static void MergeRules()
    {
        var result = StateJsonMerge.Merge(J("""{"external":0,"memory":0,"both":0,"same":0,"nullable":null}"""),
            J("""{"external":0,"memory":1,"both":1,"same":2,"nullable":null}"""),
            J("""{"external":1,"memory":0,"both":3,"same":2}"""));
        Require(result.State["external"]!.GetValue<int>() == 1, "external-only change lost");
        Require(result.State["memory"]!.GetValue<int>() == 1, "memory-only change lost");
        Require(result.State["both"]!.GetValue<int>() == 1 && result.Conflicts.Count == 1, "memory did not win the single conflict");
        Require(result.State["same"]!.GetValue<int>() == 2, "equal concurrent values conflicted");
        Require(!result.State.ContainsKey("nullable"), "deletion was confused with explicit null");
        var missing = StateJsonMerge.Merge(J("{}"), J("{\"a\":null}"), J("{\"a\":1}"));
        Require(!missing.Conflicts[0].BaselineExists && missing.Conflicts[0].PaperTodoExists, "absence flags lost");
    }

    private static void IdentityAndDeletion()
    {
        var b = J("""{"papers":[{"id":"a","content":"A","items":[]},{"id":"b","content":"B","items":[]},{"id":"c","content":"C","items":[]}]}""");
        var m = (JsonObject)b.DeepClone(); m["papers"]![1]!["content"] = "local B";
        var e = (JsonObject)b.DeepClone(); e["papers"]!.AsArray().RemoveAt(0); e["papers"]![0]!["title"] = "external title";
        var r = StateJsonMerge.Merge(b, m, e);
        Require(r.Conflicts.Count == 0 && r.State["papers"]!.AsArray().Count == 2, "array shift caused false conflicts");
        Require(Str(r.State["papers"]![0]!["content"]) == "local B", "identity-matched local edit lost");
        e = (JsonObject)b.DeepClone(); e["papers"]!.AsArray().RemoveAt(1);
        r = StateJsonMerge.Merge(b, m, e);
        Require(r.Conflicts.Count == 1 && r.State["papers"]!.AsArray().Count == 3, "external deletion defeated modified memory entity");
        m = (JsonObject)b.DeepClone(); m["papers"]!.AsArray().RemoveAt(1);
        e = (JsonObject)b.DeepClone(); e["papers"]![1]!["content"] = "external B";
        r = StateJsonMerge.Merge(b, m, e);
        Require(r.Conflicts.Count == 1 && r.State["papers"]!.AsArray().Count == 2, "memory deletion resurrected");
        m = (JsonObject)b.DeepClone(); e = (JsonObject)b.DeepClone();
        var last = e["papers"]!.AsArray()[2]!.DeepClone(); e["papers"]!.AsArray().RemoveAt(2); e["papers"]!.AsArray().Insert(0, last);
        Require(Str(StateJsonMerge.Merge(b, m, e).State["papers"]![0]!["id"]) == "c", "external reorder ignored");
        b = J("""{"papers":[{"id":"a","items":[{"id":"t1","text":"one"},{"id":"t2","text":"two"}]}]}""");
        m = (JsonObject)b.DeepClone(); m["papers"]![0]!["items"]![1]!["text"] = "local";
        e = (JsonObject)b.DeepClone(); e["papers"]![0]!["items"]!.AsArray().RemoveAt(0);
        r = StateJsonMerge.Merge(b, m, e);
        Require(r.Conflicts.Count == 0 && Str(r.State["papers"]![0]!["items"]![0]!["text"]) == "local", "todo ids were treated as indices");
    }

    private static AppState NewState() => new()
    {
        TelemetryEnabled = false, UseCapsuleMode = false, EnableAnimations = false,
        Papers = [new() { Id = "note-a", Type = PaperTypes.Note, Content = "original", X = 120, Y = 120 },
                  new() { Id = "todo", Type = PaperTypes.Todo, Items = [new() { Id = "t1", Text = "one" }, new() { Id = "t2", Text = "two", Order = 1 }] }]
    };

    private sealed class Scope : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "PaperTodo.DataReloadChecks", Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public AppState Memory { get; set; }
        public Scope(IDurableAtomicFileWriter? writer = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            var seed = new StateStore(DirectoryPath, DurableAtomicFileWriter.Shared);
            seed.SaveJsonSync(seed.SerializeState(NewState()), 1);
            Store = new StateStore(DirectoryPath, writer ?? DurableAtomicFileWriter.Shared);
            Memory = Store.Load(); Store.EnableExternalReload(Memory);
            Store.SaveJsonSync(Store.SerializeState(Memory), 1);
        }
        public JsonObject Read() => J(File.ReadAllText(Store.FilePath));
        public void Write(JsonObject value) => File.WriteAllText(Store.FilePath, value.ToJsonString());
        public void Dispose() { try { Directory.Delete(DirectoryPath, true); } catch { } }
    }

    private static void StaleEditor()
    {
        using var s = new Scope(); var external = s.Read();
        s.Memory.Papers[0].X = 500;
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);
        external["theme"] = "dark"; s.Write(external);
        var r = s.Store.ReloadExternalState(s.Memory, 3);
        Require(r.State?.Theme == "dark" && r.State.Papers[0].X == 500 && r.Result.ConflictCount == 0, "latest snapshot was incorrectly used as the editor's base");
        Require(!s.Store.HasExternalStateChange(), "own merged save triggered another reload");
        Require(s.Store.ReloadExternalState(r.State!, 4).Result.Outcome == "unchanged", "idempotent reread failed");
    }

    private static void ConflictArchive()
    {
        using var s = new Scope(); var external = s.Read();
        s.Memory.Papers[0].Content = "memory";
        external["papers"]![0]!["content"] = "external"; external["theme"] = "dark";
        var raw = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("// retained raw comment\n" + external.ToJsonString())).ToArray();
        File.WriteAllBytes(s.Store.FilePath, raw);
        File.WriteAllText(Path.Combine(s.DirectoryPath, "dataconflict(1).json"), "old archive");
        var r = s.Store.ReloadExternalState(s.Memory, 2);
        Require(r.Result.Outcome == "conflict" && r.Result.ConflictCount == 1 && r.Result.ConflictFile == "dataconflict(2).json", "conflict pair/number wrong");
        Require(r.State!.Theme == "dark" && r.State.Papers[0].Content == "memory", "partial merge policy wrong");
        Require(File.ReadAllBytes(Path.Combine(s.DirectoryPath, r.Result.ConflictFile!)).SequenceEqual(raw), "external source was normalized instead of preserved byte-for-byte");
        var diff = J(File.ReadAllText(Path.Combine(s.DirectoryPath, r.Result.ConflictDiffFile!)));
        Require(Str(diff["conflicts"]![0]!["path"]) == "papers[id=note-a].content", "conflict path was positional");
        Require(Str(diff["conflicts"]![0]!["baseline"]) == "original" && Str(diff["conflicts"]![0]!["paperTodo"]) == "memory" && Str(diff["conflicts"]![0]!["external"]) == "external", "three source values missing");
    }

    private static void InvalidDocuments()
    {
        var invalid = new[] { "{", "null", "{}", "{\"papers\":null}", "{\"papers\":[null]}", "{\"papers\":[{\"id\":\"a\"},{\"id\":\"a\"}]}",
            "{\"papers\":[{\"id\":\"a\",\"items\":[{\"id\":\"t\"},{\"id\":\"t\"}]}]}", "{\"papers\":[],\"theme\":\"dark\",\"theme\":\"light\"}" };
        foreach (var text in invalid)
        {
            using var s = new Scope(); Require(s.Store.TryRefreshBackupFromPrimary(), "backup seed failed");
            File.WriteAllText(s.Store.FilePath, text);
            var r = s.Store.ReloadExternalState(s.Memory, 2);
            Require(r.State == null && r.Result.Outcome == "invalid", "malformed document accepted: " + text);
            Require(File.ReadAllText(s.Store.FilePath) == text && s.Memory.Papers.Count == 2, "invalid input or memory was overwritten");
            Require(!s.Store.TryRefreshBackupFromPrimary(), "pending external input replaced healthy backup");
            Throws<ExternalStatePendingException>(() => s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 3));
        }
        using var missing = new Scope(); File.Delete(missing.Store.FilePath);
        Require(missing.Store.ReloadExternalState(missing.Memory, 2).Result.Outcome == "invalid" && !File.Exists(missing.Store.FilePath), "missing file became empty defaults");
    }

    private static void SaveFence()
    {
        using var s = new Scope(); var external = s.Read(); external["theme"] = "dark"; s.Write(external);
        Throws<ExternalStatePendingException>(() => s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2));
        var r = s.Store.ReloadExternalState(s.Memory, 3); Require(r.Result.Applied, "reload failed");
        s.Store.SaveJsonAsync(s.Store.SerializeState(s.Memory), 2).GetAwaiter().GetResult();
        Require(Str(s.Read()["theme"]) == "dark", "queued stale save overwrote reload");
    }

    private static void ReplaceRace()
    {
        var inject = false;
        var writer = new DurableAtomicFileWriter((stage, path) =>
        {
            if (inject && stage == DurableAtomicWriteStage.BeforeReplace)
            {
                inject = false; File.WriteAllText(path, "external arrived during flush");
            }
        });
        using var s = new Scope(writer);
        inject = true;
        Throws<ExternalStatePendingException>(() => s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2));
        Require(File.ReadAllText(s.Store.FilePath) == "external arrived during flush", "replace guard did not run after flush");
        var operations = new RacingOperations();
        using var retry = new Scope(new DurableAtomicFileWriter(fileOperations: operations));
        operations.Race = true;
        Throws<ExternalStatePendingException>(() => retry.Store.SaveJsonSync(retry.Store.SerializeState(retry.Memory), 2));
        Require(File.ReadAllText(retry.Store.FilePath) == "external arrived during retry", "replace retry skipped guard");
    }
    private sealed class RacingOperations : IDurableAtomicFileOperations
    {
        public bool Race;
        public void FlushToDisk(FileStream stream) => stream.Flush(true);
        public void Replace(string temp, string target)
        {
            if (Race) { Race = false; File.WriteAllText(target, "external arrived during retry"); throw new IOException("sharing race"); }
            File.Move(temp, target, true);
        }
        public void Delay(TimeSpan delay) { }
        public void Delete(string path) => File.Delete(path);
    }

    private static void FailurePreservation()
    {
        using (var s = new Scope())
        {
            var external = s.Read(); external["papers"]![0]!["content"] = "external"; s.Memory.Papers[0].Content = "memory"; s.Write(external);
            Directory.CreateDirectory(Path.Combine(s.DirectoryPath, "dataconflictdiff(1).json"));
            var raw = File.ReadAllBytes(s.Store.FilePath);
            var r = s.Store.ReloadExternalState(s.Memory, 2);
            Require(!r.Result.Applied && r.State == null && File.ReadAllBytes(s.Store.FilePath).SequenceEqual(raw), "archive failure still replaced primary");
        }
        var fail = false;
        using (var s = new Scope(new DurableAtomicFileWriter((stage, _) =>
               { if (fail && stage == DurableAtomicWriteStage.BeforeReplace) throw new IOException("disk failure"); })))
        {
            var external = s.Read(); external["theme"] = "dark"; s.Write(external);
            var raw = File.ReadAllBytes(s.Store.FilePath); fail = true;
            Require(s.Store.ReloadExternalState(s.Memory, 2).Result.Outcome == "failed" && File.ReadAllBytes(s.Store.FilePath).SequenceEqual(raw), "primary failure changed candidate");
        }
    }

    private static void UnknownBaseline()
    {
        using var s = new Scope(); var external = s.Read(); external[StateStore.ReloadRevisionProperty] = "unknown";
        external["theme"] = "dark"; s.Write(external);
        var r = s.Store.ReloadExternalState(s.Memory, 2);
        Require(r.Result.Outcome == "conflict" && r.State!.Theme == s.Memory.Theme, "unknown base was guessed");
        var diff = J(File.ReadAllText(Path.Combine(s.DirectoryPath, r.Result.ConflictDiffFile!)));
        Require(Str(diff["conflicts"]![0]!["reason"]) == "unknown_baseline", "unknown-base reason not exposed");
        var old = s.Read(); s.Memory = r.State!;
        for (var version = 3; version < 23; version++) s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), version);
        old["theme"] = "light"; s.Write(old);
        Require(s.Store.ReloadExternalState(s.Memory, 23).Result.Outcome == "conflict", "evicted base silently rebased");
    }

    private static void RecoveryImageReferences()
    {
        using var s = new Scope(); var snapshot = StateStore.CopyForReload(s.Memory);
        snapshot.Papers[0].Content = "![retained](i:123)";
        File.WriteAllText(Path.Combine(s.DirectoryPath, "dataconflict(1).json"), s.Store.SerializeState(snapshot));
        Require(s.Store.TryCollectProtectedImageIds(s.Memory, out var ids) && ids.Contains("123"), "conflict image not protected");
        var external = s.Read(); external["papers"]![0]!["content"] = "![pending](i:789)"; s.Write(external);
        Require(s.Store.TryCollectProtectedImageIds(s.Memory, out ids) && ids.Contains("789"), "pending external primary image not protected");
        snapshot.Papers[0].Content = "![exit](i:456)";
        var exit = s.Store.SavePendingExitRecovery(snapshot);
        Require(File.Exists(exit) && s.Store.TryCollectProtectedImageIds(s.Memory, out ids) && ids.Contains("456"), "exit snapshot not protected");
        File.WriteAllText(Path.Combine(s.DirectoryPath, "dataconflict(2).json"), "{");
        Require(!s.Store.TryCollectProtectedImageIds(s.Memory, out _), "unreadable recovery did not stop image collection");
    }

    private static void ModelIdentities()
    {
        var current = NewState(); var paper = current.Papers[1]; var item = paper.Items[0]; var list = paper.Items; var papers = current.Papers;
        var next = StateStore.CopyForReload(current); next.Papers[1].Items[0].Text = "changed";
        next.Papers[1].Items[0].RestoreQuickLaunch(null, "C:\\fixture.txt", false);
        next.GlobalHotkeys["fixture"] = "Ctrl+F1";
        next.Papers.Add(new() { Id = "new-identity", Type = PaperTypes.Note });
        StateReloadModels.Apply(current, next);
        Require(ReferenceEquals(papers, current.Papers) && ReferenceEquals(paper, current.Papers[1]) && ReferenceEquals(item, paper.Items[0]) && ReferenceEquals(list, paper.Items), "live identity replaced");
        Require(item.Text == "changed" && item.LinkedPath == "C:\\fixture.txt" && item.LinkedPathIsDirectory == false, "private-set relation lost");
        current.GlobalHotkeys["fixture"] = "Ctrl+F2"; current.Papers[^1].Title = "live-only";
        Require(next.GlobalHotkeys["fixture"] == "Ctrl+F1" && next.Papers[^1].Title == "", "live objects mutated the committed comparison snapshot");
    }
}
