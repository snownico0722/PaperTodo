using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperWorkspacePresentationApi
{
    public PaperPresentationResult ShowPaper(string paperId, bool activate = true) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.Show, activate);

    public PaperPresentationResult HidePaper(string paperId) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.Hide, true);

    public PaperPresentationResult TogglePaperVisibility(string paperId, bool activate = true) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.ToggleVisibility, activate);

    public PaperPresentationResult ExpandPaper(string paperId, bool activate = true) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.Expand, activate);

    public PaperPresentationResult CollapsePaper(string paperId) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.Collapse, true);

    public PaperPresentationResult TogglePaperCollapsed(string paperId, bool activate = true) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.ToggleCollapsed, activate);

    public PaperPresentationResult ActivatePaper(string paperId) =>
        PresentWorkspacePaper(paperId, PaperPresentationAction.Activate, true);


    private PaperPresentationResult PresentWorkspacePaper(string paperId, PaperPresentationAction action,
        bool activate) => PopupOnUi(() =>
    {
        return Invoke(() => _controller.PresentWorkspacePaper(paperId, action, activate,
            PaperOperationContext.Plugin(_providerId)));
    });
}
