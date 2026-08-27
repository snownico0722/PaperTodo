using System.Diagnostics;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record PluginTodoActionBinding(
    Guid OwnerId,
    string ProviderId,
    PaperTodoAction Action);

public sealed partial class AppController
{
    private sealed class PluginTodoActionRegistration
    {
        public required Guid OwnerId { get; init; }
        public required string ProviderId { get; init; }
        public required Func<bool> IsActive { get; set; }
        public required Action<PaperTodoActionInvocation> Invoke { get; set; }
        public required long Order { get; init; }
        public Dictionary<(string PaperId, string TodoId), PaperTodoAction[]> Actions { get; } = [];
    }

    private readonly Dictionary<string, PluginTodoActionRegistration>
        _pluginTodoActionRegistrations = new(StringComparer.Ordinal);
    private long _pluginTodoActionRegistrationOrder;

    internal void SetPluginTodoActions(
        Guid ownerId,
        string providerId,
        string paperId,
        string todoId,
        IReadOnlyList<PaperTodoAction> actions,
        Func<bool> isActive,
        Action<PaperTodoActionInvocation> invoke)
    {
        var (paper, item) = RequirePluginTodoTarget(paperId, todoId);
        var normalized = PluginContributionPolicy.NormalizeTodoActions(actions);

        if (_pluginTodoActionRegistrations.TryGetValue(providerId, out var current) &&
            current.OwnerId != ownerId)
        {
            throw new PaperTodoPluginException(
                "todo_action_runtime_conflict",
                "A provider can have only one active Runtime Todo-action owner.");
        }

        if (current == null)
        {
            current = new PluginTodoActionRegistration
            {
                OwnerId = ownerId,
                ProviderId = providerId,
                IsActive = isActive,
                Invoke = invoke,
                Order = ++_pluginTodoActionRegistrationOrder
            };
            _pluginTodoActionRegistrations.Add(providerId, current);
        }
        else
        {
            current.IsActive = isActive;
            current.Invoke = invoke;
        }

        var key = (PaperId: paper.Id, TodoId: item.Id);
        if (normalized.Length == 0)
        {
            current.Actions.Remove(key);
        }
        else
        {
            current.Actions[key] = normalized;
            PaperWindow.EnsurePluginTodoActionsLoadedHandler();
        }
        RefreshPluginTodoActions(key.PaperId, key.TodoId);
    }

    internal void ClearPluginTodoActions(
        Guid ownerId,
        string providerId,
        string paperId,
        string todoId)
    {
        if (!_pluginTodoActionRegistrations.TryGetValue(providerId, out var registration) ||
            registration.OwnerId != ownerId)
        {
            return;
        }

        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        var normalizedTodoId = todoId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length == 0 || normalizedTodoId.Length == 0)
        {
            return;
        }
        if (registration.Actions.Remove((normalizedPaperId, normalizedTodoId)))
        {
            RefreshPluginTodoActions(normalizedPaperId, normalizedTodoId);
        }
    }

    internal void RemovePluginTodoActionsOwner(Guid ownerId, string providerId)
    {
        if (!_pluginTodoActionRegistrations.TryGetValue(providerId, out var registration) ||
            registration.OwnerId != ownerId)
        {
            return;
        }

        var affected = registration.Actions.Keys.ToArray();
        _pluginTodoActionRegistrations.Remove(providerId);
        foreach (var (paperId, todoId) in affected)
        {
            RefreshPluginTodoActions(paperId, todoId);
        }
    }

    internal void PrunePluginTodoActionsForPaper(string paperId)
    {
        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length == 0 || _pluginTodoActionRegistrations.Count == 0)
        {
            return;
        }

        foreach (var registration in _pluginTodoActionRegistrations.Values)
        {
            foreach (var key in registration.Actions.Keys
                         .Where(key =>
                             string.Equals(
                                 key.PaperId,
                                 normalizedPaperId,
                                 StringComparison.Ordinal) &&
                             !TryGetTodoTarget(key.PaperId, key.TodoId, out _, out _))
                         .ToArray())
            {
                registration.Actions.Remove(key);
            }
        }
    }

    internal void RemovePluginTodoActionsForPaper(string paperId)
    {
        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length == 0 || _pluginTodoActionRegistrations.Count == 0)
        {
            return;
        }

        foreach (var registration in _pluginTodoActionRegistrations.Values)
        {
            foreach (var key in registration.Actions.Keys
                         .Where(key => string.Equals(
                             key.PaperId,
                             normalizedPaperId,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                registration.Actions.Remove(key);
            }
        }
    }

    internal IReadOnlyList<PluginTodoActionBinding> GetPluginTodoActions(
        string paperId,
        string todoId)
    {
        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        var normalizedTodoId = todoId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length == 0 || normalizedTodoId.Length == 0 ||
            !TryGetTodoTarget(normalizedPaperId, normalizedTodoId, out _, out _))
        {
            return [];
        }

        var candidates = new List<(
            PluginTodoActionRegistration Registration,
            PaperTodoAction Action,
            int DeclarationOrder)>();
        foreach (var registration in _pluginTodoActionRegistrations.Values)
        {
            if (!IsActive(registration.IsActive) ||
                !registration.Actions.TryGetValue(
                    (normalizedPaperId, normalizedTodoId),
                    out var actions))
            {
                continue;
            }

            for (var index = 0; index < actions.Length; index++)
            {
                if (actions[index].Visible)
                {
                    candidates.Add((registration, actions[index], index));
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Action.Priority)
            .ThenBy(candidate => candidate.Registration.Order)
            .ThenBy(candidate => candidate.DeclarationOrder)
            .Select(candidate => new PluginTodoActionBinding(
                candidate.Registration.OwnerId,
                candidate.Registration.ProviderId,
                candidate.Action))
            .ToArray();
    }

    internal void InvokePluginTodoAction(
        PluginTodoActionBinding binding,
        string paperId,
        string todoId)
    {
        if (!_pluginTodoActionRegistrations.TryGetValue(binding.ProviderId, out var registration) ||
            registration.OwnerId != binding.OwnerId ||
            !IsActive(registration.IsActive) ||
            !registration.Actions.TryGetValue((paperId, todoId), out var actions))
        {
            return;
        }

        var action = actions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, binding.Action.Id, StringComparison.Ordinal));
        if (action == null || !action.Visible || !action.Enabled ||
            !TryGetTodoTarget(paperId, todoId, out var paper, out var item))
        {
            return;
        }

        try
        {
            registration.Invoke(new PaperTodoActionInvocation(
                action.Id,
                paper.Id,
                item.Id,
                CaptureTodoSnapshot(paper, item)));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Plugin Todo action failed. Provider={0}; Action={1}; Paper={2}; Todo={3}; Exception={4}",
                binding.ProviderId,
                action.Id,
                paperId,
                todoId,
                ex.GetBaseException());
        }
    }

    private (PaperData Paper, PaperItem Item) RequirePluginTodoTarget(
        string paperId,
        string todoId)
    {
        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        var normalizedTodoId = todoId?.Trim() ?? string.Empty;
        if (TryGetTodoTarget(normalizedPaperId, normalizedTodoId, out var paper, out var item))
        {
            return (paper, item);
        }

        throw new PaperTodoPluginException(
            "todo_not_found",
            "The requested Todo does not exist.");
    }

    private bool TryGetTodoTarget(
        string paperId,
        string todoId,
        out PaperData paper,
        out PaperItem item)
    {
        var foundPaper = State.Papers.FirstOrDefault(candidate =>
            candidate.Type == PaperTypes.Todo &&
            string.Equals(candidate.Id, paperId, StringComparison.Ordinal));
        var foundItem = foundPaper?.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, todoId, StringComparison.Ordinal));
        if (foundPaper == null || foundItem == null)
        {
            paper = null!;
            item = null!;
            return false;
        }

        paper = foundPaper;
        item = foundItem;
        return true;
    }

    private void RefreshPluginTodoActions(string paperId, string todoId)
    {
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.RefreshPluginTodoActions(todoId);
        }
    }
}
