using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace PaperTodo;

/// <summary>
/// Four age-spaced recovery points for the raw, already-saved core JSON files.
/// This is not a full backup of images, plugin state or app binaries.
/// </summary>
internal sealed class CoreHistoryBackup
{
    private const int CopiesToKeep = 4;
    private const int WorkspaceReadAttempts = 3;
    private const int WorkspaceReadRetryDelayMs = 200;
    private const string ArchivePrefix = "PaperTodo-core-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fffffff";
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongHistoryTarget = TimeSpan.FromDays(30);
    private static readonly TimeSpan LongHistoryReplacementFloor = TimeSpan.FromDays(21);
    private static readonly TimeSpan MediumHistoryTarget = TimeSpan.FromDays(3);
    private static readonly TimeSpan ShortHistoryTarget = TimeSpan.FromHours(6);

    private readonly string _dataDirectory;
    private readonly Action? _ensureWritesAllowed;

    internal CoreHistoryBackup(string dataDirectory, Action? ensureWritesAllowed = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _ensureWritesAllowed = ensureWritesAllowed;
    }

    internal string BackupDirectory => Path.Combine(_dataDirectory, "PaperTodo-backups");

    internal sealed record WorkspaceSnapshot(
        string ArchivePath, DateTime TimestampUtc, byte[] ArchiveHash, byte[] Bytes);

    // Read only the requested workspace, newest first. ZIP integrity and JSON
    // validity are separate: StateStore owns the latter. Never extract other slots.
    internal IEnumerable<WorkspaceSnapshot> ReadWorkspaceSnapshots(int workspaceNumber,
        Action<Exception>? reportReadFailure = null)
    {
        var name = Path.GetFileName(new WorkspacePaths(_dataDirectory, workspaceNumber).Data);
        foreach (var archive in ListArchives(DateTime.UtcNow))
        {
            WorkspaceSnapshot snapshot;
            try
            {
                var archiveBytes = File.ReadAllBytes(archive.Path);
                using var stream = new MemoryStream(archiveBytes, writable: false);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entries = zip.Entries.Where(entry => entry.FullName == name).ToArray();
                if (entries.Length != 1) continue;
                var entry = entries[0];
                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                var bytes = output.ToArray();
                if (bytes.LongLength != entry.Length ||
                    ~UpdateCrc32(uint.MaxValue, bytes) != entry.Crc32) continue;
                snapshot = new WorkspaceSnapshot(archive.Path, archive.Timestamp,
                    SHA256.HashData(archiveBytes), bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (ex is not (InvalidDataException or FileNotFoundException or DirectoryNotFoundException))
                    reportReadFailure?.Invoke(ex);
                Trace.TraceWarning("Skipping unavailable core history recovery candidate: {0}", ex);
                continue;
            }
            yield return snapshot;
        }
    }

    internal bool TryCreateIfDue(DateTimeOffset now) => TryCreateIfDue(now, out _);

    // A no-op (unchanged or not yet due) is different from a failed attempt.
    // The caller uses this distinction for one bounded failure retry.
    internal bool TryCreateIfDue(DateTimeOffset now, out bool failed)
    {
        failed = false;
        var utc = now.UtcDateTime;
        try
        {
            _ensureWritesAllowed?.Invoke();
            var archives = ListArchives(utc);
            CleanAbandonedTemporaryFiles();
            // Retry pruning left over from a prior successful publication even if
            // no new content is due. Never make cleanup a prerequisite for saving.
            if (archives.Count > CopiesToKeep)
            {
                PruneArchives(archives, utc);
                archives = ListArchives(utc);
            }

            // A clock correction must not let a future-dated ZIP suppress backups.
            if (archives.Count > 0 && archives[0].Timestamp <= utc &&
                utc - archives[0].Timestamp < MinimumInterval)
                return false;

            // Missing slots are normal, and malformed JSON is copied as raw bytes.
            // If an individual workspace is temporarily unreadable, retry briefly
            // and then skip it for this snapshot only. The next scheduled scan will
            // try it again: even a sole unreadable main workspace is never abandoned.
            var documents = new List<(string Name, byte[] Bytes)>();
            var anyUnreadable = false;
            for (var number = 1; number <= 5; number++)
            {
                var path = new WorkspacePaths(_dataDirectory, number).Data;
                if (TryReadWorkspaceBytes(path, out var bytes, out var unreadable))
                    documents.Add((Path.GetFileName(path), bytes));
                anyUnreadable |= unreadable;
            }
            if (documents.Count == 0)
            {
                failed = anyUnreadable;
                return false;
            }

            // No new historical version is needed when every workspace file is unchanged.
            // This compares bytes, not JSON structure or application-level validity.
            if (archives.Count > 0 && archives[0].Timestamp <= utc &&
                SameDocuments(archives[0].Path, documents))
                return false;

            _ensureWritesAllowed?.Invoke();
            Directory.CreateDirectory(BackupDirectory);
            var name = ArchivePrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".zip";
            var destination = Path.Combine(BackupDirectory, name);
            var temporary = Path.Combine(BackupDirectory, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var document in documents)
                        {
                            var entry = zip.CreateEntry(document.Name, CompressionLevel.Optimal);
                            using var output = entry.Open();
                            output.Write(document.Bytes);
                        }
                    }
                    // Flush after the ZIP central directory has been finalized.
                    stream.Flush(flushToDisk: true);
                }

                // Only publish a ZIP that reproduces the exact source bytes.
                // No JSON parsing or semantic validation is performed.
                if (!SameDocuments(temporary, documents))
                    throw new InvalidDataException("Core history ZIP verification failed.");
                _ensureWritesAllowed?.Invoke();
                // Only a finalized archive becomes eligible for retention.
                File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Could not clean core history temporary ZIP: {0}", ex);
                }
            }

            PruneArchives(ListArchives(utc), utc);
            return true;
        }
        catch (Exception ex)
        {
            failed = true;
            // Never affect ordinary saving, switching, startup or termination.
            Trace.TraceWarning("Core history backup skipped: {0}", ex);
            return false;
        }
    }

    private void PruneArchives(List<(string Path, DateTime Timestamp)> archives, DateTime now)
    {
        if (archives.Count <= CopiesToKeep) return;

        // Verify the old archives only when pruning is actually needed. A damaged
        // ZIP must not use a recovery slot at the expense of a readable one.
        var readable = archives.Where(a => IsReadableArchive(a.Path)).ToList();
        var retained = ChooseRetainedArchives(readable, now);
        foreach (var stale in archives.Where(a => !retained.Contains(a.Path)))
        {
            try
            {
                _ensureWritesAllowed?.Invoke();
                File.Delete(stale.Path);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not prune core history backup: {0}", ex);
            }
        }
    }

    internal static HashSet<string> ChooseRetainedArchives(
        List<(string Path, DateTime Timestamp)> archives, DateTime now)
    {
        var available = archives.ToList(); // newest first
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (available.Count == 0) return keep;

        keep.Add(available[0].Path); // newest always wins
        available.RemoveAt(0);

        if (available.Count >= 2)
        {
            var oldest = available[^1];
            var successor = available[^2];
            var oldAge = now - oldest.Timestamp;
            var successorAge = now - successor.Timestamp;

            // A month-scale history must not be replaced by a three-day-old one.
            // Let its next-oldest successor age to at least three weeks before
            // retirement. The middle restore points can shift temporarily.
            if (oldAge >= LongHistoryTarget &&
                (successorAge < LongHistoryReplacementFloor ||
                 Distance(oldAge, LongHistoryTarget) <=
                 Distance(successorAge, LongHistoryTarget)))
            {
                keep.Add(oldest.Path);
                keep.Add(successor.Path);
                available.RemoveRange(available.Count - 2, 2);
                if (available.Count > 0)
                    keep.Add(SelectClosest(available, now, MediumHistoryTarget).Path);
                return keep;
            }
        }

        foreach (var target in new[]
                 { LongHistoryTarget, MediumHistoryTarget, ShortHistoryTarget })
        {
            if (available.Count == 0 || keep.Count == CopiesToKeep) break;
            keep.Add(SelectClosest(available, now, target).Path);
        }
        return keep;
    }

    private static long Distance(TimeSpan age, TimeSpan target) =>
        Math.Abs((age - target).Ticks);

    private static (string Path, DateTime Timestamp) SelectClosest(
        List<(string Path, DateTime Timestamp)> available, DateTime now, TimeSpan target)
    {
        var candidate = available.MinBy(a => Distance(now - a.Timestamp, target));
        available.Remove(candidate);
        return candidate;
    }

    private static bool TryReadWorkspaceBytes(string path, out byte[] bytes, out bool unreadable)
    {
        bytes = [];
        unreadable = false;
        for (var attempt = 0; attempt < WorkspaceReadAttempts; attempt++)
        {
            try
            {
                // Windows File.Move(overwrite) does not reliably honor share-delete
                // on an open destination. Hold the small common I/O gate only
                // while copying source bytes, then compress/verify outside it.
                lock (DurableAtomicFileWriter.SnapshotReadReplaceGate)
                {
                    using var stream = OpenSourceSnapshot(path);
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    bytes = copy.ToArray();
                }
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false; // An unconfigured workspace does not need a backup.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == WorkspaceReadAttempts - 1)
                {
                    unreadable = true;
                    Trace.TraceWarning("Skipping unreadable workspace in this core history snapshot: {0}: {1}",
                        path, ex);
                }
                else
                {
                    Thread.Sleep(WorkspaceReadRetryDelayMs); // background task, never UI thread
                }
            }
        }
        return false;
    }

    // Allow the primary's atomic file replacement while this read handle is open.
    internal static FileStream OpenSourceSnapshot(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private static bool SameDocuments(string archivePath, List<(string Name, byte[] Bytes)> documents)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count != documents.Count) return false;
            foreach (var document in documents)
            {
                var entry = zip.GetEntry(document.Name);
                if (entry == null || entry.Length != document.Bytes.LongLength) return false;
                using var input = entry.Open();
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                var restored = copy.GetBuffer().AsSpan(0, (int)copy.Length);
                if (!restored.SequenceEqual(document.Bytes) ||
                    ~UpdateCrc32(uint.MaxValue, restored) != entry.Crc32)
                    return false;
            }
            return true;
        }
        catch { return false; } // a damaged older ZIP must not prevent a new backup
    }

    // .NET 10 doesn't verify ZIP CRC on entry reads, so check it only when
    // deciding which older recovery points can be retained.
    private static bool IsReadableArchive(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count is < 1 or > 5) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!IsCoreDocumentName(entry.FullName) || !names.Add(entry.FullName))
                    return false;
                using var input = entry.Open();
                var buffer = new byte[16 * 1024];
                long length = 0;
                uint crc = uint.MaxValue;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    length += read;
                    crc = UpdateCrc32(crc, buffer.AsSpan(0, read));
                }
                if (length != entry.Length || ~crc != entry.Crc32) return false;
            }
            return true;
        }
        catch (IOException)
        {
            // An archive temporarily locked by another program is not proven bad.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsCoreDocumentName(string name) =>
        name == "data.json" ||
        (name.Length == 10 && name.StartsWith("data", StringComparison.Ordinal) &&
         name[4] is >= '2' and <= '5' && name.EndsWith(".json", StringComparison.Ordinal));

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var n = 0; n < table.Length; n++)
        {
            uint value = (uint)n;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xEDB88320u;
            table[n] = value;
        }
        return table;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = (crc >> 8) ^ Crc32Table[(int)((crc ^ value) & 0xFF)];
        return crc;
    }

    private void CleanAbandonedTemporaryFiles()
    {
        if (!Directory.Exists(BackupDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, "." + ArchivePrefix + "*.tmp"))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not clean abandoned core history ZIP: {0}", ex);
            }
        }
    }

    private List<(string Path, DateTime Timestamp)> ListArchives(DateTime now)
    {
        var result = new List<(string Path, DateTime Timestamp)>();
        if (!Directory.Exists(BackupDirectory)) return result;

        foreach (var path in Directory.EnumerateFiles(BackupDirectory, ArchivePrefix + "*.zip"))
        {
            var filename = Path.GetFileName(path);
            if (!filename.StartsWith(ArchivePrefix, StringComparison.Ordinal) ||
                !filename.EndsWith(".zip", StringComparison.Ordinal))
                continue;

            var stamp = filename[ArchivePrefix.Length..^4];
            if (DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                result.Add((path, when));
        }
        // Put impossible future-dated archives last, without deleting them just
        // because the wall clock was corrected. The newest real backup wins.
        result.Sort((a, b) =>
        {
            var aFuture = a.Timestamp > now;
            var bFuture = b.Timestamp > now;
            if (aFuture != bFuture) return aFuture ? 1 : -1;
            return b.Timestamp.CompareTo(a.Timestamp);
        });
        return result;
    }
}
