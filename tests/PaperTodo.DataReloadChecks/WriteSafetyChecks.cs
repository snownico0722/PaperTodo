using System.IO;
using System.Text.Json.Nodes;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void StorageSafetyChecks()
    {
        ExternalEditsDoNotBlockOrdinarySaves();
        FailedExplicitRequestsDoNotFenceQueuedSaves();
        ExplicitMergeRejectsChangedSource();
        OrdinarySavesDoNotInspectExternalChanges();
        TerminationRejectsQueuedWrites();
        ConflictImagesRemainRecoverable();
        ArchiveFailureNeverCommits();
        PostCommitRetentionCancellationCannotRejectMerge();
        CorruptLoadsRejectBlankReplacement();
        InterruptedWriteSelectionChecks();
        OrphanedTempImageAndWriteChecks();
        RecoveredBackupDoesNotOverwriteRepairedPrimary();
        PluginExitFailureRetainsDirtyData();
        Console.WriteLine("PASS write safety: legacy ordinary saves, explicit transaction validation, shutdown, recovery images and archive failures");
    }

    private static void InterruptedWriteSelectionChecks()
    {
        foreach (var backup in new[] { false, true })
        {
            using var scope = new Scope();
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            var tempPath = (backup ? scope.Store.BackupPath : scope.Store.FilePath) + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var restored = store.Load(out var empty);
            Require(!empty && store.NeedsStartupRecoverySave &&
                    restored.Papers.Single(p => p.Id == "note-a").Content == "original" &&
                    File.ReadAllBytes(tempPath).SequenceEqual(bytes),
                "a unique complete temp did not automatically restore the original papers");
            store.SaveJsonSync(store.SerializeState(restored), 2);
            Require(File.Exists(store.FilePath) && !File.Exists(tempPath) &&
                    !Directory.GetFiles(scope.DirectoryPath, "*.orphaned.*.tmp").Any() &&
                    store.Load().Papers.Single(p => p.Id == "note-a").Content == "original",
                "successful interrupted-write recovery did not retire its temporary source");
        }

        using (var scope = new Scope())
        {
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            var orphan = Path.Combine(scope.DirectoryPath, "data.json.orphaned.20261001-000000-000.tmp");
            File.WriteAllBytes(orphan, bytes);
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var state = recovered.Load(out var empty);
            Require(!empty && state.Papers.Count == 2, "valid legacy orphan was not considered");
            recovered.SaveJsonSync(recovered.SerializeState(state), 2);
            Require(!File.Exists(orphan), "consumed orphan was left behind");
        }

        using (var scope = new Scope())
        {
            var aBytes = File.ReadAllBytes(scope.Store.FilePath);
            var different = scope.Store.Load();
            different.Papers[0].Content = "conflicting alternate temp";
            var bBytes = Encoding.UTF8.GetBytes(scope.Store.SerializeState(different));
            File.Delete(scope.Store.FilePath);
            File.WriteAllBytes(scope.Store.FilePath + ".tmp", aBytes);
            File.WriteAllBytes(scope.Store.BackupPath + ".tmp", bBytes);
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidDataException>(() => reader.Load());
            Require(!File.Exists(scope.Store.FilePath) &&
                    File.ReadAllBytes(scope.Store.FilePath + ".tmp").SequenceEqual(aBytes) &&
                    File.ReadAllBytes(scope.Store.BackupPath + ".tmp").SequenceEqual(bBytes),
                "ambiguous temporary versions were silently chosen or overwritten");
        }
        // Complete documents may differ only in whitespace or property layout.
        // That is ONE full snapshot, not two conflicting revisions, and neither
        // a per-field merge nor a loss of the compact candidate is acceptable.
        using (var scope = new Scope())
        {
            var pretty = File.ReadAllBytes(scope.Store.FilePath);
            var compact = Encoding.UTF8.GetBytes(
                JsonNode.Parse(Encoding.UTF8.GetString(pretty))!.ToJsonString());
            Require(!pretty.SequenceEqual(compact), "semantic equality fixture lost formatting distinction");
            File.Delete(scope.Store.FilePath);
            File.WriteAllBytes(scope.Store.FilePath + ".tmp", pretty);
            File.WriteAllBytes(scope.Store.BackupPath + ".tmp", compact);
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var state = recovered.Load(out var empty);
            Require(!empty && state.Papers[0].Content == "original",
                "two complete but differently formatted documents were treated as competing versions");
            recovered.SaveJsonSync(recovered.SerializeState(state), 2);
            Require(File.Exists(recovered.FilePath) &&
                    !File.Exists(scope.Store.FilePath + ".tmp") &&
                    !File.Exists(scope.Store.BackupPath + ".tmp") &&
                    !Directory.GetFiles(scope.DirectoryPath, "*.orphaned.*.tmp").Any(),
                "semantically equivalent temporary documents were not both retired after full restore");
        }

        // Unknown properties still belong to the complete JSON document.
        // A difference there cannot be ignored by comparing only AppState fields.
        using (var scope = new Scope())
        {
            var first = scope.Read();
            var second = scope.Read();
            first["futureExtension"] = "revision-one";
            second["futureExtension"] = "revision-two";
            File.Delete(scope.Store.FilePath);
            File.WriteAllText(scope.Store.FilePath + ".tmp", first.ToJsonString());
            File.WriteAllText(scope.Store.BackupPath + ".tmp", second.ToJsonString());
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidDataException>(() => recovered.Load());
            Require(!File.Exists(recovered.FilePath),
                "two full documents with different unknown properties were silently merged");
        }
        Console.WriteLine("PASS unique interrupted writes auto-restore, ambiguous versions stop safely");
    }

    private static void ExternalEditsDoNotBlockOrdinarySaves()
    {
        using var s = new Scope();
        var external = s.Read();
        external["papers"]![0]!["content"] = "external version";
        s.Write(external);
        var local = s.Store.SerializeState(s.Memory);
        s.Store.SaveJsonSync(local, 2);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "original",
            "ordinary save unexpectedly detected or deferred an external edit");
        Require(s.Store.TryRefreshBackupFromPrimary(),
            "ordinary save disabled the periodic backup path");
    }

    private static void FailedExplicitRequestsDoNotFenceQueuedSaves()
    {
        // These versions model an autosave queued before the explicit request. Even if
        // the request fails, the older save must still be allowed to reach the file.
        foreach (var scenario in new[] { "invalid_baseline", "invalid_reload", "invalid_merge", "missing_reload" })
        {
            using var s = new Scope();
            s.Memory.Papers[0].Content = "queued ordinary edit";
            var queuedSnapshot = s.Store.SerializeState(s.Memory);
            if (scenario is "invalid_reload" or "invalid_merge")
                File.WriteAllText(s.Store.FilePath, "{broken");
            else if (scenario == "missing_reload")
                File.Delete(s.Store.FilePath);

            var rejected = scenario switch
            {
                "invalid_baseline" => s.Store.PreviewExternalMerge(s.Memory, 3, "{broken"),
                "invalid_merge" => s.Store.PreviewExternalMerge(s.Memory, 3, s.BaselineJson),
                _ => s.Store.ReloadExternalState(3)
            };
            Require(rejected.State == null && rejected.Result.Outcome == "invalid",
                "invalid explicit operation unexpectedly succeeded: " + scenario);
            s.Store.SaveJsonAsync(queuedSnapshot, 2).GetAwaiter().GetResult();
            Require(Str(s.Read()["papers"]![0]!["content"]) == "queued ordinary edit",
                "failed explicit operation invalidated a queued autosave: " + scenario);
        }
    }

    private static void ExplicitMergeRejectsChangedSource()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Content = "retained local";
        var external = s.Read();
        external["papers"]![0]!["content"] = "first external";
        s.Write(external);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        external["papers"]![0]!["content"] = "changed during review";
        s.Write(external);
        var bytes = File.ReadAllBytes(s.Store.FilePath);
        Throws<ExternalDocumentChangedException>(() => s.Store.CommitExternalMerge(
            ResolvePreview(preview, ConflictChoice.KeepExternal), preview.ObservedDiskJson!, 2));
        s.Store.SaveJsonSync(s.BaselineJson, 1); // Old queued snapshots remain fenced.
        Require(File.ReadAllBytes(s.Store.FilePath).SequenceEqual(bytes),
            "rejected explicit merge committed a stale candidate");
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 3);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "retained local",
            "ordinary save remained blocked after rejected explicit merge");

        File.WriteAllText(s.Store.FilePath, "{broken");
        Require(s.Store.ReloadExternalState(4).Result.Outcome == "invalid" &&
                File.ReadAllText(s.Store.FilePath) == "{broken",
            "invalid explicit reload rewrote malformed source");
        s.Store.SaveJsonSync(s.Store.SerializeState(s.Memory), 5);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "retained local",
            "invalid explicit reload permanently disabled ordinary saving");
    }

    private static void OrdinarySavesDoNotInspectExternalChanges()
    {
        using var s = new Scope();
        var later = s.Read();
        later["papers"]![0]!["content"] = "arrived after temp flush";
        var writer = new DurableAtomicFileWriter((stage, target) =>
        {
            if (target == s.Store.FilePath && stage == DurableAtomicWriteStage.AfterFlush) s.Write(later);
        });
        var store = new StateStore(s.DirectoryPath, writer);
        var state = store.Load();
        state.Papers[0].Content = "ordinary save";
        store.SaveJsonSync(store.SerializeState(state), 2);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "ordinary save" &&
            !File.Exists(store.FilePath + ".tmp"),
            "ordinary save performed an unintended external-source check");
    }

    private static void TerminationRejectsQueuedWrites()
    {
        using var s = new Scope();
        var before = File.ReadAllBytes(s.Store.FilePath);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lifetime = new PersistenceWriteLifetime();
        var calls = 0;
        var writer = new DurableAtomicFileWriter((stage, _) =>
        {
            if (stage != DurableAtomicWriteStage.BeforeTempOpen) return;
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("writer was not released");
        });
        var store = new StateStore(s.DirectoryPath, writer) { WriteLifetime = lifetime };
        var state = store.Load(); state.Papers[0].Content = "not admitted after shutdown";
        var json = store.SerializeState(state);
        var active = Task.Run(() => store.SaveJsonSync(json, 2));
        Require(entered.Wait(TimeSpan.FromSeconds(20)), "writer did not reach controlled boundary");
        var queued = store.SaveJsonAsync(json, 3);
        store.StopWritesForTermination();
        release.Set();
        Throws<OperationCanceledException>(() => active.GetAwaiter().GetResult());
        Throws<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
        Require(calls == 1 && File.ReadAllBytes(store.FilePath).SequenceEqual(before) &&
            !File.Exists(store.FilePath + ".tmp"), "queued shutdown write opened a temporary file");
        Require(!store.TryRefreshBackupFromPrimary(), "shutdown created a backup");
        using var plugin = new PaperBodyPluginDataStore(Path.Combine(s.DirectoryPath, "plugins"),
            DurableAtomicFileWriter.Shared, 60_000, 60_000) { WriteLifetime = lifetime };
        plugin.SavePaperState("fixture", "paper", 1, "{\"pending\":true}");
        plugin.Dispose();
        Require(!Directory.Exists(plugin.DataRoot) || !Directory.GetFiles(plugin.DataRoot, "*.json").Any(),
            "plugin final flush bypassed shared termination signal");
    }

    private static void ConflictImagesRemainRecoverable()
    {
        using var s = new Scope();
        using var images = new NoteImageStore(Path.Combine(s.DirectoryPath, "note-assets.lmdb"));
        images.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
        var id = images.ImportBitmapSource("note-a", bitmap).Id;
        s.Memory.Papers[0].Content = "local-only\n" + MarkdownImageReferences.CreateReference(id);
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external without image"; s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        var files = s.Store.CommitExternalMerge(ResolvePreview(preview, ConflictChoice.KeepExternal),
            preview.ObservedDiskJson!, 2);
        var loaded = s.Store.Load();
        Require(s.Store.TryCollectProtectedImageIds(loaded, out var ids) && ids.Contains(id),
            "local-only image in conflict diff was not protected");
        images.CollectUnprotectedImages(ids);
        Require(images.TryGetEncodedImageBytes(id, out _, out _), "actual LMDB GC deleted recoverable image");
        File.WriteAllText(Path.Combine(s.DirectoryPath, files.ConflictDiffFile!), "{\"conflicts\":[{}]}");
        Require(s.Store.TryCollectProtectedImageIds(loaded, out _),
            "an abandoned malformed conflict diff blocked healthy image cleanup");
    }

    private static void ArchiveFailureNeverCommits()
    {
        using var s = new Scope();
        var store = new StateStore(s.DirectoryPath, new DurableAtomicFileWriter((stage, path) =>
        {
            if (stage == DurableAtomicWriteStage.AfterFlush && path.EndsWith(".diff.json", StringComparison.Ordinal))
                throw new IOException("injected archive flush failure");
        }));
        var memory = store.Load(); memory.Papers[0].Content = "local recovery value";
        var disk = s.Read(); disk["papers"]![0]!["content"] = "external recovery value"; s.Write(disk);
        var bytes = File.ReadAllBytes(store.FilePath);
        var preview = store.PreviewExternalMerge(memory, 2, s.BaselineJson);
        Throws<IOException>(() => store.CommitExternalMerge(ResolvePreview(preview), preview.ObservedDiskJson!, 2));
        Require(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "main committed without durable conflict record");
        store.SaveJsonSync(store.SerializeState(memory), 3);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "local recovery value",
            "failed explicit archive blocked an unrelated ordinary save");
    }

    private static void PostCommitRetentionCancellationCannotRejectMerge()
    {
        using var scope = new Scope();
        var lifetime = new PersistenceWriteLifetime();
        var writer = new DurableAtomicFileWriter((stage, target) =>
        {
            // Simulate system termination exactly AFTER the main atomic replace,
            // but BEFORE the optional conflict archive retention scan.
            if (stage == DurableAtomicWriteStage.AfterReplace &&
                string.Equals(target, scope.Store.FilePath, StringComparison.Ordinal))
                lifetime.Stop();
        });
        var store = new StateStore(scope.DirectoryPath, writer) { WriteLifetime = lifetime };
        var memory = store.Load();
        memory.Papers[0].Content = "committed local selection";
        var external = scope.Read();
        external["papers"]![0]!["content"] = "rejected incoming selection";
        scope.Write(external);
        var preview = store.PreviewExternalMerge(memory, 2, scope.BaselineJson);
        Require(preview.State != null && preview.Conflicts is { Count: 1 },
            "postcommit termination fixture never reached a real merge conflict");
        var files = store.CommitExternalMerge(
            ResolvePreview(preview, ConflictChoice.KeepMemory), preview.ObservedDiskJson!, 2);
        Require(Str(scope.Read()["papers"]![0]!["content"]) == "committed local selection" &&
                files.ConflictFile != null && files.ConflictDiffFile != null,
            "shutdown cancellation after commit caused a false merge failure or data rollback");
        Console.WriteLine("PASS committed merge survives shutdown during optional archive cleanup");
    }

    private static void PluginExitFailureRetainsDirtyData()
    {
        using var s = new Scope();
        var fail = false;
        var writer = new DurableAtomicFileWriter((stage, _) =>
        {
            if (fail && stage == DurableAtomicWriteStage.BeforeReplace)
                throw new IOException("plugin disk failure");
        });
        using var store = new PaperBodyPluginDataStore(Path.Combine(s.DirectoryPath, "plugins"), writer, 60_000, 60_000);
        store.SavePaperState("fixture", "paper", 1, "{\"value\":1}");
        store.FlushForUserExit();
        var path = Path.Combine(store.DataRoot, "fixture.json");
        var before = File.ReadAllBytes(path);
        store.SavePaperState("fixture", "paper", 1, "{\"value\":2}");
        fail = true;
        Throws<IOException>(store.FlushForUserExit);
        Require(File.ReadAllBytes(path).SequenceEqual(before) &&
            store.ReadPaperState("fixture", "paper").Json.Contains(":2"),
            "plugin failed exit lost disk generation or the dirty live value");
        fail = false;
        store.FlushForUserExit();
        Require(!File.ReadAllBytes(path).SequenceEqual(before), "plugin retry did not save the retained value");
    }

    private static void RecoveredBackupDoesNotOverwriteRepairedPrimary()
    {
        using var scope = new Scope();
        Require(scope.Store.TryRefreshBackupFromPrimary(), "fixed backup fixture failed");
        var backup = File.ReadAllBytes(scope.Store.BackupPath);
        File.WriteAllText(scope.Store.FilePath, "{corrupt before recovery");
        var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var state = recovered.Load();
        state.Papers[0].Content = "recovered memory";
        scope.Memory.Papers[0].Content = "external repaired";
        var repairedBytes = Encoding.UTF8.GetBytes(scope.Store.SerializeState(scope.Memory));
        File.WriteAllBytes(scope.Store.FilePath, repairedBytes);
        Throws<IOException>(() => recovered.SaveJsonSync(recovered.SerializeState(state), 2));
        Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(repairedBytes) &&
                File.ReadAllBytes(scope.Store.BackupPath).SequenceEqual(backup),
            "an external repair was overwritten by a stale startup recovery");
        var retry = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        Require(retry.Load().Papers[0].Content == "external repaired",
            "a failed recovery could not reopen the newer healthy primary");

        File.WriteAllText(scope.Store.FilePath, "{corrupt before second recovery");
        var fresh = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
        var valid = fresh.Load();
        fresh.SaveJsonSync(fresh.SerializeState(valid), 2);
        Require(fresh.Load().Papers[0].Content == "original" &&
                Directory.GetFiles(scope.DirectoryPath, "data.failed_load.*.json").Length == 0 &&
                Directory.GetFiles(scope.DirectoryPath, "data.backup.used_for_recovery.*.json").Length == 0,
            "backup recovery did not persist or created abandoned duplicate copies");
        Console.WriteLine("PASS fixed backup recovery rejects external repairs and creates no extra copies");
    }

    private static void CorruptLoadsRejectBlankReplacement()
    {
        foreach (var invalid in new[] { "{}", "{\"papers\":null}", "{\"papers\":[null]}", "not-json" })
        {
            using var scope = new Scope();
            File.WriteAllText(scope.Store.FilePath, invalid);
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidDataException>(() => reader.Load());
            Require(File.ReadAllText(reader.FilePath) == invalid &&
                    !Directory.GetFiles(scope.DirectoryPath, "data.failed_load.*.json").Any(),
                "unrecoverable corruption generated blank state or duplicate recovery files");
        }

        using (var scope = new Scope())
        {
            Require(scope.Store.TryRefreshBackupFromPrimary(), "missing-primary test needs backup");
            var original = File.ReadAllBytes(scope.Store.BackupPath);
            File.Delete(scope.Store.FilePath);
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var restored = reader.Load(out var empty);
            Require(!empty && reader.NeedsStartupRecoverySave &&
                    restored.Papers.Count == scope.Memory.Papers.Count,
                "valid fixed backup was not loaded automatically");
            reader.SaveJsonSync(reader.SerializeState(restored), 2);
            Require(File.Exists(reader.FilePath) &&
                    File.ReadAllBytes(reader.BackupPath).SequenceEqual(original),
                "restoring a missing primary destroyed the fixed backup");
        }

        using (var scope = new Scope())
        {
            File.WriteAllText(scope.Store.FilePath, "{broken-primary");
            File.WriteAllText(scope.Store.BackupPath, "{broken-backup");
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidDataException>(() => reader.Load());
            Require(File.ReadAllText(reader.FilePath) == "{broken-primary" &&
                    File.ReadAllText(reader.BackupPath) == "{broken-backup",
                "two corrupt sources were silently replaced with empty data");
        }

        using (var scope = new Scope())
        {
            File.Delete(scope.Store.FilePath);
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var blank = reader.Load(out var empty);
            Require(empty && blank.Papers.Count == 0, "genuinely new workspace cannot initialize");
        }

        using (var scope = new Scope())
        {
            Require(scope.Store.TryRefreshBackupFromPrimary(), "busy-file fixture needs a valid backup");
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            using (var busy = new FileStream(scope.Store.FilePath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
                Throws<IOException>(() => reader.Load());
            }
            Require(File.ReadAllBytes(scope.Store.FilePath).SequenceEqual(bytes),
                "a busy primary was treated as corrupt and restored over");
        }

        using (var scope = new Scope())
        {
            Require(scope.Store.TryRefreshBackupFromPrimary(), "busy backup fixture needs a valid backup");
            File.WriteAllText(scope.Store.FilePath, "{broken-primary");
            using (var busy = new FileStream(scope.Store.BackupPath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
                Throws<IOException>(() => reader.Load());
            }
            Require(File.ReadAllText(scope.Store.FilePath) == "{broken-primary",
                "an inaccessible backup incorrectly authorized a blank replacement");
        }

        using var directoryPath = new Scope();
        File.Delete(directoryPath.Store.FilePath);
        Directory.CreateDirectory(directoryPath.Store.FilePath);
        Throws<InvalidDataException>(() => directoryPath.Store.Load());
    }
}
