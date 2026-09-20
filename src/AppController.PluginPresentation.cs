using PaperTodo.Plugin;

namespace PaperTodo;

internal enum PaperPresentationAction
{
    Show, Hide, ToggleVisibility, Expand, Collapse, ToggleCollapsed, Activate
}

public sealed partial class AppController
{
    internal bool TryShowPluginHostPaper(string paperId, string providerId, bool activate) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.Show, activate);

    internal bool TryHidePluginHostPaper(string paperId, string providerId) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.Hide, true);

    internal bool TryTogglePluginHostPaperVisibility(string paperId, string providerId, bool activate) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.ToggleVisibility, activate);

    internal bool TryExpandPluginHostPaper(string paperId, string providerId, bool activate) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.Expand, activate);

    internal bool TryCollapsePluginHostPaper(string paperId, string providerId) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.Collapse, true);

    internal bool TryTogglePluginHostPaperCollapsed(string paperId, string providerId, bool activate) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.ToggleCollapsed, activate);

    internal bool TryActivatePluginHostPaper(string paperId, string providerId) =>
        TryPresentPluginHostPaper(paperId, providerId, PaperPresentationAction.Activate, true);


    private bool TryPresentPluginHostPaper(string paperId, string providerId,
        PaperPresentationAction action, bool activate)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper)) return false;
        return ApplyPaperPresentation(paper, action, activate);
    }

    // UI transitions stay here: neither the plugin/MCP adapters nor the capability entry
    // write IsVisible/IsCollapsed, touch HWNDs, or create a second persistence/animation owner.
    internal bool ApplyPaperPresentation(PaperData paper, PaperPresentationAction action, bool activate)
    {
        if (IsExiting) return false;
        action = action switch
        {
            PaperPresentationAction.ToggleVisibility => paper.IsVisible
                ? PaperPresentationAction.Hide : PaperPresentationAction.Show,
            PaperPresentationAction.ToggleCollapsed => paper.IsCollapsed
                ? PaperPresentationAction.Expand : PaperPresentationAction.Collapse,
            _ => action
        };
        switch (action)
        {
            case PaperPresentationAction.Show:
                ShowPaper(paper, activate);
                break;
            case PaperPresentationAction.Hide:
                HidePaper(paper);
                break;
            case PaperPresentationAction.Expand:
                SetPaperCollapsedRuntime(paper, collapsed: false,
                    animate: State.EnableAnimations, saveGeometry: true);
                ShowPaper(paper, activate);
                break;
            case PaperPresentationAction.Collapse:
                if (!CanPaperDisplayAsCapsule(paper)) return false;
                SetPaperCollapsedRuntime(paper, collapsed: true,
                    animate: State.EnableAnimations, saveGeometry: true);
                if (paper.IsVisible && State.UseCapsuleMode && State.UseDeepCapsuleMode)
                    ArrangeDeepCapsules(animate: State.EnableAnimations);
                RefreshTrayMenu();
                MarkDirty();
                break;
            case PaperPresentationAction.Activate:
                if (!paper.IsVisible) ShowPaper(paper, activate: true);
                else BringPaperToFront(paper);
                break;
            default:
                return false;
        }
        return true;
    }

    // Optional cross-paper capability shared by Native/Web/MCP. This is a presentation request,
    // not a content transaction: the existing UI transitions retain their normal save scheduling.
    internal PaperPresentationResult PresentWorkspacePaper(string paperId, PaperPresentationAction action,
        bool activate, PaperOperationContext context)
    {
        if (!IsRunning) throw new PaperCommandException("app_exiting", "PaperTodo is exiting.");
        var id = paperId?.Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !Enum.IsDefined(action))
            throw new PaperCommandException("invalid_params", "An exact paper ID and a valid presentation action are required.");
        // Attribute already-pending user changes before publishing this presentation request.
        // Showing a paper must not commit the content of unrelated editor/provider sessions.
        _paperBodyPluginEvents?.ScanNow(PaperOperationContext.User());
        var paper = State.Papers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
            ?? throw new PaperCommandException("paper_not_found", "The requested paper does not exist.");
        using (SuppressPaperPluginEventScans())
        {
            if (!ApplyPaperPresentation(paper, action, activate))
                throw new PaperCommandException("presentation_unavailable", "This paper cannot use the requested presentation.");
        }
        PublishExternalPaperOperation(context);
        return new PaperPresentationResult(paper.Id, paper.IsVisible, paper.IsCollapsed);
    }

    private bool TryGetPluginHostPaper(string paperId, string providerId, out PaperData paper)
    {
        paper = null!;
        if (IsExiting || string.IsNullOrWhiteSpace(paperId) || string.IsNullOrWhiteSpace(providerId))
            return false;
        var candidate = State.Papers.FirstOrDefault(item =>
            string.Equals(item.Id, paperId, StringComparison.Ordinal));
        if (candidate == null || candidate.Type != PaperTypes.Note ||
            !string.Equals(candidate.BodyProviderId, providerId, StringComparison.Ordinal))
            return false;
        paper = candidate;
        return true;
    }
}
