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
        CorruptLoadsRequireExplicitRebuild();
        InterruptedWriteSelectionChecks();
        OrphanedTempImageAndWriteChecks();
        OrphanCleanupRequiresHealthyPrimary();
        RecoveredBackupDoesNotOverwriteRepairedPrimary();
        RecoveryReadbackFailureCanRetry();
        RecoveryReloadSupersedesPendingSource();
        PluginExitFailureRetainsDirtyData();
        Console.WriteLine("PASS write safety: legacy ordinary saves, explicit transaction validation, shutdown, recovery images and archive failures");
    }

    private static void InterruptedWriteSelectionChecks()
    {
        using (var scope = new Scope())
        {
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            var orphan = Path.Combine(scope.DirectoryPath, "data.json.orphaned.20261001-000000-000.tmp");
            File.WriteAllBytes(orphan, bytes);
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var state = recovered.Load(out var empty);
            Require(!empty && state.Papers.Count == 2 && File.ReadAllBytes(orphan).SequenceEqual(bytes),
                "valid legacy orphan was not retained until recovery committed");
            recovered.SaveJsonSync(recovered.SerializeState(state), 2);
            Require(!File.Exists(orphan), "consumed orphan was left behind");
        }

        // Selection follows the snapshot modification time, including moved
        // orphans, rather than filenames or the order directory entries arrive.
        foreach (var newestKind in new[] { "primary", "backup", "orphan" })
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var paths = new[] { store.FilePath + ".tmp", store.BackupPath + ".tmp",
                store.FilePath + ".orphaned.20990101-000000-000.tmp" };
            var kinds = new[] { "primary", "backup", "orphan" };
            File.WriteAllText(store.FilePath, "{broken primary");
            var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < paths.Length; index++)
            {
                var alternate = NewState();
                alternate.Papers[0].Content = kinds[index];
                File.WriteAllText(paths[index], store.SerializeState(alternate));
                File.SetLastWriteTimeUtc(paths[index], old.AddHours(kinds[index] == newestKind ? 2 : 1));
            }
            var broken = store.BackupPath + ".orphaned.20990102-000000-000.tmp";
            File.WriteAllText(broken, "{}");
            File.SetLastWriteTimeUtc(broken, old.AddHours(3));
            var restored = store.Load(out var empty);
            Require(!empty && restored.Papers[0].Content == newestKind && paths.All(File.Exists),
                "newest complete temporary snapshot was not selected without modifying sources");
            store.SaveJsonSync(store.SerializeState(restored), 2);
            Require(!File.Exists(paths[2]) && !File.Exists(broken),
                "latest snapshot did not commit before orphan cleanup");
            Require(store.Load().Papers[0].Content == newestKind,
                "latest temporary snapshot was not persisted");
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

        // Different unknown fields do not veto selection. Equal timestamps use
        // deterministic path order, without inventing another recovery prompt.
        using (var scope = new Scope())
        {
            var first = scope.Read();
            var second = scope.Read();
            first["futureExtension"] = "revision-one";
            second["futureExtension"] = "revision-two";
            second["papers"]![0]!["content"] = "tie winner";
            File.Delete(scope.Store.FilePath);
            File.WriteAllText(scope.Store.FilePath + ".tmp", first.ToJsonString());
            File.WriteAllText(scope.Store.BackupPath + ".tmp", second.ToJsonString());
            var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(scope.Store.FilePath + ".tmp", stamp);
            File.SetLastWriteTimeUtc(scope.Store.BackupPath + ".tmp", stamp);
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Require(recovered.Load().Papers[0].Content == "tie winner" && !File.Exists(recovered.FilePath),
                "timestamp tie was inconsistent or different unknown fields blocked selection");
        }
        Console.WriteLine("PASS newest complete interrupted writes, invalid candidates and timestamp ties");
    }

    private static void ExternalEditsDoNotBlockOrdinarySaves()
    {
        using var s = new Scope();
        var external = s.Read();
        external["papers"]![0]!["content"] = "external version";
        external["theme"] = "dark";
        s.Write(external);
        Require(s.Memory.Theme != "dark", "disk edits mutated the live model");
        var local = s.Store.SerializeState(s.Memory);
        s.Store.SaveJsonSync(local, 2);
        var saved = s.Read();
        Require(Str(saved["papers"]![0]!["content"]) == "original" &&
                Str(saved["theme"]) == s.Memory.Theme,
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
        Require(preview.State != null, "merge preview did not start");
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

    private static void CorruptLoadsRequireExplicitRebuild()
    {
        foreach (var invalid in new[] { "{}", "{\"papers\":null}", "{\"papers\":[null]}", "not-json" })
        {
            using var scope = new Scope();
            File.WriteAllText(scope.Store.FilePath, invalid);
            var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<StateRecoveryException>(() => reader.Load());
            var blank = reader.Load(out var empty, timestamp =>
            {
                Require(timestamp == null, "corrupt sources incorrectly offered history");
                return WorkspaceRecoveryChoice.CreateEmpty;
            });
            Require(empty && blank.Papers.Count == 0 && reader.NeedsStartupRecoverySave &&
                    File.ReadAllText(reader.FilePath) == invalid &&
                    !Directory.GetFiles(scope.DirectoryPath, "data.failed_load.*.json").Any(),
                "unrecoverable corruption did not stage blank data without premature writes");
            reader.SaveJsonSync(reader.SerializeState(blank), 2);
            Require(reader.Load().Papers.Count == 0, "blank replacement did not commit");
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
            Throws<StateRecoveryException>(() => reader.Load());
            var blank = reader.Load(out var empty, timestamp =>
            {
                Require(timestamp == null, "corrupt sources incorrectly offered history");
                return WorkspaceRecoveryChoice.CreateEmpty;
            });
            Require(empty && blank.Papers.Count == 0 &&
                    File.ReadAllText(reader.FilePath) == "{broken-primary" &&
                    File.ReadAllText(reader.BackupPath) == "{broken-backup",
                "blank rebuild changed old files before the normal writer committed");
            File.WriteAllText(reader.FilePath, scope.BaselineJson);
            Throws<IOException>(() => reader.SaveJsonSync(reader.SerializeState(blank), 2));
            Require(File.ReadAllText(reader.FilePath) == scope.BaselineJson,
                "explicit rebuild overwrote an intervening primary repair");
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
