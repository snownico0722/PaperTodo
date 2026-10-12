using System.Text.Json.Nodes;

namespace PaperTodo;

internal sealed record StateMergeConflict(
    string Path,
    bool BaselineExists, JsonNode? Baseline,
    bool PaperTodoExists, JsonNode? PaperTodo,
    bool ExternalExists, JsonNode? External,
    string Reason = "concurrent_change")
{
    public string Resolution => "paperTodo";
}

internal sealed record StateMergeResult(
    JsonObject State,
    IReadOnlyList<string> AppliedPaths,
    IReadOnlyList<StateMergeConflict> Conflicts);

/// <summary>Pure three-way merge. Paper/Todo identity is an id, never an array index.</summary>
internal static class StateJsonMerge
{
    private readonly record struct Value(bool Exists, JsonNode? Node)
    {
        public Value Copy() => new(Exists, Node?.DeepClone());
    }

    public static StateMergeResult Merge(JsonObject baseline, JsonObject memory, JsonObject external)
    {
        var applied = new List<string>();
        var conflicts = new List<StateMergeConflict>();
        var result = MergeValue(new(true, baseline), new(true, memory), new(true, external), "", applied, conflicts);
        return new((JsonObject)result.Node!, applied, conflicts);
    }

    private static bool Same(Value left, Value right) =>
        left.Exists == right.Exists && JsonNode.DeepEquals(left.Node, right.Node);

    private static Value Member(JsonObject value, string key) =>
        new(value.TryGetPropertyValue(key, out var node), node);

    private static Value MergeValue(Value baseline, Value memory, Value external, string path,
        List<string> applied, List<StateMergeConflict> conflicts)
    {
        if (Same(memory, external) || Same(baseline, external)) return memory.Copy();

        if (baseline.Node is JsonObject b && memory.Node is JsonObject m && external.Node is JsonObject e)
        {
            var result = new JsonObject();
            foreach (var key in b.Select(x => x.Key).Concat(m.Select(x => x.Key)).Concat(e.Select(x => x.Key)).Distinct(StringComparer.Ordinal))
            {
                var value = MergeValue(Member(b, key), Member(m, key), Member(e, key),
                    string.IsNullOrEmpty(path) ? key : $"{path}.{key}", applied, conflicts);
                if (value.Exists) result[key] = value.Node;
            }
            return new(true, result);
        }

        if ((path == "papers" || path.EndsWith("].items", StringComparison.Ordinal)) &&
            baseline.Node is JsonArray ba && memory.Node is JsonArray ma && external.Node is JsonArray ea)
        {
            return new(true, MergeEntities(ba, ma, ea, path, applied, conflicts));
        }

        if (Same(baseline, memory))
        {
            applied.Add(path);
            return external.Copy();
        }

        conflicts.Add(new(path, baseline.Exists, baseline.Node?.DeepClone(),
            memory.Exists, memory.Node?.DeepClone(), external.Exists, external.Node?.DeepClone()));
        return memory.Copy();
    }

    private static Dictionary<string, JsonNode?> Index(JsonArray array) =>
        array.ToDictionary(node => node!["id"]!.GetValue<string>(), node => node, StringComparer.Ordinal);

    private static JsonArray MergeEntities(JsonArray baseline, JsonArray memory, JsonArray external,
        string path, List<string> applied, List<StateMergeConflict> conflicts)
    {
        var b = Index(baseline);
        var m = Index(memory);
        var e = Index(external);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var id in b.Keys.Concat(m.Keys).Concat(e.Keys).Distinct(StringComparer.Ordinal))
        {
            var value = MergeValue(new(b.TryGetValue(id, out var bv), bv),
                new(m.TryGetValue(id, out var mv), mv), new(e.TryGetValue(id, out var ev), ev),
                $"{path}[id={id}]", applied, conflicts);
            if (value.Exists) values[id] = value.Node;
        }

        // Insertions/deletions are not reorders. Compare only surviving common identities.
        var common = b.Keys.Where(id => m.ContainsKey(id) && e.ContainsKey(id) && values.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var bo = b.Keys.Where(common.Contains).ToArray();
        var mo = m.Keys.Where(common.Contains).ToArray();
        var eo = e.Keys.Where(common.Contains).ToArray();
        var preferExternal = mo.SequenceEqual(bo) && !eo.SequenceEqual(bo);
        if (!mo.SequenceEqual(bo) && !eo.SequenceEqual(bo) && !mo.SequenceEqual(eo))
        {
            conflicts.Add(new($"{path}.$order", true, OrderNode(bo), true, OrderNode(mo), true, OrderNode(eo)));
        }
        else if (preferExternal) applied.Add($"{path}.$order");

        var order = (preferExternal ? e.Keys : m.Keys).Where(values.ContainsKey).ToList();
        AddMissingIdentities(order, m.Keys.Where(values.ContainsKey).ToArray());
        AddMissingIdentities(order, e.Keys.Where(values.ContainsKey).ToArray());
        return new JsonArray(order.Select(id => values[id]).ToArray());
    }

    private static JsonArray OrderNode(IEnumerable<string> ids) =>
        new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    private static void AddMissingIdentities(List<string> result, IReadOnlyList<string> other)
    {
        for (var i = 0; i < other.Count; i++)
        {
            if (result.Contains(other[i], StringComparer.Ordinal)) continue;
            var previous = i > 0 ? result.IndexOf(other[i - 1]) : -1;
            var next = other.Skip(i + 1).Select(item => result.IndexOf(item)).FirstOrDefault(index => index >= 0, -1);
            result.Insert(previous >= 0 ? previous + 1 : next >= 0 ? next : result.Count, other[i]);
        }
    }
}
