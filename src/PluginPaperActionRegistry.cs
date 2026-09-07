using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record PluginPaperActionBinding(
    Guid OwnerId, string ProviderId, string PaperId, long Revision, PaperAction Action);

/// <summary>
/// UI-dispatcher-owned, volatile registrations. Revisions prevent an already-open menu or an
/// asynchronously delivered click from executing a replacement registration with the same id.
/// </summary>
internal sealed class PluginPaperActionRegistry
{
    private sealed record Registration(string ProviderId, long Order,
        Func<bool> IsActive, Action<PaperActionInvocation> Invoke)
    {
        internal Dictionary<string, (long Revision, PaperAction[] Actions)> Papers { get; } = [];
    }
    private readonly Dictionary<Guid, Registration> _owners = [];
    private long _sequence;

    internal void Set(Guid ownerId, string providerId, string paperId, PaperAction[] actions,
        Func<bool> isActive, Action<PaperActionInvocation> invoke)
    {
        if (!_owners.TryGetValue(ownerId, out var owner))
        {
            if (_owners.Values.Any(value => value.ProviderId == providerId))
                throw new PaperTodoPluginException("paper_action_runtime_conflict", "A provider already owns Paper actions.");
            owner = new Registration(providerId, ++_sequence, isActive, invoke);
            _owners.Add(ownerId, owner);
        }
        if (actions.Length == 0) owner.Papers.Remove(paperId);
        else owner.Papers[paperId] = (++_sequence, actions);
    }

    internal string[] RemoveOwner(Guid ownerId) =>
        _owners.Remove(ownerId, out var owner) ? owner.Papers.Keys.ToArray() : [];
    internal void Clear(Guid ownerId, string paperId)
    {
        if (_owners.TryGetValue(ownerId, out var owner)) owner.Papers.Remove(paperId);
    }
    internal void RemovePaper(string paperId)
    {
        foreach (var owner in _owners.Values) owner.Papers.Remove(paperId);
    }

    internal PluginPaperActionBinding[] Get(PaperSnapshot paper, PaperActionPlacement placement) =>
        _owners.OrderBy(pair => pair.Value.Order)
            .Where(pair => Active(pair.Value) && pair.Value.Papers.ContainsKey(paper.Id))
            .SelectMany(pair =>
            {
                var entry = pair.Value.Papers[paper.Id];
                return entry.Actions.Where(action => Matches(action, paper, placement))
                    .Select(action => new PluginPaperActionBinding(
                        pair.Key, pair.Value.ProviderId, paper.Id, entry.Revision, action));
            })
            .OrderByDescending(binding => binding.Action.Priority).ToArray();

    internal bool TryResolve(PluginPaperActionBinding binding, PaperSnapshot paper,
        PaperActionPlacement placement, out Action<PaperActionInvocation>? invoke)
    {
        invoke = null;
        if (binding.PaperId != paper.Id || !_owners.TryGetValue(binding.OwnerId, out var owner) ||
            owner.ProviderId != binding.ProviderId || !Active(owner) ||
            !owner.Papers.TryGetValue(paper.Id, out var entry) || entry.Revision != binding.Revision)
            return false;
        var action = entry.Actions.FirstOrDefault(item => item.Id == binding.Action.Id);
        if (action == null || !action.Enabled || !Matches(action, paper, placement)) return false;
        invoke = owner.Invoke;
        return true;
    }

    private static bool Matches(PaperAction action, PaperSnapshot paper, PaperActionPlacement placement) =>
        action.Visible && (action.Placement & placement) != 0 &&
        (action.BodyProviderId == null || paper.Type == PaperTypes.Note &&
            string.Equals(action.BodyProviderId, paper.BodyProviderId, StringComparison.Ordinal));
    private static bool Active(Registration value)
    {
        try { return value.IsActive(); } catch { return false; }
    }
}
