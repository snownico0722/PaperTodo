using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void OrphanedTempImageAndWriteChecks()
    {
        // Actual LMDB image bytes survive a complete interrupted-write recovery.
        foreach (var number in new[] { 1, 2 })
        foreach (var backupTemp in new[] { false, true })
        {
            using var scope = new Scope();
            var writer = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            using var images = new NoteImageStore(Path.Combine(scope.DirectoryPath,
                number == 1 ? "note-assets.lmdb" : "note-assets2.lmdb"));
            images.Load();
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            var id = images.ImportBitmapSource("note-a", bitmap).Id;
            var state = NewState();
            state.Papers[0].Content = MarkdownImageReferences.CreateReference(id);
            var bytes = Encoding.UTF8.GetBytes(writer.SerializeState(state));
            var tempPath = (backupTemp ? writer.BackupPath : writer.FilePath) + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            if (File.Exists(writer.FilePath)) File.Delete(writer.FilePath);
            if (File.Exists(writer.BackupPath)) File.Delete(writer.BackupPath);

            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var restored = store.Load(out var empty);
            Require(!empty && restored.Papers[0].Content.Contains("i:" + id) &&
                    store.NeedsStartupRecoverySave && File.ReadAllBytes(tempPath).SequenceEqual(bytes),
                "valid temp image note was not restored without changing its source");
            Require(!store.TryCollectProtectedImageIds(restored, out _),
                "startup GC ran before the temporary recovery was committed");
            store.SaveJsonSync(store.SerializeState(restored), 2);
            Require(!File.Exists(tempPath) && File.Exists(store.FilePath) &&
                    !Directory.GetFiles(scope.DirectoryPath,
                        Path.GetFileNameWithoutExtension(tempPath) + ".orphaned.*.tmp").Any(),
                "successfully recovered temp remained as an unnecessary orphan");
            Require(Str(J(File.ReadAllText(store.FilePath))["papers"]![0]!["content"]) == state.Papers[0].Content,
                "committed primary lost the recovered image note");
            Require(store.TryCollectProtectedImageIds(restored, out var protectedIds) &&
                    protectedIds.Contains(id), "recovered primary did not protect its image");
            images.CollectUnprotectedImages(protectedIds);
            Require(images.TryGetEncodedImageBytes(id, out _, out _),
                "real LMDB cleanup deleted an automatically restored image");
        }

        // A healthy fixed backup is the full restoration authority, but a
        // different complete primary temp stays intact until recovery commits.
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var seeded = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            seeded.SaveJsonSync(seeded.SerializeState(NewState()), 1);
            Require(seeded.TryRefreshBackupFromPrimary(), "fixed backup fixture failed");
            var alternative = seeded.Load();
            alternative.Papers[0].Content =
                "different complete temp\n" + MarkdownImageReferences.CreateReference("987");
            var originalTempBytes = Encoding.UTF8.GetBytes(seeded.SerializeState(alternative));
            File.WriteAllText(seeded.FilePath, "{corrupt committed document");
            File.WriteAllBytes(seeded.FilePath + ".tmp", originalTempBytes);

            var rejectCommit = true;
            var atomic = new DurableAtomicFileWriter((stage, target) =>
            {
                if (rejectCommit && stage == DurableAtomicWriteStage.BeforeReplace &&
                    string.Equals(target, seeded.FilePath, StringComparison.Ordinal))
                    throw new IOException("Injected failure before recovery replacement.");
            });
            var store = new StateStore(scope.DirectoryPath, atomic, number);
            var restored = store.Load(out var empty);
            Require(!empty && restored.Papers[0].Content == "original",
                "the fixed backup did not take precedence over an uncommitted temp");
            Throws<IOException>(() => store.SaveJsonSync(store.SerializeState(restored), 2));
            var preserved = Directory.GetFiles(scope.DirectoryPath,
                Path.GetFileNameWithoutExtension(seeded.FilePath + ".tmp") +
                ".orphaned.*.tmp");
            Require(preserved.Length == 1 &&
                    File.ReadAllBytes(preserved[0]).SequenceEqual(originalTempBytes) &&
                    File.ReadAllText(seeded.FilePath) == "{corrupt committed document",
                "failed recovery deleted the complete displaced temporary source");
            rejectCommit = false;
            store.SaveJsonSync(store.SerializeState(restored), 3);
            Require(preserved.All(path => !File.Exists(path)) &&
                    !File.Exists(seeded.FilePath + ".tmp"),
                "healthy recovered primary did not retire displaced temporary sources");
            Require(store.TryCollectProtectedImageIds(restored, out var images) &&
                    !images.Contains("987"),
                "retired orphan still protected images absent from the recovered primary and backup");
        }

        // A successful ordinary save can retire the complete temp it displaced.
        using (var scope = new Scope())
        {
            var alternative = scope.Store.Load();
            alternative.Papers[0].Content = "complete but uncommitted local edit";
            var bytes = Encoding.UTF8.GetBytes(scope.Store.SerializeState(alternative));
            File.WriteAllBytes(scope.Store.FilePath + ".tmp", bytes);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var healthy = store.Load();
            Require(healthy.Papers[0].Content == "original",
                "an uncommitted temporary snapshot replaced a healthy primary");
            store.SaveJsonSync(store.SerializeState(healthy), 2);
            Require(!Directory.GetFiles(scope.DirectoryPath, "data.json.orphaned.*.tmp").Any() &&
                    store.Load().Papers[0].Content == "original",
                "ordinary save retained an orphan after committing a healthy primary");
        }

        // A backup refresh also owns its own fixed .tmp filename. It must not
        // discard a source before preservation; the healthy primary permits cleanup afterwards.
        using (var scope = new Scope())
        {
            var alternative = scope.Store.Load();
            alternative.Papers[0].Content = "older complete interrupted backup";
            var bytes = Encoding.UTF8.GetBytes(scope.Store.SerializeState(alternative));
            File.WriteAllBytes(scope.Store.BackupPath + ".tmp", bytes);

            var changed = scope.Store.Load();
            changed.Papers[0].Content = "latest committed primary";
            scope.Store.SaveJsonSync(scope.Store.SerializeState(changed), 2);
            Require(scope.Store.TryRefreshBackupFromPrimary(),
                "periodic fixed backup refresh did not complete");
            Require(File.Exists(scope.Store.BackupPath) &&
                    !File.Exists(scope.Store.BackupPath + ".tmp") &&
                    !Directory.GetFiles(scope.DirectoryPath,
                        "data.backup.json.orphaned.*.tmp").Any(),
                "backup refresh retained a displaced orphan beside a healthy primary");
            Require(Str(scope.Read()["papers"]![0]!["content"]) == "latest committed primary",
                "backup refresh changed the committed primary");
        }

        // Access failures after Load are not permission to write over the sole
        // complete recovery source. Once the lock releases, retry works in-place.
        foreach (var backupTemp in new[] { false, true })
        {
            using var scope = new Scope();
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            var source = (backupTemp ? scope.Store.BackupPath : scope.Store.FilePath) + ".tmp";
            File.WriteAllBytes(source, bytes);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var restored = store.Load();
            using (var busy = new FileStream(source, FileMode.Open,
                       FileAccess.ReadWrite, FileShare.None))
            {
                var failed = false;
                try { store.SaveJsonSync(store.SerializeState(restored), 2); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Require(failed && !File.Exists(store.FilePath),
                    "a locked unique recovery source was overwritten");
            }
            Require(File.ReadAllBytes(source).SequenceEqual(bytes),
                "a refused recovery truncated its sole original source");
            store.SaveJsonSync(store.SerializeState(restored), 3);
            Require(File.Exists(store.FilePath) && !File.Exists(source) &&
                    store.Load().Papers[0].Content == "original",
                "recovery retry failed after the source became readable");
        }

        // A malformed historical temp is not a recoverable document. It must
        // not permanently block image collection for a healthy primary.
        using (var scope = new Scope())
        {
            File.WriteAllText(scope.Store.FilePath + ".tmp", "{incomplete");
            File.WriteAllText(Path.Combine(scope.DirectoryPath,
                "data.json.orphaned.20260101-000000-000.tmp"), "{broken orphan");
            var state = scope.Store.Load();
            Require(scope.Store.TryCollectProtectedImageIds(state, out _),
                "abandoned malformed temps disabled GC for a healthy primary");
        }

        // Even after recovery from the only valid temp, a corrupt fixed backup
        // is abandoned; it does not permanently veto image cleanup.
        using (var scope = new Scope())
        {
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            File.WriteAllText(scope.Store.BackupPath, "{broken backup");
            File.WriteAllBytes(scope.Store.FilePath + ".tmp", bytes);
            var recovered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var state = recovered.Load(out var empty);
            Require(!empty, "valid temp could not replace two unusable committed files");
            recovered.SaveJsonSync(recovered.SerializeState(state), 2);
            Require(recovered.TryCollectProtectedImageIds(state, out _),
                "abandoned broken fixed backup blocked GC after successful temp recovery");
        }
        Console.WriteLine("PASS actual images, complete temp recovery, failure/retry and damaged-source retirement");
    }

    private static void OrphanCleanupRequiresHealthyPrimary()
    {
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            store.SaveJsonSync(store.SerializeState(NewState()), 1);
            Require(store.TryRefreshBackupFromPrimary(), "cleanup backup fixture failed");
            var original = File.ReadAllBytes(store.FilePath);
            var alternate = NewState();
            alternate.Papers[0].Content = MarkdownImageReferences.CreateReference("987");
            var orphan = store.FilePath + ".orphaned.20260101-000000-000.tmp";
            var broken = store.BackupPath + ".orphaned.20261105-000000-000.tmp";
            File.WriteAllText(orphan, store.SerializeState(alternate));
            File.WriteAllText(broken, "{broken orphan");
            var other = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number == 1 ? 2 : 1);
            var unrelated = other.FilePath + ".orphaned.20260101-000000-000.tmp";
            File.WriteAllBytes(unrelated, original);
            File.WriteAllBytes(store.FilePath + ".tmp", original);
            File.WriteAllBytes(store.BackupPath + ".tmp", original);

            var state = store.Load();
            Require(!File.Exists(orphan) && !File.Exists(broken) && File.Exists(unrelated) &&
                    File.ReadAllBytes(store.FilePath).SequenceEqual(original) &&
                    File.ReadAllBytes(store.BackupPath).SequenceEqual(original) &&
                    File.ReadAllBytes(store.FilePath + ".tmp").SequenceEqual(original) &&
                    File.ReadAllBytes(store.BackupPath + ".tmp").SequenceEqual(original),
                "healthy startup did not clean only its workspace's orphaned temporary files");
            Require(store.TryCollectProtectedImageIds(state, out var ids) && !ids.Contains("987"),
                "cleaned orphan still held an otherwise unreferenced image");
        }

        foreach (var primaryKind in new[] { "missing", "corrupt", "locked" })
        {
            using var scope = new Scope();
            Require(scope.Store.TryRefreshBackupFromPrimary(), "unhealthy-primary backup fixture failed");
            var original = File.ReadAllBytes(scope.Store.FilePath);
            var orphan = scope.Store.FilePath + ".orphaned.20260101-000000-000.tmp";
            var broken = scope.Store.BackupPath + ".orphaned.20260101-000000-000.tmp";
            File.WriteAllBytes(orphan, original);
            File.WriteAllText(broken, "{broken orphan");
            FileStream? held = null;
            try
            {
                if (primaryKind == "missing") File.Delete(scope.Store.FilePath);
                else if (primaryKind == "corrupt") File.WriteAllText(scope.Store.FilePath, "{}");
                else held = new FileStream(scope.Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Require(!scope.Store.TryRefreshBackupFromPrimary() &&
                        File.ReadAllBytes(orphan).SequenceEqual(original) && File.Exists(broken),
                    "a healthy backup permitted cleanup without a readable healthy primary");
                var reader = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
                if (primaryKind == "locked") Throws<IOException>(() => reader.Load());
                else
                {
                    var restored = reader.Load();
                    Require(File.Exists(orphan) && File.Exists(broken),
                        "loading a backup retired orphans before recovery committed");
                    reader.SaveJsonSync(reader.SerializeState(restored), 2);
                    Require(!File.Exists(orphan) && !File.Exists(broken),
                        "verified recovery did not enable orphan cleanup");
                }
            }
            finally { held?.Dispose(); }
        }

        using (var scope = new Scope())
        {
            var orphan = scope.Store.FilePath + ".orphaned.20260101-000000-000.tmp";
            File.WriteAllBytes(orphan, File.ReadAllBytes(scope.Store.FilePath));
            using (var held = new FileStream(orphan, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var state = scope.Store.Load();
                state.Papers[0].Content = "save succeeds despite locked cleanup";
                scope.Store.SaveJsonSync(scope.Store.SerializeState(state), 2);
                Require(scope.Store.TryRefreshBackupFromPrimary() && File.Exists(orphan),
                    "cleanup failure rejected a healthy load, save or backup refresh");
            }
            Require(scope.Store.TryRefreshBackupFromPrimary() && !File.Exists(orphan),
                "existing backup maintenance did not retry cleanup after the lock ended");
        }
        Console.WriteLine("PASS orphan cleanup requires a healthy primary; recovery, isolation and retry remain intact");
    }
}
