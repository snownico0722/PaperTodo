using System.Globalization;
using System.IO;

namespace PaperTodo;

public sealed partial class StateStore
{
    private sealed record ConflictArchiveGroup(
        string Key, DateTime CreatedUtc, List<string> Files);

    // One conflict means one *group*: original JSON + diff JSON. The product
    // policy is an intersection, not two independent upper bounds. Young records
    // are never removed just because there are more than five.
    internal void TryPruneConflictArchives(DateTime utcNow)
    {
        try
        {
            _writeLock.Wait();
            try { PruneConflictArchivesCore(utcNow); }
            finally { _writeLock.Release(); }
        }
        catch (OperationCanceledException)
        {
            // Shutdown has already stopped persistence maintenance.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Conflict archive retention failed: {0}", ex);
        }
    }

    // The merge caller already holds _writeLock.
    private void PruneConflictArchivesCore(DateTime utcNow)
    {
        WriteLifetime.ThrowIfStopped();
        var directory = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(directory)) return;
        var groups = new Dictionary<string, ConflictArchiveGroup>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, ConflictFilePrefix + "*"))
        {
            if (!TryParseConflictArchiveName(Path.GetFileName(path), out var key, out var created))
                continue;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new ConflictArchiveGroup(key, created, []);
                groups.Add(key, group);
            }
            group.Files.Add(path);
        }

        var remaining = groups.Count;
        if (remaining <= 5) return;
        foreach (var group in groups.Values
                     .OrderBy(group => group.CreatedUtc)
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            if (remaining <= 5 || utcNow - group.CreatedUtc <= TimeSpan.FromDays(3))
                break;
            try
            {
                foreach (var path in group.Files)
                {
                    WriteLifetime.ThrowIfStopped();
                    File.Delete(path);
                }
                remaining--;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Could not retire conflict group {0}: {1}", group.Key, ex);
                // A partially deleted pair is still one group until next maintenance.
            }
        }
    }

    private bool TryParseConflictArchiveName(
        string filename, out string key, out DateTime createdUtc)
    {
        key = "";
        createdUtc = default;
        if (!filename.StartsWith(ConflictFilePrefix, StringComparison.Ordinal))
            return false;
        var remainder = filename[ConflictFilePrefix.Length..];
        foreach (var extension in new[] { ".diff.json.tmp", ".diff.json", ".json.tmp", ".json" })
        {
            if (!remainder.EndsWith(extension, StringComparison.Ordinal)) continue;
            var candidate = remainder[..^extension.Length];
            // Current archive suffix: 17 UTC digits, hyphen, 8 hex characters.
            if (candidate.Length != 26 || candidate[17] != '-' ||
                !candidate.AsSpan(18).ToString().All(Uri.IsHexDigit))
                return false;
            if (!DateTime.TryParseExact(candidate[..17], "yyyyMMddHHmmssfff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out createdUtc))
                return false;
            key = candidate;
            return true;
        }
        return false;
    }
}
