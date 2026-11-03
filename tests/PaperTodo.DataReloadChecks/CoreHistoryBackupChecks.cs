using System.Globalization;
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

        var numbered = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, 2);
        numbered.SaveJsonSync(numbered.SerializeState(NewState()), 1);
        var damaged = Path.Combine(scope.DirectoryPath, "data5.json");
        var rawInvalidJson = "{ NOT VALID JSON";
        File.WriteAllText(damaged, rawInvalidJson);
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "note-assets.lmdb"), "images excluded");
        var pluginData = Path.Combine(scope.DirectoryPath, "plugins", "data");
        Directory.CreateDirectory(pluginData);
        File.WriteAllText(Path.Combine(pluginData, "plugin.json"), "{}");
        File.WriteAllText(scope.Store.BackupPath, File.ReadAllText(scope.Store.FilePath));

        Require(backup.TryCreateIfDue(start), "first raw-data history snapshot was not created");
        var first = Directory.GetFiles(backup.BackupDirectory, "PaperTodo-core-*.zip").Single();
        using (var zip = ZipFile.OpenRead(first))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Require(names.SequenceEqual(new[] { "data.json", "data2.json", "data5.json" }),
                "backup includes non-core files or misses an inactive workspace");
            using var reader = new StreamReader(zip.GetEntry("data5.json")!.Open());
            Require(reader.ReadToEnd() == rawInvalidJson, "damaged workspace bytes were not preserved as-is");
        }

        File.WriteAllText(scope.Store.FilePath, "changed without JSON validation");
        Require(!backup.TryCreateIfDue(start.AddMinutes(9)), "10-minute minimum interval was not honored");
        Require(backup.TryCreateIfDue(start.AddMinutes(10)), "changed raw JSON was not archived at 10 minutes");
        Require(!backup.TryCreateIfDue(start.AddMinutes(20)), "identical files created redundant history");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").Length == 2,
            "duplicate or premature snapshots were created");

        // Sparse synthetic history: as versions accumulate, four different age targets
        // should win over the four newest files. No calendar buckets or fixed-slot files.
        foreach (var elapsed in new[]
        {
            TimeSpan.FromHours(6),
            TimeSpan.FromDays(3),
            TimeSpan.FromDays(27),
            TimeSpan.FromDays(30) - TimeSpan.FromHours(6),
            TimeSpan.FromDays(30)
        })
        {
            File.WriteAllText(scope.Store.FilePath, "version-" + elapsed.TotalMinutes.ToString(CultureInfo.InvariantCulture));
            Require(backup.TryCreateIfDue(start.Add(elapsed)), "changed checkpoint not archived");
            Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").Length <= 4,
                "gradient rotation exceeded four archives");
        }

        var finalFiles = Directory.GetFiles(backup.BackupDirectory, "*.zip");
        var suffixes = finalFiles.Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Require(suffixes.Length == 4, "four dynamic checkpoints were not retained");
        Require(finalFiles.Any(p => Path.GetFileName(p).Contains("20261103-180000", StringComparison.Ordinal)),
            "month-old recovery point was lost");
        Require(finalFiles.Any(p => Path.GetFileName(p).Contains("20261130-180000", StringComparison.Ordinal)),
            "three-day-old recovery point was lost");
        Require(finalFiles.Any(p => Path.GetFileName(p).Contains("20261203-120000", StringComparison.Ordinal)),
            "six-hour-old recovery point was lost");
        Require(finalFiles.Any(p => Path.GetFileName(p).Contains("20261203-180000", StringComparison.Ordinal)),
            "newest recovery point was lost");

        var rebooted = new CoreHistoryBackup(scope.DirectoryPath);
        Require(!rebooted.TryCreateIfDue(start.AddDays(30).AddMinutes(10)),
            "a restart duplicated unchanged backups");
        Require(Directory.GetFiles(backup.BackupDirectory, "*.zip").Length == 4,
            "restart changed the number of archives");
        var leftover = Path.Combine(backup.BackupDirectory, ".PaperTodo-core-interrupted.tmp");
        File.WriteAllText(leftover, "partial zip");
        Require(!backup.TryCreateIfDue(start.AddDays(30).AddMinutes(11)),
            "unchanged state unexpectedly archived after a temporary-file cleanup");
        Require(!File.Exists(leftover), "abandoned temporary ZIP was not cleaned");

        // Missing main data must not invalidate or prevent archiving a remaining workspace.
        File.Delete(scope.Store.FilePath);
        File.WriteAllText(damaged, rawInvalidJson + " changed");
        Require(backup.TryCreateIfDue(start.AddDays(30).AddMinutes(20)),
            "missing main file blocked remaining raw workspace documents");
        var newest = Directory.GetFiles(backup.BackupDirectory, "*.zip")
            .OrderByDescending(x => x, StringComparer.Ordinal).First();
        using (var zip = ZipFile.OpenRead(newest))
            Require(zip.GetEntry("data.json") == null && zip.GetEntry("data5.json") != null,
                "raw backup attempted recovery from a missing main data file");
        Console.WriteLine("PASS core history: raw files, 10-minute cadence, 4-way gradient, duplicate skip and restart");
    }
}
