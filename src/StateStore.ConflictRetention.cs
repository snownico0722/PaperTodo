using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed partial class StateStore
{
    private sealed class ConflictArchiveGroup(string key, DateTime createdUtc)
    {
        internal string Key { get; } = key;
        internal DateTime CreatedUtc { get; } = createdUtc;
        internal string? OriginalPath { get; set; }
        internal string? DiffPath { get; set; }
        internal bool IsComplete => OriginalPath != null && DiffPath != null;
    }

    // An archive is ONE completed (original,diff) pair. Incomplete fragments
    // must never force eviction of a complete, still-recoverable pair.
    internal void TryPruneConflictArchives()
    {
        try
        {
            _writeLock.Wait();
            try { PruneConflictArchivesCore(); }
            finally { _writeLock.Release(); }
        }
        catch (OperationCanceledException)
        {
            // Shutdown has already stopped maintenance.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Conflict archive retention failed: {0}", ex);
        }
    }

    // Caller may already hold the StateStore write lock (explicit merge).
    private void PruneConflictArchivesCore()
    {
        WriteLifetime.ThrowIfStopped();
        var directory = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(directory)) return;

        var groups = new Dictionary<string, ConflictArchiveGroup>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, ConflictFilePrefix + "*"))
        {
            if (!TryParseConflictArchiveName(Path.GetFileName(path),
                    out var key, out var created, out var isDiff))
                continue;
            if (!groups.TryGetValue(key, out var group))
                groups.Add(key, group = new ConflictArchiveGroup(key, created));
            if (isDiff) group.DiffPath = path;
            else group.OriginalPath = path;
        }

        // A pair of filenames with damaged contents is not a complete recovery
        // record. Do not allow such a pair to evict five genuinely usable ones.
        var paired = groups.Values.Where(group => group.IsComplete).ToArray();
        if (paired.Length <= 5)
            return; // No deletions possible: do not parse archives within the retention limit.
        var complete = paired.Where(IsUsableCompleteConflictGroup)
            .OrderBy(group => group.CreatedUtc)
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var remaining = complete.Length;
        if (remaining <= 5) return;

        // Keep the latest five complete groups, regardless of their age.
        foreach (var group in complete)
        {
            if (remaining <= 5)
                break;
            try
            {
                WriteLifetime.ThrowIfStopped();
                // Remove the diff first. If the second deletion fails, the full
                // original document remains readable instead of a diff-only stub.
                File.Delete(group.DiffPath!);
                WriteLifetime.ThrowIfStopped();
                File.Delete(group.OriginalPath!);
                remaining--;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Could not retire complete conflict group {0}: {1}", group.Key, ex);
                // A partially removed pair is no longer a complete group.
                // Stop instead of evicting another valid recovery point.
                break;
            }
        }
    }

    private bool IsUsableCompleteConflictGroup(ConflictArchiveGroup group)
    {
        if (!group.IsComplete) return false;
        try
        {
            var originalJson = DecodeStateBytes(File.ReadAllBytes(group.OriginalPath!));
            var original = JsonNode.Parse(
                originalJson, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("Incomplete conflict original.");
            ValidateExternalDocument(original);
            _ = ReadCoreState(originalJson)
                ?? throw new JsonException("Conflict original is not a complete state.");
            return TryAddConflictImageReferences(
                File.ReadAllText(group.DiffPath!), new HashSet<string>(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false; // Broken record: never count it as a recoverable whole.
        }
    }

    private static bool TryAddConflictImageReferences(string json, HashSet<string> ids)
    {
        var root = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions) as JsonObject;
        if (root?["conflicts"] is not JsonArray conflicts) return false;
        foreach (var entry in conflicts)
        {
            if (entry is not JsonObject conflict) return false;
            foreach (var side in new[] { "baseline", "paperTodo", "external" })
            {
                if (conflict[side + "Exists"] is not JsonValue existsValue ||
                    !existsValue.TryGetValue<bool>(out var exists)) return false;
                if (!exists) continue;
                // A JSON null is valid, but an absent value with Exists=true is incomplete.
                if (!conflict.ContainsKey(side)) return false;
                AddStrings(conflict[side], ids);
            }
        }
        return true;

        static void AddStrings(JsonNode? node, HashSet<string> target)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var pair in obj) AddStrings(pair.Value, target);
                    break;
                case JsonArray array:
                    foreach (var item in array) AddStrings(item, target);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    target.UnionWith(MarkdownImageReferences.CollectImageIds(text));
                    break;
            }
        }
    }

    private bool TryParseConflictArchiveName(
        string filename, out string key, out DateTime createdUtc, out bool isDiff)
    {
        key = "";
        createdUtc = default;
        isDiff = false;
        if (!filename.StartsWith(ConflictFilePrefix, StringComparison.Ordinal))
            return false;

        var remainder = filename[ConflictFilePrefix.Length..];
        // .tmp files are uncommitted writes, not finished conflict records.
        string candidate;
        if (remainder.EndsWith(".diff.json", StringComparison.Ordinal))
        {
            candidate = remainder[..^".diff.json".Length];
            isDiff = true;
        }
        else if (remainder.EndsWith(".json", StringComparison.Ordinal))
        {
            candidate = remainder[..^".json".Length];
        }
        else return false;

        // Current archive suffix: 17 UTC digits, hyphen, eight hexadecimal chars.
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
}
