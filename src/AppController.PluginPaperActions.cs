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
