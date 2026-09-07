using System.Diagnostics;
using System.Windows;
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
        if (normalized.Length > 0) PaperWindow.EnsurePluginTopBarLoadedHandler();
        RefreshPluginTopBarForPaper(target.Id);
    }

    internal void ClearPluginPaperActions(Guid ownerId, string paperId)
    {
        _pluginPaperActions?.Clear(ownerId, paperId);
        RefreshPluginTopBarForPaper(paperId);
    }
    internal void RemovePluginPaperActionsOwner(Guid ownerId)
    {
        foreach (var paperId in _pluginPaperActions?.RemoveOwner(ownerId) ?? [])
            RefreshPluginTopBarForPaper(paperId);
    }

    internal IReadOnlyList<PluginPaperActionBinding> GetPluginPaperActions(
        string paperId, PaperActionPlacement placement)
    {
        if (_pluginPaperActions == null) return [];
        var paper = State.Papers.FirstOrDefault(item => item.Id == paperId);
        return paper == null ? [] :
            _pluginPaperActions.Get(CapturePaperSnapshot(paper), placement);
    }

    internal void InvokePluginPaperAction(PluginPaperActionBinding binding,
        PaperActionPlacement placement, FrameworkElement? source, Window? owner,
        bool transientSource = false)
    {
        try
        {
            var paper = PaperCommands.GetPaper(binding.PaperId);
            if (paper == null || _pluginPaperActions == null ||
                !_pluginPaperActions.TryResolve(binding, paper, placement, out var invoke)) return;
            var anchor = CapturePluginUiAnchor(binding.OwnerId, binding.PaperId, source, owner, transientSource);
            invoke!(new PaperActionInvocation(binding.Action.Id, paper, placement, anchor));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Plugin Paper action failed. Provider={0}; Action={1}; Error={2}",
                binding.ProviderId, binding.Action.Id, ex.GetBaseException());
        }
    }
}
