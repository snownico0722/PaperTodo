using System.Text.Json.Nodes;

namespace PaperTodo;

public static class DataMergeConflictPolicies
{
    public const string Ask = "ask";
    public const string External = "external";
    public const string Internal = "internal";

    public static string Normalize(string? policy) => policy is External or Internal ? policy : Ask;
}

/// <summary>
/// 用户在 data.json 冲突弹窗中的选择。
/// </summary>
internal enum ConflictChoice
{
    /// <summary>默认:保留当前 PaperTodo 内存值(关闭/取消/Esc 等价于此)。</summary>
    KeepMemory = 0,

    /// <summary>用户明确选择:保留外部文件传入的值。</summary>
    KeepExternal = 1,
}

/// <summary>
/// 用户在 paper 级冲突弹窗中的选择:同一 paper 的冲突项保留本地或采用传入,非冲突修改始终保留。
/// </summary>
internal enum PaperConflictChoice
{
    /// <summary>默认:保留当前 PaperTodo 内存值(关闭/取消/Esc 等价于此)。</summary>
    KeepMemory = 0,

    /// <summary>用户明确选择:同一 paper 的冲突项用外部文件传入的值。</summary>
    KeepExternal = 1,
}

internal abstract record ConflictBucket;

/// <summary>Resolve all conflicting paths of one paper together; keep independent merged edits.</summary>
internal sealed record PaperBucket(
    string PaperId,
    string PaperType,
    IReadOnlyList<StateMergeConflict> Conflicts,
    JsonNode? MemoryPaper,
    JsonNode? ExternalPaper) : ConflictBucket;

internal sealed record OrderBucket(string Scope, StateMergeConflict Conflict) : ConflictBucket;
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
        var singles = new List<ConflictBucket>();
        foreach (var conflict in conflicts)
        {
            if (conflict.Path == "papers.$order")
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

        // Preserve the existing dialog order: global order, global fields, then papers.
        var result = new List<ConflictBucket>(orders.Count + singles.Count + papers.Count);
        result.AddRange(orders);
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

internal sealed record ResolvedStateMergeConflict(
    StateMergeConflict Conflict, ConflictChoice Choice, string Source);

/// <summary>
/// One set of decisions owns both the accepted state and its audit record. Policies
/// resolve only conflicts; independently merged values are never replaced wholesale.
/// </summary>
internal sealed class StateMergeResolution
{
    private StateMergeResolution(AppState state, string policy,
        IReadOnlyList<ResolvedStateMergeConflict> conflicts)
    {
        State = state;
        Policy = policy;
        Conflicts = conflicts;
    }

    internal AppState State { get; }
    internal string Policy { get; }
    internal IReadOnlyList<ResolvedStateMergeConflict> Conflicts { get; }

    internal static StateMergeResolution Resolve(AppState next,
        IReadOnlyList<StateMergeConflict>? conflicts, JsonObject? externalJson,
        string? policy, Func<ConflictBucket, ConflictChoice> ask)
    {
        var selectedPolicy = DataMergeConflictPolicies.Normalize(policy);
        if (conflicts is not { Count: > 0 }) return new(next, selectedPolicy, []);

        var choices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
        var decisions = new Dictionary<string, ResolvedStateMergeConflict>(StringComparer.Ordinal);
        foreach (var bucket in ConflictAggregator.Group(conflicts, externalJson))
        {
            var interactive = selectedPolicy == DataMergeConflictPolicies.Ask;
            var choice = interactive ? ask(bucket) : selectedPolicy == DataMergeConflictPolicies.External
                ? ConflictChoice.KeepExternal : ConflictChoice.KeepMemory;
            if (choice is not (ConflictChoice.KeepMemory or ConflictChoice.KeepExternal))
                throw new ArgumentOutOfRangeException(nameof(ask), "Unknown conflict choice.");
            IReadOnlyList<StateMergeConflict> members = bucket switch
            {
                PaperBucket paper => paper.Conflicts,
                OrderBucket order => [order.Conflict],
                SingleLeafBucket leaf => [leaf.Conflict],
                _ => throw new InvalidOperationException("Unknown conflict group.")
            };
            foreach (var conflict in members)
            {
                choices.Add(conflict.Path, choice);
                decisions.Add(conflict.Path, new(conflict, choice, interactive ? "user" : "policy"));
            }
        }

        var accepted = JsonPath.ApplyChoices(next, conflicts, choices, externalJson);
        return new(accepted, selectedPolicy, conflicts.Select(c => decisions[c.Path]).ToArray());
    }
}
