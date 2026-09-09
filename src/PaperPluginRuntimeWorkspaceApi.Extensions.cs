using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperPluginRuntimeWorkspaceApi :
    IPaperPluginPaperActions, IPaperNoteAssetsApi, IPaperPluginPopups
{
    private Action<PaperActionInvocation>? _paperActionHandler;

    public PaperNoteImage ReadImage(string paperId, string imageId) => OnUi(() =>
    {
        EnsureUsable();
        return _inner.ReadImage(paperId, imageId);
    });

    void IPaperPluginPaperActions.SetActionHandler(Action<PaperActionInvocation>? handler) => OnUi(() =>
    {
        EnsureUsable();
        _paperActionHandler = handler;
        if (handler == null) _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
    });

    void IPaperPluginPaperActions.SetActions(string paperId, IReadOnlyList<PaperAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var snapshot = actions.ToArray();
        OnUi(() =>
        {
            EnsureUsable();
            EnsurePermission(PaperTodoPermissionNames.PapersRead, "Paper actions require papers.read.");
            if (snapshot.Length > 0 && _paperActionHandler == null)
                throw new PaperTodoPluginException("paper_action_handler_missing", "Register a Paper action handler first.");
            _controller.SetPluginPaperActions(_contributionOwnerId, _providerId, paperId, snapshot,
                () => !_disposed && _isActive(), invocation => _paperActionHandler?.Invoke(invocation));
        });
    }

    void IPaperPluginPaperActions.Clear(string paperId) => OnUi(() =>
    {
        EnsureUsable();
        _controller.ClearPluginPaperActions(_contributionOwnerId, paperId);
    });
    void IPaperPluginPaperActions.Clear() => OnUi(() =>
    {
        EnsureUsable();
        _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
    });

    public IPaperPluginPopup Open(PaperPopupPosition position, PaperPluginPopupOptions options,
        Func<PaperPluginPopupContext, IPaperPluginPopupContent> createContent) =>
        OnUi(() => { EnsureUsable(); return _inner.Open(position, options, createContent); });
    public void Close() => OnUi(() => { EnsureUsable(); _inner.Close(); });

    internal void ResetExtensionUi() => OnUi(() =>
    {
        _paperActionHandler = null;
        _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
        _inner.ResetExtensionUi();
    });
}
