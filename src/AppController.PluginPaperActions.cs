using System.Diagnostics;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private PluginPaperActionRegistry? _pluginPaperActions;

    internal void SetPluginPaperActions(Guid ownerId, string providerId, string paperId,
        IReadOnlyList<PaperAction> actions, Func<bool> isActive, Action<PaperActionInvocation> invoke)
    {
        PaperSnapshot target;
        try
        {
            target = PaperCommands.GetPaper(paperId)
                ?? throw new PaperTodoPluginException("paper_not_found", "The paper no longer exists.");
        }
        catch (PaperCommandException ex) { throw new PaperTodoPluginException(ex.Code, ex.Message); }
        if (!IsActive(isActive))
            throw new PaperTodoPluginException("runtime_closed", "The owning Runtime ended while preparing the operation.");
        var normalized = PluginContributionPolicy.NormalizePaperActions(actions);
        (_pluginPaperActions ??= new()).Set(ownerId, providerId, target.Id, normalized, isActive, invoke);
    }

    internal void ClearPluginPaperActions(Guid ownerId, string paperId)
    {
        _pluginPaperActions?.Clear(ownerId, paperId);
    }
    internal void RemovePluginPaperActionsOwner(Guid ownerId)
    {
        _pluginPaperActions?.RemoveOwner(ownerId);
    }

    internal IReadOnlyList<PluginPaperActionBinding> GetPluginPaperActions(
        string paperId)
    {
        if (_pluginPaperActions == null) return [];
        var paper = State.Papers.FirstOrDefault(item => item.Id == paperId);
        return paper == null ? [] :
            _pluginPaperActions.Get(CapturePaperSnapshot(paper));
    }

    internal void InvokePluginPaperAction(PluginPaperActionBinding binding)
    {
        try
        {
            var position = PluginPopupHost.CapturePosition();
            var paper = PaperCommands.GetPaper(binding.PaperId);
            if (paper == null || _pluginPaperActions == null ||
                !_pluginPaperActions.TryResolve(binding, paper, out var invoke)) return;
            invoke!(new PaperActionInvocation(binding.Action.Id, paper, position));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Plugin Paper action failed. Provider={0}; Action={1}; Error={2}",
                binding.ProviderId, binding.Action.Id, ex.GetBaseException());
        }
    }
}

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

    internal PluginPaperActionBinding[] Get(PaperSnapshot paper) =>
        _owners.OrderBy(pair => pair.Value.Order)
            .Where(pair => Active(pair.Value) && pair.Value.Papers.ContainsKey(paper.Id))
            .SelectMany(pair =>
            {
                var entry = pair.Value.Papers[paper.Id];
                return entry.Actions
                    .Select(action => new PluginPaperActionBinding(
                        pair.Key, pair.Value.ProviderId, paper.Id, entry.Revision, action));
            })
            .ToArray();

    internal bool TryResolve(PluginPaperActionBinding binding, PaperSnapshot paper,
        out Action<PaperActionInvocation>? invoke)
    {
        invoke = null;
        if (binding.PaperId != paper.Id || !_owners.TryGetValue(binding.OwnerId, out var owner) ||
            owner.ProviderId != binding.ProviderId || !Active(owner) ||
            !owner.Papers.TryGetValue(paper.Id, out var entry) || entry.Revision != binding.Revision)
            return false;
        var action = entry.Actions.FirstOrDefault(item => item.Id == binding.Action.Id);
        if (action == null) return false;
        invoke = owner.Invoke;
        return true;
    }

    private static bool Active(Registration value)
    {
        try { return value.IsActive(); } catch { return false; }
    }
}
