using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record StateReloadCommit(
    AppState? State, DataReloadResult Result,
    IReadOnlyList<StateMergeConflict>? Conflicts = null,
    JsonObject? ExternalJson = null,
    string? ObservedDiskJson = null);

public sealed partial class StateStore
{
    // A user-requested import is authoritative. Normal saves remain the original
    // whole-file atomic writer, with no revision metadata or retained history.
    internal StateReloadCommit ReloadExternalState(long version)
    {
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                return new(null, new() { Outcome = "busy" });

            // Do not fall back to the startup backup or manufacture an empty state.
            if (!File.Exists(FilePath))
                return new(null, new() { Outcome = "invalid", Error = "data.json is missing." });

            var json = File.ReadAllText(FilePath);
            var document = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("data.json must contain an object.");
            ValidateExternalDocument(document);
            var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
                ?? throw new JsonException("data.json cannot be null.");
            NormalizeAfterLoad(state);

            // Any asynchronous normal save queued before this request is now obsolete.
            // This does not write to disk: the imported file is already the primary file.
            _latestWrittenVersion = version;
            _mergeBaselineJson = json;
            return new(state, new() { Outcome = "applied", Applied = true });
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

    internal static AppState CopyForReload(AppState state) =>
        JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)!;

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

}
