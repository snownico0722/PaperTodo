using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperPluginRuntimeWorkspaceApi :
    IPaperPluginPaperActions, IPaperNoteAssetsApi, IPaperPluginSurfaces
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

    private PluginSurfaceHost SurfaceHost
    {
        get
        {
            EnsureUsable();
            return _controller.PluginSurfacesFor(
                _contributionOwnerId, _providerId, () => !_disposed && _isActive(), null);
        }
    }
    public IPaperPluginSurface OpenWindow(PaperPluginWindowOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) =>
        OnUi(() => SurfaceHost.OpenWindow(options, createContent));
    public IPaperPluginSurface OpenPopup(PaperUiAnchor anchor, PaperPluginPopupOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) =>
        OnUi(() => SurfaceHost.OpenPopup(anchor, options, createContent));
    public void Close(string id) => OnUi(() => SurfaceHost.Close(id));
    public void CloseAll() => OnUi(() => SurfaceHost.CloseAll());

    internal void ResetExtensionUi() => OnUi(() =>
    {
        _paperActionHandler = null;
        _controller.RemovePluginPaperActionsOwner(_contributionOwnerId);
        _controller.RevokePluginUi(_contributionOwnerId);
    });
}
