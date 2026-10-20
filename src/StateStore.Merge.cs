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
    /// The caller supplies the actual original JSON read before editing; saves never infer it.
    /// No disk write occurs here: the UI must resolve any conflicts before commit.
    /// </summary>
    internal StateReloadCommit PreviewExternalMerge(AppState memory, long version, string? baselineJson = null)
    {
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                return new(null, new() { Outcome = "busy" });
            if (!File.Exists(FilePath))
                return new(null, new() { Outcome = "invalid", Error = "data.json is missing." });

            var observed = File.ReadAllText(FilePath);
            var document = JsonNode.Parse(observed, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("data.json must contain an object.");
            ValidateExternalDocument(document);
            var imported = JsonSerializer.Deserialize<AppState>(observed, JsonOptions)
                ?? throw new JsonException("data.json cannot be null.");
            NormalizeAfterLoad(imported);
            var external = JsonSerializer.SerializeToNode(imported, JsonOptions)!.AsObject();
            var current = JsonSerializer.SerializeToNode(memory, JsonOptions)!.AsObject();
            JsonObject? baseline = null;
            if (baselineJson != null)
            {
                var originalDocument = JsonNode.Parse(baselineJson, documentOptions: StateJsonReadPolicy.DocumentOptions)
                    as JsonObject ?? throw new JsonException("Merge baseline must contain an object.");
                ValidateExternalDocument(originalDocument);
                var original = JsonSerializer.Deserialize<AppState>(baselineJson, JsonOptions)
                    ?? throw new JsonException("Merge baseline cannot be null.");
                NormalizeAfterLoad(original);
                baseline = JsonSerializer.SerializeToNode(original, JsonOptions)!.AsObject();
            }
            if (JsonNode.DeepEquals(current, external))
            {
                // Even a no-op request must fence older queued snapshots.
                _latestWrittenVersion = version;
                return new(null, new() { Outcome = "unchanged" });
            }

            StateMergeResult merge;
            if (baseline == null)
            {
                // The last save is not necessarily what the external editor read. Ask explicitly.
                merge = new((JsonObject)current.DeepClone(), [],
                    [new StateMergeConflict("$", false, null, true, current.DeepClone(),
                        true, external.DeepClone(), "unknown_baseline")]);
            }
            else
            {
                merge = StateJsonMerge.Merge(baseline, current, external);
            }

            var candidate = JsonSerializer.Deserialize<AppState>(
                merge.State.ToJsonString(JsonOptions), JsonOptions)
                ?? throw new JsonException("Merged data cannot be null.");
            // Candidate settings may still be disputed. Normalizing dependent fields here
            // can irreversibly truncate a title before the user chooses a longer title limit.
            // Apply cross-field invariants once, after all choices, in CommitExternalMerge.

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
        StateMergeResolution resolution, string observedDiskJson, long version)
    {
        // Normalize against the selected settings, not the provisional conflict defaults.
        // The resolution owns a detached candidate; live objects are still untouched.
        NormalizeAfterLoad(resolution.State);
        var json = SerializeState(resolution.State);
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
            if (resolution.Conflicts.Count > 0)
                (conflictFile, diffFile) = ArchiveConflicts(resolution);
            WriteJsonInternal(json, beforeReplace: ValidateSource);
            _latestWrittenVersion = version;
            return (conflictFile, diffFile);
        }
        finally { _writeLock.Release(); }
    }

    private (string ConflictFile, string ConflictDiffFile) ArchiveConflicts(
        StateMergeResolution resolution)
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                     Guid.NewGuid().ToString("N")[..8];
        var mainName = "dataconflict-" + suffix + ".json";
        var diffName = "dataconflict-" + suffix + ".diff.json";
        var directory = Path.GetDirectoryName(FilePath)!;
        // Save exact external source bytes before a successful merge can replace it.
        // Copy the exact source file bytes, including any BOM and formatting.
        File.Copy(FilePath, Path.Combine(directory, mainName));
        // Keep the existing flat archive schema, but record the decisions actually used
        // to produce the accepted state. A preview conflict has no implied resolution.
        var conflicts = resolution.Conflicts.Select(decision => new
        {
            decision.Conflict.Path,
            decision.Conflict.BaselineExists, decision.Conflict.Baseline,
            decision.Conflict.PaperTodoExists, decision.Conflict.PaperTodo,
            decision.Conflict.ExternalExists, decision.Conflict.External,
            decision.Conflict.Reason,
            Resolution = decision.Choice == ConflictChoice.KeepExternal ? "external" : "paperTodo",
            ResolutionSource = decision.Source
        });
        File.WriteAllText(Path.Combine(directory, diffName),
            JsonSerializer.Serialize(new { policy = resolution.Policy, conflicts }, JsonOptions), new UTF8Encoding(false));
        return (mainName, diffName);
    }
}
