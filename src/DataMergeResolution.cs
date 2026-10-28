using System.Text.Json.Nodes;

namespace PaperTodo;

public static class DataMergeConflictPolicies
{
    public const string Ask = "ask";
    public const string External = "external";
    public const string Internal = "internal";

    public static string Normalize(string? policy) => policy is External or Internal ? policy : Ask;
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
            // Without the actual original version there is no field-level conflict to
            // resolve. An automatic preference must not authorize a whole-state overwrite.
            var interactive = selectedPolicy == DataMergeConflictPolicies.Ask || bucket is UnknownBaselineBucket;
            var choice = interactive ? ask(bucket) : selectedPolicy == DataMergeConflictPolicies.External
                ? ConflictChoice.KeepExternal : ConflictChoice.KeepMemory;
            if (choice is not (ConflictChoice.KeepMemory or ConflictChoice.KeepExternal))
                throw new ArgumentOutOfRangeException(nameof(ask), "Unknown conflict choice.");
            IReadOnlyList<StateMergeConflict> members = bucket switch
            {
                PaperBucket paper => paper.Conflicts,
                OrderBucket order => [order.Conflict],
                UnknownBaselineBucket unknown => [unknown.Conflict],
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
