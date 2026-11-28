using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--shutdown-workspace")) return RunWorkspaceShutdownFixture();
        if (args.Contains("--kill-writer")) return RunKillWriter(args);
        if (args.Contains("--shutdown-merge")) return RunShutdownMergeFixture();
        if (args.Contains("--live-fixture")) return RunLiveFixture();
        if (args.Contains("--history-dialog")) return RunHistoryDialogFixture();
        if (args.Contains("--history-actions")) return RunHistoryActionsFixture();
        if (args.Contains("--workspace-failure")) return RunWorkspaceFailureFixture();
        if (args.Contains("--workspace-move-recovery-exit")) return RunWorkspaceMoveRecoveryExitFixture();
        if (args.Contains("--exit-fixture")) return RunDeferredExitFixture();
        if (args.Contains("--passive-dispose")) return RunPassiveDisposeFixture();
        if (args.Contains("--workspace-fixture")) return RunWorkspaceFixture();
        if (args.Contains("--workspace-recovery-image-fixture")) return RunWorkspaceRecoveryImageFixture();
        if (args.Contains("--workspace-missing-images-fixture")) return RunWorkspaceMissingImagesFixture();
        try
        {
            var checks = new (string Name, Action Run)[]
            {
                ("explicit request accepts external snapshot; no revision metadata", ExplicitImport),
                ("reload and merge preserve legacy long notes and reject new growth", LegacyNoteLengthEdits),
                ("invalid or missing input never falls back to backup", InvalidInput),
                ("persisted notes at the limit retain their image anchor through reload and catalog updates", TrailingImageAnchorLength),
                ("old async writes cannot replace an accepted import", SaveFence),
                ("model/list references survive accepted reload", ModelIdentities),
                ("opt-in three-way merge combines independent changes", MergeIndependentEdits),
                ("derived todo order does not create false conflicts", MergeDerivedTodoOrderDoesNotConflict),
                ("caller baseline survives newer sync and async saves", MergeOriginalBaseline),
                ("merge rejects missing baseline without whole-state choice", MergeMissingBaseline),
                ("invalid baseline is rejected without changing disk or memory", MergeInvalidBaseline),
                ("unchanged external snapshot retains local edits", MergeUneditedExternal),
                ("conflicts group by paper and archive input", MergePaperConflict),
                ("delete/edit conflicts honor both choices for notes and todos", MergePaperDeletionConflicts),
                ("merge preserves links to a paper chosen to survive", MergeDeletedPaperRetainsChosenLinks),
                ("merge resolves the complete Todo link target", MergeQuickLaunchTarget),
                ("merge keeps reminder delivery with its scheduled occurrence", MergeReminderOccurrence),
                ("conflict archives retain the latest five complete groups",
                    ConflictArchiveRetentionChecks),
                ("source changed after flush is rejected before replacement", MergeRejectDuringReplace),
                ("source changed during replace retry is also rejected", MergeRejectDuringRetry),
                ("large text diff avoids quadratic allocations", LargeConflictDiff),
                ("three conflict policies preserve independent edits and archive actual decisions", MergePolicies),
                ("mixed user decisions share the state and archive owner", MergeMixedPolicyChoices),
                ("merged normalization waits for the selected settings", MergeNormalizeAfterChoices),
                ("lowering external title limit preserves unchanged title until resolution", MergeLoweredTitleLimitPreservesUnchangedTitle),
                ("legacy shortcut values migrate on reload and merge", MergeLegacyShortcutMigration),
                ("identity insertion preserves independent additions and order", MergeIdentityOrderChecks)
            };
            foreach (var check in checks)
            {
                check.Run();
                Console.WriteLine("PASS " + check.Name);
            }
            CoreHistoryBackupChecks();
            HistoryRecoveryChecks();
            WorkspaceStorageChecks();
            WorkspaceFollowingMergeBaseline();
            StorageSafetyChecks();
            WorkspaceWriteSafetyChecks();
            KillSafetyChecks();
            RunIsolatedLiveChecks();
            RunIsolatedLiveChecks("--history-dialog");
            RunIsolatedLiveChecks("--history-actions");
            RunIsolatedLiveChecks("--workspace-failure");
            RunIsolatedLiveChecks("--workspace-move-recovery-exit");
            RunIsolatedLiveChecks("--shutdown-merge");
            RunIsolatedLiveChecks("--shutdown-workspace");
            RunIsolatedLiveChecks("--exit-fixture");
            RunIsolatedLiveChecks("--passive-dispose");
            RunIsolatedLiveChecks("--workspace-fixture");
            RunIsolatedLiveChecks("--workspace-recovery-image-fixture");
            RunIsolatedLiveChecks("--workspace-missing-images-fixture");
            Console.WriteLine($"PASS explicit reload: {checks.Length} storage groups and isolated WPF/API/MCP fixture");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
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
        public string BaselineJson { get; }
        public Scope()
        {
            Directory.CreateDirectory(DirectoryPath);
            Store = new StateStore(DirectoryPath, DurableAtomicFileWriter.Shared);
            Store.SaveJsonSync(Store.SerializeState(NewState()), 1);
            Memory = Store.Load();
            BaselineJson = File.ReadAllText(Store.FilePath);
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

    private static void TrailingImageAnchorLength()
    {
        using var s = new Scope();
        var image = MarkdownImageReferences.CreateReference("001");
        var content = new string('x', PaperWindow.NoteTextMaxLength - image.Length - 2) +
            "\r\n" + image + "\r\n";
        s.Memory.Papers[0].Content = content;
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 2);

        s.Store.UpdateWorkspaceConfiguration(new WorkspaceConfiguration(), 3);
        var reload = s.Store.ReloadExternalState(4);
        Require(reload.Result.Applied && reload.State?.Papers[0].Content == content,
            "the editor's trailing image anchor was rejected or changed");

        var oversized = s.Read();
        oversized["papers"]![0]!["content"] = "x" + content;
        s.Write(oversized);
        Require(s.Store.ReloadExternalState(5).Result.Outcome == "invalid",
            "the exception for the image anchor also allowed oversized note text");
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

    private static void LegacyNoteLengthEdits()
    {
        using var s = new Scope();
        var historical = new string('x', PaperWindow.NoteTextMaxLength + 5);
        s.Memory.Papers[0].Content = historical;
        var baseline = s.Store.SerializeState(s.Memory);
        var external = J(baseline);
        external["papers"]![0]!["title"] = "rename";
        s.Write(external);
        var shorter = historical[..^1];
        s.Memory.Papers[0].Content = shorter;
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, baseline);
        Require(preview.State?.Papers[0].Content == shorter && preview.Conflicts is { Count: 0 },
            "a historical baseline larger than the current note blocked an independent rename");
        s.Store.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2, s.Memory);
        var merged = s.Read();
        Require(Str(merged["papers"]![0]!["content"]) == shorter &&
                Str(merged["papers"]![0]!["title"]) == "rename",
            "accepting the merge lost the shorter text or the rename");

        merged["papers"]![0]!["content"] = historical + "x";
        s.Write(merged);
        Require(s.Store.ReloadExternalState(3, s.Memory).Result.Outcome == "invalid",
            "reload allowed the same paper to grow again above the limit");
        var conflict = s.Store.PreviewExternalMerge(s.Memory, 3, baseline);
        Require(conflict.Conflicts is { Count: > 0 }, "the two edits did not produce a content conflict");
        Throws<System.Text.Json.JsonException>(() => s.Store.CommitExternalMerge(
            ResolvePreview(conflict, ConflictChoice.KeepExternal), conflict.ObservedDiskJson!, 3, s.Memory));
        merged["papers"]![0]!["content"] = shorter[..^1];
        s.Write(merged);
        Require(s.Store.ReloadExternalState(3, s.Memory).State?.Papers[0].Content == shorter[..^1],
            "reload rejected a valid shorter note");
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
