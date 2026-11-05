using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperPluginRuntimeWorkspaceApi : IPaperWorkspacePresentationApi
{
    public PaperPresentationResult ShowPaper(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ShowPaper(paperId, activate); });

    public PaperPresentationResult HidePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).HidePaper(paperId); });

    public PaperPresentationResult TogglePaperVisibility(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).TogglePaperVisibility(paperId, activate); });

    public PaperPresentationResult ExpandPaper(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ExpandPaper(paperId, activate); });

    public PaperPresentationResult CollapsePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).CollapsePaper(paperId); });

    public PaperPresentationResult TogglePaperCollapsed(string paperId, bool activate = true) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).TogglePaperCollapsed(paperId, activate); });

    public PaperPresentationResult ActivatePaper(string paperId) =>
        OnUi(() => { EnsureUsable(); return ((IPaperWorkspacePresentationApi)_inner).ActivatePaper(paperId); });
}
