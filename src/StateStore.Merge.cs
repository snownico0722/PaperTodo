using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>An external editor changed the source after the explicit merge request began.</summary>
internal sealed class ExternalDocumentChangedException : InvalidOperationException
{
    public ExternalDocumentChangedException() :
        base("data.json changed during conflict review; no merged changes were written. Please retry.") { }
}

public sealed partial class StateStore
{
    /// <summary>
    /// An *opt-in*, explicit 3-way merge preview. The usual ReloadExternalState remains
    /// disk-authoritative. No watcher, revision field or on-disk history is installed.
    /// Baseline is just the last successfully loaded/saved JSON string, held in memory.
    /// No disk write occurs here: the UI must resolve any conflicts before commit.
    /// </summary>
    internal StateReloadCommit PreviewExternalMerge(AppState memory, long version)
    {
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                return new(null, new() { Outcome = "busy" });
            if (!File.Exists(FilePath))
                return new(null, new() { Outcome = "invalid", Error = "data.json is missing." });

            var observed = File.ReadAllText(FilePath);
            if (_mergeBaselineJson != null && string.Equals(observed, _mergeBaselineJson, StringComparison.Ordinal))
                return new(null, new() { Outcome = "unchanged" });

            var document = JsonNode.Parse(observed, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("data.json must contain an object.");
            ValidateExternalDocument(document);
            var imported = JsonSerializer.Deserialize<AppState>(observed, JsonOptions)
                ?? throw new JsonException("data.json cannot be null.");
            NormalizeAfterLoad(imported);
            var external = JsonSerializer.SerializeToNode(imported, JsonOptions)!.AsObject();
            var current = JsonSerializer.SerializeToNode(memory, JsonOptions)!.AsObject();
            StateMergeResult merge;
            if (_mergeBaselineJson == null)
            {
                // No proven baseline (e.g. startup backup recovery). Never silently infer one.
                merge = new((JsonObject)current.DeepClone(), [],
                    [new StateMergeConflict("$", false, null, true, current.DeepClone(),
                        true, external.DeepClone(), "unknown_baseline")]);
            }
            else
            {
                var saved = JsonSerializer.Deserialize<AppState>(_mergeBaselineJson, JsonOptions)
                    ?? throw new JsonException("Saved merge baseline is invalid.");
                NormalizeAfterLoad(saved);
                var baseline = JsonSerializer.SerializeToNode(saved, JsonOptions)!.AsObject();
                merge = StateJsonMerge.Merge(baseline, current, external);
            }

            var candidate = JsonSerializer.Deserialize<AppState>(
                merge.State.ToJsonString(JsonOptions), JsonOptions)
                ?? throw new JsonException("Merged data cannot be null.");
            NormalizeAfterLoad(candidate);

            // Fence only older queued saves; no primary-file write until conflicts are resolved.
            _latestWrittenVersion = version;
            return new(candidate, new DataReloadResult
            {
                Outcome = "pending", Applied = false,
                AppliedExternalChanges = merge.AppliedPaths.Count,
                ConflictCount = merge.Conflicts.Count,
                RestartRequired = !string.Equals(memory.UiLanguage, candidate.UiLanguage, StringComparison.Ordinal)
            }, merge.Conflicts, (JsonObject)external.DeepClone(), observed);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new(null, new() { Outcome = "invalid", Error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(null, new() { Outcome = "failed", Error = ex.Message });
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Commit the user's merged snapshot only if the external input has not changed.
    /// This is the normal atomic writer with an opt-in pre-replace check on each retry.
    /// The caller must not install the merged in-memory objects before this succeeds.
    /// </summary>
    internal (string? ConflictFile, string? ConflictDiffFile) CommitExternalMerge(
        AppState merged, string observedDiskJson, long version,
        IReadOnlyList<StateMergeConflict>? conflicts)
    {
        // Serializable snapshot is prepared outside the shared write lock.
        var json = SerializeState(merged);
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                throw new ExternalDocumentChangedException();
            void ValidateSource()
            {
                if (!File.Exists(FilePath) ||
                    !string.Equals(File.ReadAllText(FilePath), observedDiskJson, StringComparison.Ordinal))
                    throw new ExternalDocumentChangedException();
            }

            ValidateSource();
            string? conflictFile = null;
            string? diffFile = null;
            if (conflicts is { Count: > 0 })
                (conflictFile, diffFile) = ArchiveConflicts(observedDiskJson, conflicts);
            WriteJsonInternal(json, beforeReplace: ValidateSource);
            _latestWrittenVersion = version;
            _mergeBaselineJson = json;
            return (conflictFile, diffFile);
        }
        finally { _writeLock.Release(); }
    }

    private (string ConflictFile, string ConflictDiffFile) ArchiveConflicts(
        string source, IReadOnlyList<StateMergeConflict> conflicts)
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                     Guid.NewGuid().ToString("N")[..8];
        var mainName = "dataconflict-" + suffix + ".json";
        var diffName = "dataconflict-" + suffix + ".diff.json";
        var directory = Path.GetDirectoryName(FilePath)!;
        // Save exact external source bytes before a successful merge can replace it.
        File.WriteAllText(Path.Combine(directory, mainName), source, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, diffName),
            JsonSerializer.Serialize(new { conflicts }, JsonOptions), new UTF8Encoding(false));
        return (mainName, diffName);
    }
}
