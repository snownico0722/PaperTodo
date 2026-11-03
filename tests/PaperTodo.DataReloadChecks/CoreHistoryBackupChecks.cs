using System.IO;
using System.IO.Compression;
using PaperTodo;

internal static partial class Program
{
    private static void CoreHistoryBackupChecks()
    {
        using var scope = new Scope();
        var backup = new CoreHistoryBackup(scope.DirectoryPath);
        var start = new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

        // Numbered documents exist independently of the currently enabled workspace count.
        foreach (var number in new[] { 2, 5 })
        {
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var state = NewState();
            state.Papers[0].Content = number == 2 ? "second workspace" : "dormant workspace";
            store.SaveJsonSync(store.SerializeState(state), 1);
        }
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "note-assets.lmdb"), "not backed up");
        var pluginData = Path.Combine(scope.DirectoryPath, "plugins", "data");
        Directory.CreateDirectory(pluginData);
        File.WriteAllText(Path.Combine(pluginData, "plugin.json"), "{}");
        File.WriteAllText(scope.Store.BackupPath, File.ReadAllText(scope.Store.FilePath));

        Require(backup.TryCreateIfDue(start), "initial saved core documents were not archived");
        var files = Directory.GetFiles(backup.BackupDirectory, "PaperTodo-core-*.zip");
        Require(files.Length == 1, "initial backup did not create exactly one archive");
        var first = files.Single();
        using (var zip = ZipFile.OpenRead(first))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Require(names.SequenceEqual(new[] { "data.json", "data2.json", "data5.json" }),
                "backup included image/plugin/backup files or missed a dormant workspace");
            using var reader = new StreamReader(zip.GetEntry("data5.json")!.Open());
            Require(reader.ReadToEnd().Contains("dormant workspace", StringComparison.Ordinal),
                "archived numbered workspace did not preserve original paper content");
        }

        Require(!backup.TryCreateIfDue(start.AddDays(6)), "backup ran before seven days elapsed");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").Length == 1,
            "an early check created duplicate backups");
        for (var week = 1; week <= 3; week++)
            Require(backup.TryCreateIfDue(start.AddDays(week * 7)), "weekly archive was skipped");

        var previous = Directory.GetFiles(backup.BackupDirectory, "*.zip").OrderBy(x => x).ToArray();
        Require(previous.Length == 4, "four weekly history archives were not retained");

        // Invalid existing data cannot silently produce an incomplete replacement history.
        var invalid = Path.Combine(scope.DirectoryPath, "data3.json");
        File.WriteAllText(invalid, "{");
        Require(!backup.TryCreateIfDue(start.AddDays(28)), "corrupt workspace entered the history");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").OrderBy(x => x).SequenceEqual(previous),
            "failed backup pruned an existing valid history");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.tmp").Length == 0,
            "failed backup left a temporary archive");

        File.Delete(invalid);
        Require(backup.TryCreateIfDue(start.AddDays(28)), "valid retry after failure was skipped");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").Length == 4 &&
                !File.Exists(first), "successful fifth archive did not remove only the oldest");
        Console.WriteLine("PASS core history: weekly due dates, numbered JSON, excludes, validation and 4-copy rotation");
    }
}
