using PaperTodo.Plugin;
using System.Windows;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperNoteAssetsApi, IPaperPluginPopups
{
    private PluginPopupHost? _popupHost;

    public PaperNoteImage ReadImage(string paperId, string imageId) => PopupOnUi(() =>
    {
        Require(PaperTodoPermissionNames.NotesRead);
        return Invoke(() => _commands.ReadNoteImage(paperId, imageId));
    });

    public IPaperPluginPopup Open(PaperPopupPosition position, PaperPluginPopupOptions options,
        Func<PaperPluginPopupContext, IPaperPluginPopupContent> createContent) => PopupOnUi(() =>
    {
        EnsureUsable();
        if (_popupHost == null)
        {
            _popupHost = new PluginPopupHost(() => !_disposed && _isSessionCurrent());
            _controller.PluginPopupThemeChanged += _popupHost.RefreshTheme;
        }
        return _popupHost.Open(position, options, createContent);
    });

    public void Close() => PopupOnUi(() => { EnsureUsable(); _popupHost?.Close(); return true; });

    private static T PopupOnUi<T>(Func<T> action)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    internal void ResetExtensionUi()
    {
        var host = _popupHost;
        _popupHost = null;
        if (host != null)
        {
            _controller.PluginPopupThemeChanged -= host.RefreshTheme;
            host.Dispose();
        }
    }
}
