using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperWorkspacePresentationApi
{
    public string PaperId => RequireHostPaperId();

    public void Show(bool activate = true) =>
        QueuePresentation((paperId) =>
            _controller.TryShowPluginHostPaper(paperId, _providerId, activate));

    public void Hide() =>
        QueuePresentation((paperId) =>
            _controller.TryHidePluginHostPaper(paperId, _providerId));

    public void ToggleVisibility(bool activate = true) =>
        QueuePresentation((paperId) =>
            _controller.TryTogglePluginHostPaperVisibility(
                paperId,
                _providerId,
                activate));

    public void Expand(bool activate = true) =>
        QueuePresentation((paperId) =>
            _controller.TryExpandPluginHostPaper(paperId, _providerId, activate));

    public void Collapse() =>
        QueuePresentation((paperId) =>
            _controller.TryCollapsePluginHostPaper(paperId, _providerId));

    public void ToggleCollapsed(bool activate = true) =>
        QueuePresentation((paperId) =>
            _controller.TryTogglePluginHostPaperCollapsed(
                paperId,
                _providerId,
                activate));

    public void Activate() =>
        QueuePresentation((paperId) =>
            _controller.TryActivatePluginHostPaper(paperId, _providerId));

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

    private string RequireHostPaperId()
    {
        EnsureUsable();
        if (string.IsNullOrEmpty(_hostPaperId))
        {
            throw Error(
                "host_paper_unavailable",
                "This plugin context is not attached to a paper.");
        }
        return _hostPaperId;
    }

    private void QueuePresentation(Func<string, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var paperId = RequireHostPaperId();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw Error(
                "host_unavailable",
                "PaperTodo is shutting down.");
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_disposed || !_isSessionCurrent())
                {
                    return;
                }
                _ = action(paperId);
            }),
            DispatcherPriority.Background);
    }
}
