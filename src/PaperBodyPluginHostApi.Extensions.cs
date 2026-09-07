using PaperTodo.Plugin;
using System.Windows;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperNoteAssetsApi, IPaperPluginSurfaces
{
    public PaperNoteImage ReadImage(string paperId, string imageId) => SurfaceOnUi(() =>
    {
        Require(PaperTodoPermissionNames.NotesRead);
        return Invoke(() => _commands.ReadNoteImage(paperId, imageId));
    });

    private PluginSurfaceHost SurfaceHost
    {
        get
        {
            EnsureUsable();
            return _controller.PluginSurfacesFor(
                _sessionId, _providerId, () => !_disposed && _isSessionCurrent(), _hostPaperId);
        }
    }

    public IPaperPluginSurface OpenWindow(PaperPluginWindowOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) =>
        SurfaceOnUi(() => SurfaceHost.OpenWindow(options, createContent));
    public IPaperPluginSurface OpenPopup(PaperUiAnchor anchor, PaperPluginPopupOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) =>
        SurfaceOnUi(() => SurfaceHost.OpenPopup(anchor, options, createContent));
    public void Close(string id) => SurfaceOnUi(() => { SurfaceHost.Close(id); return true; });
    public void CloseAll() => SurfaceOnUi(() => { SurfaceHost.CloseAll(); return true; });
    private static T SurfaceOnUi<T>(Func<T> callback)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? callback() : dispatcher.Invoke(callback);
    }

    // Document navigation revokes UI tokens and surfaces without ending the owning body session.
    internal void ResetExtensionUi() => _controller.RevokePluginUi(_sessionId);
}
