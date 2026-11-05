using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void OrphanedTempImageAndWriteChecks()
    {
        // A lost first commit may leave the only image reference in an orphaned temp.
        // The archival filename must be included in GC for the matching workspace.
        foreach (var number in new[] { 1, 2 })
        foreach (var backupTemp in new[] { false, true })
        {
            using var scope = new Scope();
            var writer = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var imagePath = Path.Combine(scope.DirectoryPath,
                number == 1 ? "note-assets.lmdb" : "note-assets2.lmdb");
            using var images = new NoteImageStore(imagePath);
            images.Load();
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            var id = images.ImportBitmapSource("note-a", bitmap).Id;
            var stateWithImage = NewState();
            stateWithImage.Papers[0].Content = MarkdownImageReferences.CreateReference(id);
            var tempPath = (backupTemp ? writer.BackupPath : writer.FilePath) + ".tmp";
            var bytes = System.Text.Encoding.UTF8.GetBytes(writer.SerializeState(stateWithImage));
            File.WriteAllBytes(tempPath, bytes);
            if (File.Exists(writer.FilePath)) File.Delete(writer.FilePath);
            if (File.Exists(writer.BackupPath)) File.Delete(writer.BackupPath);

            var recoveredStore = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var blank = recoveredStore.Load(out var initializedEmpty);
            Require(initializedEmpty && blank.Papers.Count == 0, "orphan fixture did not initialize empty");
            recoveredStore.SaveJsonSync(recoveredStore.SerializeState(blank), 2);
            var archive = Directory.GetFiles(scope.DirectoryPath,
                Path.GetFileNameWithoutExtension(tempPath) + ".orphaned.*.tmp");
            Require(archive.Length == 1 && File.ReadAllBytes(archive[0]).SequenceEqual(bytes),
                "the initial orphan was not preserved byte-for-byte");
            Require(recoveredStore.TryCollectProtectedImageIds(blank, out var protectedIds) &&
                    protectedIds.Contains(id),
                "orphaned temporary JSON did not protect its actual image reference");
            images.CollectUnprotectedImages(protectedIds);
            Require(images.TryGetEncodedImageBytes(id, out _, out _),
                "actual LMDB GC destroyed an image referenced only by an orphan");

            File.WriteAllText(archive[0], "{unreadable");
            Require(!recoveredStore.TryCollectProtectedImageIds(blank, out _),
                "malformed orphaned recovery evidence did not block destructive GC");
        }

        // A locked *primary* temp must abort the save, not allow FileMode.Create
        // to truncate the unique surviving source.
        using (var scope = new Scope())
        {
            var bytes = File.ReadAllBytes(scope.Store.FilePath);
            File.Delete(scope.Store.FilePath);
            var path = scope.Store.FilePath + ".tmp";
            File.WriteAllBytes(path, bytes);
            var writer = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var blank = writer.Load(out var empty);
            Require(empty, "primary orphan fixture not initialized");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = false;
                try { writer.SaveJsonSync(writer.SerializeState(blank), 2); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Require(failed && !File.Exists(writer.FilePath),
                    "failed primary orphan preservation still wrote a new main file");
            }
            Require(File.ReadAllBytes(path).SequenceEqual(bytes),
                "failed preservation truncated the unique primary temp");
            writer.SaveJsonSync(writer.SerializeState(blank), 3);
            Require(Directory.GetFiles(scope.DirectoryPath,
                        Path.GetFileNameWithoutExtension(path) + ".orphaned.*.tmp")
                    .Any(file => File.ReadAllBytes(file).SequenceEqual(bytes)),
                "retry did not move the original orphan safely");
        }

        // A busy historical backup temp must not block the main save. The backup
        // refresh must preserve that same file before reusing its temp name.
        using (var scope = new Scope())
        {
            File.Delete(scope.Store.FilePath);
            var backupTemp = scope.Store.BackupPath + ".tmp";
            const string original = "{interrupted backup bytes";
            File.WriteAllText(backupTemp, original);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            var blank = store.Load(out var empty);
            Require(empty, "backup temp fixture did not initialize empty");
            using (var busy = new FileStream(backupTemp,
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                store.SaveJsonSync(store.SerializeState(blank), 2);
                Require(File.Exists(store.FilePath) && !store.TryRefreshBackupFromPrimary(),
                    "locked backup temp either blocked the main save or was overwritten during refresh");
            }
            Require(File.ReadAllText(backupTemp) == original,
                "failed backup refresh damaged the historical backup temp");
            Require(store.TryRefreshBackupFromPrimary() && !File.Exists(backupTemp) &&
                    Directory.GetFiles(scope.DirectoryPath,
                        Path.GetFileNameWithoutExtension(backupTemp) + ".orphaned.*.tmp")
                    .Any(file => File.ReadAllText(file) == original),
                "backup refresh reused its temp name without preserving the original");
        }
        Console.WriteLine("PASS orphan images, protected one-copy move, failed move and backup temp refresh");
    }
}
