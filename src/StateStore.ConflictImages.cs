using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed partial class StateStore
{
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
}
