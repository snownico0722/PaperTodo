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
        // A 90-day sequence of ten-minute edits must not lose month-scale history
        // halfway through rotation (the previous selector dropped 56d to 4d).
        var historyStart = start.UtcDateTime;
        var history = new List<(string Path, DateTime Timestamp)>();
        for (var step = 0; step <= 90 * 24 * 6; step++)
        {
            var instant = historyStart.AddMinutes(step * 10);
            history.Add((step.ToString(CultureInfo.InvariantCulture), instant));
            var retained = CoreHistoryBackup.ChooseRetainedArchives(
                history.OrderByDescending(x => x.Timestamp).ToList(), instant);
            history.RemoveAll(x => !retained.Contains(x.Path));
            Require(history.Count is > 0 and <= 4,
                "continuous editing exceeded four core history backups");
            if (step >= 30 * 24 * 6)
                Require(instant - history.Min(x => x.Timestamp) >= TimeSpan.FromDays(21),
                    "month-scale backup collapsed during continuous editing");
        }

        // A long-lived backup read handle must not block an atomic normal save.
        using (var concurrent = new Scope())
        using (var openRead = CoreHistoryBackup.OpenSourceSnapshot(concurrent.Store.FilePath))
        {
            var changed = NewState();
            changed.Papers[0].Content = "saved while backup is reading";
            concurrent.Store.SaveJsonSync(concurrent.Store.SerializeState(changed), 2);
            Require(concurrent.Store.Load().Papers[0].Content == "saved while backup is reading",
                "backup read blocked an atomic primary save");
        }

        // Clock correction: a future-dated archive may be retained as history
        // but must not suppress a newly timestamped snapshot indefinitely.
        using (var corrected = new Scope())
        {
            var clockBackup = new CoreHistoryBackup(corrected.DirectoryPath);
            Require(clockBackup.TryCreateIfDue(start.AddYears(1)), "future-date fixture failed");
            File.WriteAllText(corrected.Store.FilePath, "after clock correction");
            Require(clockBackup.TryCreateIfDue(start.AddMinutes(10)),
                "future-dated archive blocked the corrected clock");
            var correctedZip = Directory.GetFiles(clockBackup.BackupDirectory, "*.zip")
                .Single(p => Path.GetFileName(p).Contains("20261103-181000", StringComparison.Ordinal));
            using var zip = ZipFile.OpenRead(correctedZip);
            using var reader = new StreamReader(zip.GetEntry("data.json")!.Open());
            Require(reader.ReadToEnd() == "after clock correction", "corrected-clock backup is not newest");
        }

        // A corrupt old ZIP must not displace a readable historical version.
        using (var oldArchives = new Scope())
        {
            var archiveBackup = new CoreHistoryBackup(oldArchives.DirectoryPath);
            for (var index = 0; index < 4; index++)
            {
                File.WriteAllText(oldArchives.Store.FilePath, "version-" + index);
                Require(archiveBackup.TryCreateIfDue(start.AddMinutes(index * 10)),
                    "could not seed four history ZIPs");
            }
            var toCorrupt = Directory.GetFiles(archiveBackup.BackupDirectory, "*.zip")
                .OrderBy(x => x, StringComparer.Ordinal).First();
            File.WriteAllText(toCorrupt, "not a ZIP");
            File.WriteAllText(oldArchives.Store.FilePath, "fifth version");
            Require(archiveBackup.TryCreateIfDue(start.AddMinutes(40)),
                "damaged older ZIP blocked normal archival");
            var survivors = Directory.GetFiles(archiveBackup.BackupDirectory, "*.zip");
            Require(survivors.Length == 4 && !survivors.Contains(toCorrupt),
                "corrupt history consumed a healthy recovery slot");

            // Retry cleanup of extra archives even if no fresh content is saved.
            var extra = Path.Combine(archiveBackup.BackupDirectory,
                "PaperTodo-core-" + start.AddMinutes(15).UtcDateTime
                    .ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".zip");
            File.Copy(survivors[0], extra);
            Require(!archiveBackup.TryCreateIfDue(start.AddMinutes(50)),
                "unchanged state should not create a new archive");
            Require(Directory.GetFiles(archiveBackup.BackupDirectory, "*.zip").Length == 4,
                "unchanged backup check did not retry pruning");
        }

        // A read-only abandoned temp is not allowed to prevent the next backup.
        using (var abandoned = new Scope())
        {
            var tempBackup = new CoreHistoryBackup(abandoned.DirectoryPath);
            Require(tempBackup.TryCreateIfDue(start), "temp-cleanup fixture could not start");
            var locked = Path.Combine(tempBackup.BackupDirectory, ".PaperTodo-core-readonly.tmp");
            File.WriteAllText(locked, "old temporary data");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            try
            {
                File.WriteAllText(abandoned.Store.FilePath, "after temp cleanup failure");
                Require(tempBackup.TryCreateIfDue(start.AddMinutes(10)),
                    "an inaccessible temporary ZIP blocked a valid backup");
            }
            finally
            {
                if (File.Exists(locked)) File.SetAttributes(locked, FileAttributes.Normal);
            }
        }

        // Losing the write permission between finalization and publication
        // cannot delete previously completed recovery points.
        using (var interrupted = new Scope())
        {
            var baseline = new CoreHistoryBackup(interrupted.DirectoryPath);
            Require(baseline.TryCreateIfDue(start), "publication fixture could not start");
            File.WriteAllText(interrupted.Store.FilePath, "new but not published");
            var calls = 0;
            var failing = new CoreHistoryBackup(interrupted.DirectoryPath, () =>
            {
                if (++calls == 3) throw new IOException("Injected publication failure");
            });
            Require(!failing.TryCreateIfDue(start.AddMinutes(10), out var failed) && failed,
                "publication fault injection did not report a failure");
            Require(Directory.GetFiles(baseline.BackupDirectory, "*.zip").Length == 1,
                "failed publication removed or replaced the previous archive");
        }

        Console.WriteLine("PASS core history: raw files, resilient publishing, clock correction, CRC, cleanup and gradient");
    }
}
