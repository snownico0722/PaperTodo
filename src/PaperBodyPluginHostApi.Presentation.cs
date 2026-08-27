using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
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
