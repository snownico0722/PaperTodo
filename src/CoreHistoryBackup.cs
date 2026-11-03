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
    private static readonly TimeSpan[] OlderRestorePoints =
    [
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(3),
        TimeSpan.FromHours(6)
    ];

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
                    documents.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
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

    private static HashSet<string> ChooseRetainedArchives(
        List<(string Path, DateTime Timestamp)> archives, DateTime now)
    {
        var available = archives.ToList(); // already newest first
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (available.Count == 0) return keep;

        keep.Add(available[0].Path); // the newest snapshot is always retained
        available.RemoveAt(0);
        foreach (var target in OlderRestorePoints)
        {
            if (available.Count == 0 || keep.Count == CopiesToKeep) break;
            // Oldest target first, so a newly generated recent copy cannot crowd
            // out all longer-term history while the user is editing frequently.
            var selected = available.MinBy(a => Math.Abs(((now - a.Timestamp) - target).Ticks));
            keep.Add(selected.Path);
            available.Remove(selected);
        }
        return keep;
    }

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
