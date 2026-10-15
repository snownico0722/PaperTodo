using System.Text.Json.Nodes;

namespace PaperTodo;

internal abstract record ConflictBucket;

/// <summary>Resolve all conflicting paths of one paper together; keep independent merged edits.</summary>
internal sealed record PaperBucket(
    string PaperId,
    string PaperType,
    IReadOnlyList<StateMergeConflict> Conflicts,
    JsonNode? MemoryPaper,
    JsonNode? ExternalPaper) : ConflictBucket;

internal sealed record OrderBucket(string Scope, StateMergeConflict Conflict) : ConflictBucket;
internal sealed record UnknownBaselineBucket(StateMergeConflict Conflict) : ConflictBucket;
internal sealed record SingleLeafBucket(StateMergeConflict Conflict) : ConflictBucket;

/// <summary>Groups field, item existence and item order conflicts by stable paper identity.</summary>
internal static class ConflictAggregator
{
    public static IReadOnlyList<ConflictBucket> Group(
        IReadOnlyList<StateMergeConflict> conflicts, JsonObject? externalJson)
    {
        if (conflicts.Count == 0) return Array.Empty<ConflictBucket>();
        var papers = new Dictionary<string, List<StateMergeConflict>>(StringComparer.Ordinal);
        var orders = new List<ConflictBucket>();
        var unknown = new List<ConflictBucket>();
        var singles = new List<ConflictBucket>();
        foreach (var conflict in conflicts)
        {
            if (string.IsNullOrEmpty(conflict.Path) || conflict.Path == "$")
                unknown.Add(new UnknownBaselineBucket(conflict));
            else if (conflict.Path == "papers.$order")
                orders.Add(new OrderBucket("papers", conflict));
            else if (JsonPath.TryGetPaperId(conflict.Path, out var id))
            {
                if (!papers.TryGetValue(id, out var list)) papers[id] = list = new();
                // Append once, rather than copying the entire accumulated list for every field.
                list.Add(conflict);
            }
            else singles.Add(new SingleLeafBucket(conflict));
        }

        var externalById = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (externalJson?["papers"] is JsonArray externalPapers)
            foreach (var node in externalPapers)
                if (node is JsonObject paper && paper["id"] is JsonValue idValue &&
                    idValue.TryGetValue<string>(out var externalId)) externalById[externalId] = paper;

        // Preserve the existing dialog order: global order, whole-state, global fields, then papers.
        var result = new List<ConflictBucket>(orders.Count + unknown.Count + singles.Count + papers.Count);
        result.AddRange(orders);
        result.AddRange(unknown);
        result.AddRange(singles);
        foreach (var (id, list) in papers)
        {
            externalById.TryGetValue(id, out var externalPaper);
            var paperPath = JsonPath.EntityPath("papers", id);
            var whole = list.FirstOrDefault(c => c.Path == paperPath);
            var memoryPaper = whole?.PaperTodo;
            var typeNode = (externalPaper ?? memoryPaper)?["type"];
            var type = typeNode is JsonValue typeValue && typeValue.TryGetValue<string>(out var text) ? text : "";
            result.Add(new PaperBucket(id, type, list.ToArray(), memoryPaper, externalPaper));
        }
        return result;
    }
}
