using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace PaperTodo;

/// <summary>
/// Four age-spaced recovery points for the raw, already-saved core JSON files.
/// This is not a full backup of images, plugin state or app binaries.
/// </summary>
internal sealed class CoreHistoryBackup
{
    private const int CopiesToKeep = 4;
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

    internal string BackupDirectory => Path.Combine(_dataDirectory, "backups");

    internal bool TryCreateIfDue(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        try
        {
            _ensureWritesAllowed?.Invoke();
            var archives = ListArchives();
            CleanAbandonedTemporaryFiles();

            if (archives.Count > 0 && utc - archives[0].Timestamp < MinimumInterval)
                return false;

            // Preserve the original bytes even if a workspace JSON cannot be parsed.
            // A missing numbered workspace is normal; it must not block the others.
            var documents = new List<(string Name, byte[] Bytes)>();
            for (var number = 1; number <= 5; number++)
            {
                var path = new WorkspacePaths(_dataDirectory, number).Data;
                if (File.Exists(path))
                {
                    using var stream = OpenSourceSnapshot(path);
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    documents.Add((Path.GetFileName(path), copy.ToArray()));
                }
            }
            if (documents.Count == 0) return false;

            // No new historical version is needed when every workspace file is unchanged.
            // This compares bytes, not JSON structure or application-level validity.
            if (archives.Count > 0 && SameDocuments(archives[0].Path, documents))
                return false;

            _ensureWritesAllowed?.Invoke();
            Directory.CreateDirectory(BackupDirectory);
            var name = ArchivePrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".zip";
            var destination = Path.Combine(BackupDirectory, name);
            var temporary = Path.Combine(BackupDirectory, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var document in documents)
                    {
                        var entry = zip.CreateEntry(document.Name, CompressionLevel.Optimal);
                        using var output = entry.Open();
                        output.Write(document.Bytes);
                    }
                }

                _ensureWritesAllowed?.Invoke();
                // Only a finalized archive becomes eligible for retention.
                File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* A failed temporary is never a restore point. */ }
            }

            var all = ListArchives();
            var retained = ChooseRetainedArchives(all, utc);
            foreach (var stale in all.Where(a => !retained.Contains(a.Path)))
            {
                try
                {
                    _ensureWritesAllowed?.Invoke();
                    File.Delete(stale.Path);
                }
                catch (Exception ex) { Trace.TraceWarning("Could not prune core history backup: {0}", ex); }
            }
            return true;
        }
        catch (Exception ex)
        {
            // Never affect ordinary saving, switching, startup or termination.
            Trace.TraceWarning("Core history backup skipped: {0}", ex);
            return false;
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
                if (!copy.GetBuffer().AsSpan(0, (int)copy.Length).SequenceEqual(document.Bytes))
                    return false;
            }
            return true;
        }
        catch { return false; } // a damaged older ZIP must not prevent a new backup
    }

    private void CleanAbandonedTemporaryFiles()
    {
        if (!Directory.Exists(BackupDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, "." + ArchivePrefix + "*.tmp"))
        {
            try { File.Delete(path); }
            catch (IOException) { /* Best-effort cleanup of interrupted ZIP writes. */ }
        }
    }

    private List<(string Path, DateTime Timestamp)> ListArchives()
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
        result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return result;
    }
}
