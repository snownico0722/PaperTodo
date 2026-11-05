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
                    store.NeedsStartupRecoverySave, "valid temp image note was not restored");
            Require(!store.TryCollectProtectedImageIds(restored, out _),
                "startup GC ran before the temporary recovery was committed");
            store.SaveJsonSync(store.SerializeState(restored), 2);
            Require(!File.Exists(tempPath) && File.Exists(store.FilePath) &&
                    !Directory.GetFiles(scope.DirectoryPath,
                        Path.GetFileNameWithoutExtension(tempPath) + ".orphaned.*.tmp").Any(),
                "successfully recovered temp remained as an unnecessary orphan");
            Require(store.TryCollectProtectedImageIds(restored, out var protectedIds) &&
                    protectedIds.Contains(id), "recovered primary did not protect its image");
            images.CollectUnprotectedImages(protectedIds);
            Require(images.TryGetEncodedImageBytes(id, out _, out _),
                "real LMDB cleanup deleted an automatically restored image");
        }

        // A healthy fixed backup is the full restoration authority, but a
        // different complete primary temp may be newer. Do not quietly truncate
        // this alternative when the recovered backup is persisted.
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var seeded = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            seeded.SaveJsonSync(seeded.SerializeState(NewState()), 1);
            Require(seeded.TryRefreshBackupFromPrimary(), "fixed backup fixture failed");
            var alternative = seeded.Load();
            alternative.Papers[0].Content =
                "different complete temp " + MarkdownImageReferences.CreateReference("987");
            var originalTempBytes = Encoding.UTF8.GetBytes(seeded.SerializeState(alternative));
            File.WriteAllText(seeded.FilePath, "{corrupt committed document");
            File.WriteAllBytes(seeded.FilePath + ".tmp", originalTempBytes);

            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var restored = store.Load(out var empty);
            Require(!empty && restored.Papers[0].Content == "original",
                "the fixed backup did not take precedence over an uncommitted temp");
            store.SaveJsonSync(store.SerializeState(restored), 2);
            var preserved = Directory.GetFiles(scope.DirectoryPath,
                Path.GetFileNameWithoutExtension(seeded.FilePath + ".tmp") +
                ".orphaned.*.tmp");
            Require(preserved.Length == 1 &&
                    File.ReadAllBytes(preserved[0]).SequenceEqual(originalTempBytes) &&
                    !File.Exists(seeded.FilePath + ".tmp") &&
                    store.Load().Papers[0].Content == "original",
                "restoring a fixed backup destroyed a distinct, complete primary temp");
            Require(store.TryCollectProtectedImageIds(restored, out var images) &&
                    images.Contains("987"),
                "the preserved distinct full version lost its picture-reference protection");
        }

        // Even a healthy primary must not silently truncate a different valid
        // interrupted write when the ordinary save reuses data.json.tmp.
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
            Require(Directory.GetFiles(scope.DirectoryPath, "data.json.orphaned.*.tmp")
                    .Any(path => File.ReadAllBytes(path).SequenceEqual(bytes)) &&
                    store.Load().Papers[0].Content == "original",
                "ordinary save overwrote a distinct complete interrupted write");
        }

        // A backup refresh also owns its own fixed .tmp filename. It must not
        // truncate a different complete backup temp that survived a prior crash.
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
            Require(scope.Store.Load().Papers[0].Content == "latest committed primary" &&
                    File.Exists(scope.Store.BackupPath) &&
                    !File.Exists(scope.Store.BackupPath + ".tmp") &&
                    Directory.GetFiles(scope.DirectoryPath,
                        "data.backup.json.orphaned.*.tmp")
                        .Any(path => File.ReadAllBytes(path).SequenceEqual(bytes)),
                "backup refresh destroyed a distinct complete backup temp");
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
}
