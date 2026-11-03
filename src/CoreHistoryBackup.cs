using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace PaperTodo;

/// <summary>
/// Best-effort weekly history of committed core JSON documents. This is not a full
/// application backup: images, plugin state and short-term .backup.json files are excluded.
/// </summary>
internal sealed class CoreHistoryBackup
{
    private const int CopiesToKeep = 4;
    private const string ArchivePrefix = "PaperTodo-core-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fffffff";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private readonly string _dataDirectory;

    internal CoreHistoryBackup(string dataDirectory) =>
        _dataDirectory = Path.GetFullPath(dataDirectory);

    internal string BackupDirectory => Path.Combine(_dataDirectory, "backups");

    internal bool TryCreateIfDue(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        try
        {
            var existing = ListArchives();
            if (existing.Count != 0 && utc - existing[0].Timestamp < Interval)
                return false;

            // Read the saved files, not UI models or .backup.json. A dormant numbered
            // workspace still contains user notes even when its slot was disabled.
            var documents = new List<(string Name, byte[] Bytes)>();
            for (var number = 1; number <= 5; number++)
            {
                var path = new WorkspacePaths(_dataDirectory, number).Data;
                if (number > 1 && !File.Exists(path)) continue;
                if (!StateStore.TryReadValidatedStateBytes(path, out var bytes))
                {
                    Trace.TraceWarning("Core history backup skipped: invalid or unavailable {0}", path);
                    return false;
                }
                documents.Add((Path.GetFileName(path), bytes));
            }

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
                // Same-directory rename publishes only a fully finalized archive.
                File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* The failed temporary file is not part of retention. */ }
            }

            // A failure above never touches older archives. Prune only after publication.
            foreach (var stale in ListArchives().Skip(CopiesToKeep))
            {
                try { File.Delete(stale.Path); }
                catch (Exception ex) { Trace.TraceWarning("Could not prune core history backup: {0}", ex); }
            }
            return true;
        }
        catch (Exception ex)
        {
            // A history failure must not interrupt autosave, workspace switching or startup.
            Trace.TraceWarning("Core history backup failed: {0}", ex);
            return false;
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
