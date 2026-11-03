using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void StorageSafetyChecks()
    {
        ExternalEditsDoNotBlockOrdinarySaves();
        ExplicitMergeRejectsChangedSource();
        OrdinarySavesDoNotInspectExternalChanges();
        TerminationRejectsQueuedWrites();
        ConflictImagesRemainRecoverable();
        ArchiveFailureNeverCommits();
        BrokenLoadsNeverBecomeBlankSaves();
        OrphanedTempsNeverBecomeBlankSaves();
        RecoveredLoadPreservesSourcesBeforeSaving();
        PluginExitFailureRetainsDirtyData();
        Console.WriteLine("PASS write safety: legacy ordinary saves, explicit transaction validation, shutdown, recovery images and archive failures");
    }

    private static void OrphanedTempsNeverBecomeBlankSaves()
    {
        foreach (var backup in new[] { false, true })
        {
            using var s = new Scope();
            var bytes = File.ReadAllBytes(s.Store.FilePath);
            File.Delete(s.Store.FilePath);
            var path = (backup ? s.Store.BackupPath : s.Store.FilePath) + ".tmp";
            File.WriteAllBytes(path, bytes);
            var store = new StateStore(s.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidDataException>(() => store.Load());
            Require(!File.Exists(store.FilePath) && File.ReadAllBytes(path).SequenceEqual(bytes),
                "an orphaned first-write temp was replaced by an empty document");
        }
        Console.WriteLine("PASS incomplete first-write evidence is retained, never promoted or replaced by a blank state");
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
        Require(!s.Store.TryCollectProtectedImageIds(loaded, out _), "incomplete conflict archive permitted destructive GC");
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

    private static void RecoveredLoadPreservesSourcesBeforeSaving()
    {
        using var s = new Scope();
        Require(s.Store.TryRefreshBackupFromPrimary(), "recovery seed backup");
        var backup = File.ReadAllBytes(s.Store.BackupPath);
        File.WriteAllText(s.Store.FilePath, "{broken before load");
        var recovered = new StateStore(s.DirectoryPath, DurableAtomicFileWriter.Shared);
        var state = recovered.Load();
        s.Memory.Papers[0].Content = "external repaired the primary";
        File.WriteAllText(s.Store.FilePath, s.Store.SerializeState(s.Memory));
        var repaired = File.ReadAllBytes(s.Store.FilePath);
        state.Papers[0].Content = "recovered local memory";
        recovered.SaveJsonSync(recovered.SerializeState(state), 2);
        Require(Str(s.Read()["papers"]![0]!["content"]) == "recovered local memory" &&
                Directory.GetFiles(s.DirectoryPath, "data.failed_load.*.json").Any(p =>
                    File.ReadAllBytes(p).SequenceEqual(repaired)) &&
                Directory.GetFiles(s.DirectoryPath, "data.backup.used_for_recovery.*.json").Any(p =>
                    File.ReadAllBytes(p).SequenceEqual(backup)),
            "recovered startup state did not preserve its original data sources before saving");
        recovered.StopWritesForTermination();
        Throws<OperationCanceledException>(() => recovered.SaveJsonSync(recovered.SerializeState(state), 3));
    }

    private static void BrokenLoadsNeverBecomeBlankSaves()
    {
        foreach (var invalid in new[] { "{}", "{\"papers\":null}", "{\"papers\":[null]}", "not-json" })
        {
            using var s = new Scope();
            File.WriteAllText(s.Store.FilePath, invalid);
            var reader = new StateStore(s.DirectoryPath, DurableAtomicFileWriter.Shared);
            Throws<InvalidOperationException>(() => reader.Load());
            Require(File.ReadAllText(reader.FilePath) == invalid, "failed load created an empty replacement");
        }
        using var directoryPath = new Scope();
        File.Delete(directoryPath.Store.FilePath);
        Directory.CreateDirectory(directoryPath.Store.FilePath);
        Throws<InvalidDataException>(() => directoryPath.Store.Load());
    }
}
