using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>An external editor changed the source after the explicit merge request began.</summary>
internal sealed class ExternalDocumentChangedException : InvalidOperationException
{
    internal ExternalDocumentChangedException() : base("The data file changed during the explicit operation; retry with a fresh input.") { }
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
        memory = ProjectForPersistence(memory);
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                return new(null, new() { Outcome = "busy" });
            if (string.IsNullOrWhiteSpace(baselineJson))
                return new(null, new() { Outcome = "invalid", Error = "merge_data requires baseline_json containing the original data.json." });
            if (!File.Exists(FilePath))
                return new(null, new() { Outcome = "invalid", Error = "data.json is missing." });

            var sourceBytes = File.ReadAllBytes(FilePath);
            var observed = DecodeStateBytes(sourceBytes);
            var document = JsonNode.Parse(observed, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("data.json must contain an object.");
            ValidateExternalDocument(document);
            var imported = JsonSerializer.Deserialize<AppState>(observed, JsonOptions)
                ?? throw new JsonException("data.json cannot be null.");
            MigrateLegacyTopBarSettings(imported, observed);
            var externalTitles = CaptureMergeTitles(imported);
            // Normalize a detached copy for semantic comparison and for all fields whose
            // normalization is non-destructive. Keep original titles for the actual merge:
            // a disputed MaxTitleLength must not truncate text before resolution.
            // Link validity depends on the final set of papers, not the incoming side
            // alone. Preserve both sides' links until the user resolves delete/edit.
            var normalizedImported = CopyForReload(imported);
            NormalizeWorkspaceDocument(normalizedImported, deferLinkNormalization: true);
            var external = JsonSerializer.SerializeToNode(normalizedImported, JsonOptions)!.AsObject();
            var current = JsonSerializer.SerializeToNode(memory, JsonOptions)!.AsObject();
            var originalDocument = JsonNode.Parse(baselineJson, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("Merge baseline must contain an object.");
            ValidateExternalDocument(originalDocument);
            var original = JsonSerializer.Deserialize<AppState>(baselineJson, JsonOptions)
                ?? throw new JsonException("Merge baseline cannot be null.");
            MigrateLegacyTopBarSettings(original, baselineJson);
            var baselineRawTitles = CaptureMergeTitles(original);
            var normalizedOriginal = CopyForReload(original);
            // Compare the baseline under the same effective workspace settings as
            // the external snapshot, not dormant independent preferences.
            NormalizeWorkspaceDocument(normalizedOriginal, deferLinkNormalization: true);
            var baselineNormalizedTitles = CaptureMergeTitles(normalizedOriginal);
            var baseline = JsonSerializer.SerializeToNode(normalizedOriginal, JsonOptions)!.AsObject();
            if (JsonNode.DeepEquals(current, external))
            {
                // Even a no-op request must fence older queued snapshots.
                ValidatePrimaryHash(System.Security.Cryptography.SHA256.HashData(sourceBytes));
                BeginExternalRead(version);
                return new(null, new() { Outcome = "unchanged" });
            }

            RestoreRawMergeTitles(external, externalTitles);
            RestoreRawMergeTitles(baseline, baselineRawTitles);
            RestoreUnmodifiedCurrentMergeTitles(current, baselineRawTitles, baselineNormalizedTitles);
            var merge = StateJsonMerge.Merge(baseline, current, external);

            var candidate = JsonSerializer.Deserialize<AppState>(
                merge.State.ToJsonString(JsonOptions), JsonOptions)
                ?? throw new JsonException("Merged data cannot be null.");
            // Candidate settings may still be disputed. Normalizing dependent fields here
            // can irreversibly truncate a title before the user chooses a longer title limit.
            // Apply cross-field invariants once, after all choices, in CommitExternalMerge.

            // Only a valid preview may fence older queued saves. No disk write occurs
            // until conflicts are resolved; a failed commit must reschedule lost autosaves.
            BeginExternalRead(version);
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
        catch (ExternalDocumentChangedException ex)
        {
            return new(null, new() { Outcome = "busy", Error = ex.Message });
        }
        catch (OperationCanceledException ex)
        {
            return new(null, new() { Outcome = "failed", Error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(null, new() { Outcome = "failed", Error = ex.Message });
        }
        finally { _writeLock.Release(); }
    }

    private static Dictionary<string, string?> CaptureMergeTitles(AppState state) =>
        state.Papers.ToDictionary(paper => paper.Id, paper => (string?)paper.Title, StringComparer.Ordinal);

    private static void RestoreRawMergeTitles(
        JsonObject document,
        IReadOnlyDictionary<string, string?> titles)
    {
        if (document["papers"] is not JsonArray papers) return;
        foreach (var node in papers.OfType<JsonObject>())
        {
            if (node["id"] is JsonValue idValue &&
                idValue.TryGetValue<string>(out var id) &&
                titles.TryGetValue(id, out var title))
                node["title"] = JsonValue.Create(title);
        }
    }

    private static void RestoreUnmodifiedCurrentMergeTitles(
        JsonObject current,
        IReadOnlyDictionary<string, string?> baselineRawTitles,
        IReadOnlyDictionary<string, string?> baselineNormalizedTitles)
    {
        if (current["papers"] is not JsonArray papers) return;
        foreach (var node in papers.OfType<JsonObject>())
        {
            if (node["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<string>(out var id) ||
                !baselineRawTitles.TryGetValue(id, out var rawTitle) ||
                !baselineNormalizedTitles.TryGetValue(id, out var normalizedTitle))
                continue;

            // The live model may have been shortened when the baseline was loaded. If it
            // still equals that normalized baseline value, it was not a local title edit.
            if (node["title"]?.GetValue<string>() == normalizedTitle)
                node["title"] = JsonValue.Create(rawTitle);
        }
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
        NormalizeWorkspaceDocument(resolution.State);
        var json = SerializeRawState(resolution.State);
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                throw new ExternalDocumentChangedException();
            WriteLifetime.ThrowIfStopped();
            var sourceBytes = File.ReadAllBytes(FilePath);
            ValidateExplicitSource(observedDiskJson, sourceBytes);
            var sourceHash = System.Security.Cryptography.SHA256.HashData(sourceBytes);
            void ValidateSource() => ValidatePrimaryHash(sourceHash);

            ValidateSource();
            string? conflictFile = null;
            string? diffFile = null;
            if (resolution.Conflicts.Count > 0)
                (conflictFile, diffFile) = ArchiveConflicts(resolution, sourceBytes, ValidateSource);
            WriteJsonInternal(json, version, beforeReplace: ValidateSource);
            // Retention is best-effort maintenance; a successfully committed
            // merge must never be reported as failed because deletion is blocked.
            try { PruneConflictArchivesCore(); }
            catch (Exception ex)
            {
                // All retention failures (including shutdown cancellation) occur
                // AFTER the main merge commit. Never convert that commit into a
                // reported failure or let an old in-memory state save over it.
                System.Diagnostics.Trace.TraceWarning("Conflict archive retention failed: {0}", ex);
            }
            return (conflictFile, diffFile);
        }
        finally { _writeLock.Release(); }
    }

    private (string ConflictFile, string ConflictDiffFile) ArchiveConflicts(
        StateMergeResolution resolution, byte[] sourceBytes, Action validateSource)
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" +
                     Guid.NewGuid().ToString("N")[..8];
        var mainName = ConflictFilePrefix + suffix + ".json";
        var diffName = ConflictFilePrefix + suffix + ".diff.json";
        var directory = Path.GetDirectoryName(FilePath)!;
        // Flush the existing pair of recovery records BEFORE replacing the primary.
        // Use the bytes validated for this operation, not another racing read of the path.
        // No ordinary-save copies, additional generations or separate image backups.
        _atomicWriter.Write(Path.Combine(directory, mainName), sourceBytes,
            beforeReplace: validateSource);
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
        _atomicWriter.Write(Path.Combine(directory, diffName),
            JsonSerializer.SerializeToUtf8Bytes(new { policy = resolution.Policy, conflicts }, JsonOptions),
            beforeReplace: validateSource);
        return (mainName, diffName);
    }
}
