using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>Applies explicit conflict choices without replacing independently merged values.</summary>
internal static class JsonPath
{
    public static AppState ApplyChoices(AppState next, IReadOnlyList<StateMergeConflict> conflicts,
        IReadOnlyDictionary<string, ConflictChoice> choices, JsonObject? externalJson = null)
    {
        if (!choices.Values.Contains(ConflictChoice.KeepExternal)) return next;
        var json = JsonSerializer.SerializeToNode(next, StateStore.JsonOptions)!.AsObject();
        ApplyExternalChoices(json, conflicts, choices, externalJson);
        return ReadState(json);
    }

    /// <summary>
    /// A paper button resolves all conflicts in that paper, not its nonconflicting fields.
    /// Only an entity-level delete/edit conflict replaces, removes or restores the whole entity.
    /// </summary>
    public static AppState ApplyPaperChoices(AppState next,
        IReadOnlyDictionary<string, PaperConflictChoice> paperChoices, JsonObject externalJson,
        IReadOnlyList<StateMergeConflict> conflicts)
    {
        if (paperChoices.Count == 0) return next;
        var choices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
            if (TryGetPaperId(conflict.Path, out var id) &&
                paperChoices.TryGetValue(id, out var choice) && choice == PaperConflictChoice.KeepExternal)
                choices[conflict.Path] = ConflictChoice.KeepExternal;
        return ApplyChoices(next, conflicts, choices, externalJson);
    }

    internal static JsonObject ApplyChoices(JsonObject next, IReadOnlyList<StateMergeConflict> conflicts,
        IReadOnlyDictionary<string, ConflictChoice> choices, JsonObject? externalJson = null)
    {
        var result = (JsonObject)next.DeepClone();
        ApplyExternalChoices(result, conflicts, choices, externalJson);
        return result;
    }

    private static void ApplyExternalChoices(JsonObject result, IReadOnlyList<StateMergeConflict> conflicts,
        IReadOnlyDictionary<string, ConflictChoice> choices, JsonObject? externalJson)
    {
        foreach (var conflict in conflicts)
            if (choices.TryGetValue(conflict.Path, out var choice) && choice == ConflictChoice.KeepExternal)
                ApplyExternalToPath(result, conflict, externalJson);
    }

    private static AppState ReadState(JsonObject json) =>
        JsonSerializer.Deserialize<AppState>(json.ToJsonString(StateStore.JsonOptions), StateStore.JsonOptions)!;

    // Path components are encoded independently: IDs and dictionary keys may contain '.', ']' or '%'.
    private static string Encode(string value) => Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);
    internal static string FieldPath(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? Encode(name) : $"{parent}.{Encode(name)}";
    internal static string EntityPath(string parent, string id) => $"{parent}[id={Encode(id)}]";

    internal static bool TryGetPaperId(string path, out string id)
    {
        id = "";
        var segments = ParsePath(path);
        if (segments.Count < 2 || segments[0].IsArrayById || segments[0].Name != "papers" ||
            !segments[1].IsArrayById) return false;
        id = segments[1].Id;
        return true;
    }

    private static void ApplyExternalToPath(JsonObject root, StateMergeConflict conflict, JsonObject? externalJson)
    {
        if (conflict.Path == "$" || conflict.Path.Length == 0)
        {
            if (!conflict.ExternalExists || conflict.External is not JsonObject state)
                throw new JsonException("A whole-state choice must contain an object.");
            root.Clear();
            foreach (var pair in state) root[pair.Key] = pair.Value?.DeepClone();
            return;
        }

        var segments = ParsePath(conflict.Path);
        if (segments.Count == 0) throw new JsonException("Invalid merge conflict path.");
        var leaf = segments[^1];
        var parentPath = segments.Take(segments.Count - 1).ToArray();
        var parent = Navigate(root, parentPath);
        if (leaf.IsOrderMarker)
        {
            if (!conflict.ExternalExists || parent is not JsonArray array || conflict.External is not JsonArray order)
                throw new JsonException("Invalid collection order choice.");
            ReorderArray(array, order);
        }
        else if (leaf.IsArrayById)
        {
            if (parent is not JsonArray array) throw new JsonException("Missing conflict collection.");
            ApplyEntityChoice(array, leaf.Id, conflict,
                externalJson == null ? null : Navigate(externalJson, parentPath) as JsonArray);
        }
        else
        {
            if (parent is not JsonObject obj) throw new JsonException("Missing conflict object.");
            // Missing and explicit JSON null have different meanings; neither is a no-op.
            if (conflict.ExternalExists) obj[leaf.Name] = conflict.External?.DeepClone();
            else obj.Remove(leaf.Name);
        }
    }

    private static void ApplyEntityChoice(JsonArray array, string id, StateMergeConflict conflict, JsonArray? externalArray)
    {
        var index = FindIndex(array, id);
        if (!conflict.ExternalExists)
        {
            if (index >= 0) array.RemoveAt(index);
            return;
        }
        if (conflict.External is not JsonObject entity || entity["id"]?.GetValue<string>() != id)
            throw new JsonException("An entity choice must retain its identity.");
        if (index >= 0)
        {
            array[index] = entity.DeepClone();
            return;
        }

        // Restore beside surviving incoming neighbors, without replacing or reordering siblings.
        var incoming = externalArray?.Select(item => item!["id"]!.GetValue<string>()).ToList();
        var position = incoming?.IndexOf(id) ?? -1;
        var previous = position < 0 ? -1 : incoming!.Take(position).Reverse()
            .Select(other => FindIndex(array, other)).FirstOrDefault(i => i >= 0, -1);
        var next = position < 0 ? -1 : incoming!.Skip(position + 1)
            .Select(other => FindIndex(array, other)).FirstOrDefault(i => i >= 0, -1);
        array.Insert(previous >= 0 ? previous + 1 : next >= 0 ? next : array.Count, entity.DeepClone());
    }

    private static int FindIndex(JsonArray array, string id)
    {
        for (var i = 0; i < array.Count; i++)
            if (array[i] is JsonObject obj && obj["id"]?.GetValue<string>() == id) return i;
        return -1;
    }

    private static JsonNode? Navigate(JsonNode root, IReadOnlyList<PathSegment> segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (segment.IsArrayById)
            {
                if (current is not JsonArray array) return null;
                var index = FindIndex(array, segment.Id);
                current = index < 0 ? null : array[index];
            }
            else
            {
                if (current is not JsonObject obj) return null;
                current = obj.TryGetPropertyValue(segment.Name, out var node) ? node : null;
            }
        }
        return current;
    }

    private static void ReorderArray(JsonArray array, JsonArray desiredOrder)
    {
        var byId = array.ToDictionary(item => item!["id"]!.GetValue<string>(), item => item, StringComparer.Ordinal);
        var original = byId.Keys.ToArray();
        var order = desiredOrder.Select(item => item!.GetValue<string>()).Where(byId.ContainsKey)
            .Distinct(StringComparer.Ordinal).ToList();
        // Order conflicts contain common IDs only. Keep independent additions beside surviving neighbors.
        StateJsonMerge.AddMissingIdentities(order, original);
        var copies = order.Select(id => byId[id]!.DeepClone()).ToArray();
        array.Clear();
        foreach (var item in copies) array.Add(item);
    }

    private static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        if (path.Length == 0 || path == "$") return segments;
        var offset = 0;
        while (offset < path.Length)
        {
            var start = offset;
            while (offset < path.Length && path[offset] != '.' && path[offset] != '[') offset++;
            if (offset == start) throw new JsonException("Invalid merge conflict path.");
            var name = path[start..offset];
            segments.Add(name == "$order" ? PathSegment.OrderMarker() : PathSegment.Field(Uri.UnescapeDataString(name)));
            while (offset < path.Length && path[offset] == '[')
            {
                if (!path.AsSpan(offset).StartsWith("[id=", StringComparison.Ordinal))
                    throw new JsonException("Invalid merge identity path.");
                var end = path.IndexOf(']', offset + 4);
                if (end < 0) throw new JsonException("Unclosed merge identity path.");
                segments.Add(PathSegment.ArrayById(Uri.UnescapeDataString(path[(offset + 4)..end])));
                offset = end + 1;
            }
            if (offset == path.Length) break;
            if (path[offset++] != '.' || offset == path.Length) throw new JsonException("Invalid merge conflict path.");
        }
        return segments;
    }

    private readonly record struct PathSegment(string Name, string Id, bool IsArrayById, bool IsOrderMarker)
    {
        public static PathSegment Field(string name) => new(name, "", false, false);
        public static PathSegment ArrayById(string id) => new("", id, true, false);
        public static PathSegment OrderMarker() => new("$order", "", false, true);
    }
}
