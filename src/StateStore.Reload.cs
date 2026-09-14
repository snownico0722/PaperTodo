using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class ExternalStatePendingException : InvalidOperationException
{
    public ExternalStatePendingException() : base("data.json has an unconsumed external change.") { }
}

internal sealed record StateReloadCommit(AppState? State, DataReloadResult Result);

public sealed partial class StateStore
{
    internal const string ReloadRevisionProperty = "$paperTodoRevision";
    private const int ReloadHistoryLimit = 16;
    private const int ReloadHistoryByteLimit = 32 * 1024 * 1024;
    private readonly LinkedList<(string Revision, JsonObject State, int Size)> _reloadHistory = new();
    private int _reloadHistoryBytes;
    private bool _reloadTracking;
    private byte[]? _reloadAcceptedBytes;
    private JsonObject? _reloadLoadedState;
    private string? _reloadRevision;

    private static string DecodeStateBytes(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void RememberLoadedReloadState(AppState state, byte[]? primaryBytes, string? sourceJson)
    {
        _reloadAcceptedBytes = primaryBytes;
        _reloadLoadedState = JsonSerializer.SerializeToNode(state, JsonOptions)!.AsObject();
        // This optional extension must not tighten the existing startup recovery protocol.
        // Strict metadata checks apply to live imports, not an otherwise valid startup file.
        _reloadRevision = null;
        if (sourceJson != null)
        {
            try { _reloadRevision = ReadRevision(ParseObject(sourceJson)); }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { }
        }
    }

    internal void EnableExternalReload(AppState state)
    {
        _writeLock.Wait();
        try
        {
            if (_reloadTracking) return;
            _reloadLoadedState ??= JsonSerializer.SerializeToNode(state, JsonOptions)!.AsObject();
            RememberReloadVersion(_reloadRevision ?? "", _reloadLoadedState);
            _reloadLoadedState = null;
            _reloadTracking = true;
        }
        finally { _writeLock.Release(); }
    }

    internal string? ReloadRevision
    {
        get
        {
            _writeLock.Wait();
            try { return _reloadRevision; }
            finally { _writeLock.Release(); }
        }
    }

    internal bool HasExternalStateChange()
    {
        _writeLock.Wait();
        try { return _reloadTracking && !DiskMatches(_reloadAcceptedBytes); }
        finally { _writeLock.Release(); }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions) as JsonObject
        ?? throw new JsonException("The state document must be an object.");

    private static string? ReadRevision(JsonObject document)
    {
        if (!document.TryGetPropertyValue(ReloadRevisionProperty, out var value)) return null;
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var revision) ||
            string.IsNullOrEmpty(revision) || revision.Length > 64)
            throw new JsonException($"{ReloadRevisionProperty} must be an unchanged revision string.");
        return revision;
    }

    private byte[]? ReadPrimaryBytes()
    {
        try { return File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private bool DiskMatches(byte[]? expected)
    {
        try
        {
            var current = ReadPrimaryBytes();
            return current == null ? expected == null : expected != null && current.AsSpan().SequenceEqual(expected);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void RequireDiskVersion(byte[]? expected)
    {
        if (!DiskMatches(expected)) throw new ExternalStatePendingException();
    }

    private void RememberReloadVersion(string revision, JsonObject state)
    {
        var size = Encoding.UTF8.GetByteCount(state.ToJsonString());
        _reloadHistory.AddLast((revision, state, size));
        _reloadHistoryBytes += size;
        while (_reloadHistory.Count > 1 &&
               (_reloadHistory.Count > ReloadHistoryLimit || _reloadHistoryBytes > ReloadHistoryByteLimit))
        {
            _reloadHistoryBytes -= _reloadHistory.First!.Value.Size;
            _reloadHistory.RemoveFirst();
        }
    }

    private void WriteTrackedJson(string json, byte[]? expected)
    {
        RequireDiskVersion(expected);
        var state = ParseObject(json);
        state.Remove(ReloadRevisionProperty);
        var revision = Guid.NewGuid().ToString("N");
        var output = (JsonObject)state.DeepClone();
        output[ReloadRevisionProperty] = revision;
        var bytes = Encoding.UTF8.GetBytes(output.ToJsonString(JsonOptions));
        _atomicWriter.Write(FilePath, bytes, beforeReplace: () => RequireDiskVersion(expected));
        _reloadAcceptedBytes = bytes;
        _reloadRevision = revision;
        RememberReloadVersion(revision, state);
    }

    /// <summary>
    /// Shares the normal writer gate and version fence. Parsing, merge and disk commit finish
    /// before the controller mutates any live objects. No Dispatcher callback runs under this gate.
    /// </summary>
    internal StateReloadCommit ReloadExternalState(AppState memory, long version)
    {
        _writeLock.Wait();
        string? conflictFile = null;
        string? diffFile = null;
        var conflictCount = 0;
        try
        {
            if (!_reloadTracking || version < _latestWrittenVersion)
                return new(null, new() { Outcome = "busy", Revision = _reloadRevision });

            var bytes = ReadPrimaryBytes();
            if (bytes == null)
                return new(null, new() { Outcome = "invalid", Error = "data.json is missing; the running state was not replaced." });
            if (_reloadAcceptedBytes != null && bytes.AsSpan().SequenceEqual(_reloadAcceptedBytes))
                return new(null, new() { Revision = _reloadRevision });

            var text = DecodeStateBytes(bytes);
            var document = ParseObject(text);
            ValidateExternalDocument(document);
            var revision = ReadRevision(document) ?? "";
            var externalState = DeserializeReloadState(text);
            var external = JsonSerializer.SerializeToNode(externalState, JsonOptions)!.AsObject();
            var current = JsonSerializer.SerializeToNode(memory, JsonOptions)!.AsObject();
            var baseline = _reloadHistory.LastOrDefault(item => item.Revision == revision).State;
            StateMergeResult merge;
            if (baseline == null)
            {
                // A file does not tell us what an editor originally read. Never guess a newer
                // base and silently turn its stale fields into intentional external edits.
                merge = new((JsonObject)current.DeepClone(), [],
                    [new("$", false, null, true, current.DeepClone(), true, external.DeepClone(), "unknown_baseline")]);
            }
            else merge = StateJsonMerge.Merge(baseline, current, external);

            var candidate = DeserializeReloadState(merge.State.ToJsonString(JsonOptions));
            conflictCount = merge.Conflicts.Count;
            if (conflictCount > 0)
                (conflictFile, diffFile) = PreserveReloadConflict(bytes, revision, merge.Conflicts);

            var preserveRecoverySources = PreserveRecoveredLoadFilesIfNeeded();
            WriteTrackedJson(SerializeState(candidate), bytes);
            _latestWrittenVersion = version; // pending autosaves captured before this reload are obsolete
            if (preserveRecoverySources) ClearRecoveredLoadPreservationState();
            return new(candidate, new()
            {
                Outcome = conflictCount > 0 ? "conflict" : "applied",
                Applied = true,
                AppliedExternalChanges = merge.AppliedPaths.Count,
                ConflictCount = conflictCount,
                ConflictFile = conflictFile,
                ConflictDiffFile = diffFile,
                Revision = _reloadRevision,
                RestartRequired = !string.Equals(memory.UiLanguage, candidate.UiLanguage, StringComparison.Ordinal)
            });
        }
        catch (ExternalStatePendingException)
        {
            return new(null, new() { Outcome = "busy", Error = "data.json changed again during reload; retry with the latest file.",
                ConflictCount = conflictCount, ConflictFile = conflictFile, ConflictDiffFile = diffFile });
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException)
        {
            return new(null, new() { Outcome = "invalid", Error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(null, new() { Outcome = "failed", Error = ex.Message,
                ConflictCount = conflictCount, ConflictFile = conflictFile, ConflictDiffFile = diffFile });
        }
        finally { _writeLock.Release(); }
    }

    internal static AppState CopyForReload(AppState state) =>
        JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)!;

    private static AppState DeserializeReloadState(string json)
    {
        var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
            ?? throw new JsonException("The state document cannot be null.");
        NormalizeAfterLoad(state);
        return state;
    }

    private static void ValidateExternalDocument(JsonObject document)
    {
        // Materializing each object also rejects duplicate property names. Do not silently
        // regenerate identities as startup migration may do: this is a live identity merge.
        ValidateProperties(document);
        if (document["papers"] is not JsonArray papers)
            throw new JsonException("Required property 'papers' must be an array.");
        ValidateIdentities(papers, "papers");
        foreach (var node in papers)
        {
            if (node!["content"] is JsonValue content && content.TryGetValue<string>(out var text) &&
                text.Length > PaperWindow.NoteTextMaxLength)
                throw new JsonException("Note content exceeds the editor length limit.");
            if (node["items"] is JsonArray items) ValidateIdentities(items, "items");
            else if (node!.AsObject().ContainsKey("items")) throw new JsonException("items must be an array.");
        }
    }

    private static void ValidateProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj) ValidateProperties(pair.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) ValidateProperties(child);
    }

    private static void ValidateIdentities(JsonArray array, string name)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is not JsonObject item || item["id"] is not JsonValue value ||
                !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new JsonException($"{name} must contain objects with unique, nonempty string ids.");
            if (name == "items" && item["text"] is JsonValue textNode && textNode.TryGetValue<string>(out var text) &&
                text.Length > PaperWindow.TodoTextMaxLength)
                throw new JsonException("Todo text exceeds the editor length limit.");
        }
    }

    private (string External, string Diff) PreserveReloadConflict(byte[] raw, string revision,
        IReadOnlyList<StateMergeConflict> conflicts)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        for (var number = 1; ; number++)
        {
            var external = Path.Combine(directory, $"dataconflict({number}).json");
            var diff = Path.Combine(directory, $"dataconflictdiff({number}).json");
            if (File.Exists(external) || File.Exists(diff)) continue;
            // CreateNew reserves each name. Never overwrite a previous recovery file.
            using var source = new FileStream(external, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            source.Write(raw);
            source.Flush(flushToDisk: true);
            var detail = JsonSerializer.SerializeToUtf8Bytes(new
            {
                time = DateTimeOffset.Now,
                baselineRevision = revision,
                conflicts
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var details = new FileStream(diff, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            details.Write(detail);
            details.Flush(flushToDisk: true);
            return (Path.GetFileName(external), Path.GetFileName(diff));
        }
    }

    internal string SavePendingExitRecovery(AppState memory)
    {
        _writeLock.Wait();
        try
        {
            var path = UniqueRecoveryCopyPath(FilePath, "unsaved_exit");
            _atomicWriter.Write(path, Encoding.UTF8.GetBytes(SerializeState(memory)));
            return path;
        }
        finally { _writeLock.Release(); }
    }
}
