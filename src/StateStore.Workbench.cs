using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed partial class StateStore
{
    internal static AppState? ReadWorkbenchOverview(string path)
    {
        // File.Open rather than File.Exists: access/I/O failures must not look like an empty desk.
        string json;
        try { json = DecodeStateBytes(File.ReadAllBytes(path)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var document = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
            as JsonObject ?? throw new JsonException("Workspace must contain an object.");
        ValidateExternalDocument(document);
        var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
            ?? throw new JsonException("Workspace cannot be null.");
        // Migration is in-memory only: overview never writes another workspace's document.
        WorkspaceMaterials.Normalize(state);
        return state;
    }
}
