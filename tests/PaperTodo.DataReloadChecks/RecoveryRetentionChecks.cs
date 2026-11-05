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
