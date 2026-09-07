using System.Windows;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private PluginUiAnchorStore? _pluginUiAnchors;
    private Dictionary<Guid, PluginSurfaceHost>? _pluginSurfaceHosts;

    internal PaperUiAnchor? CapturePluginUiAnchor(Guid leaseId, string paperId,
        FrameworkElement? source, Window? owner, bool transientSource = false) =>
        (_pluginUiAnchors ??= new()).Capture(leaseId, paperId, source, owner, transientSource);

    internal PluginSurfaceHost PluginSurfacesFor(Guid leaseId, string providerId,
        Func<bool> isActive, string? hostPaperId)
    {
        Application.Current.Dispatcher.VerifyAccess();
        _pluginSurfaceHosts ??= [];
        if (!_pluginSurfaceHosts.TryGetValue(leaseId, out var host))
        {
            host = new PluginSurfaceHost(leaseId, providerId, isActive,
                _pluginUiAnchors ??= new(), CurrentPluginSurfaceTheme,
                () => hostPaperId != null && _windows.TryGetValue(hostPaperId, out var paperWindow) &&
                    !paperWindow.IsClosed && paperWindow.IsVisible ? paperWindow : null,
                hostPaperId);
            _pluginSurfaceHosts.Add(leaseId, host);
        }
        return host;
    }

    internal void RevokePluginUi(Guid leaseId)
    {
        _pluginUiAnchors?.RemoveOwner(leaseId);
        if (_pluginSurfaceHosts?.Remove(leaseId, out var host) == true) host.Dispose();
    }
    private void ClosePluginSurfacesForPaper(string paperId)
    {
        _pluginUiAnchors?.RemovePaper(paperId);
        foreach (var host in _pluginSurfaceHosts?.Values.ToArray() ?? []) host.CloseForPaper(paperId);
    }
    private void RefreshPluginSurfaceThemes()
    {
        foreach (var host in _pluginSurfaceHosts?.Values.ToArray() ?? []) host.RefreshTheme();
    }
    private void DisposePluginSurfaces()
    {
        foreach (var id in _pluginSurfaceHosts?.Keys.ToArray() ?? []) RevokePluginUi(id);
        _pluginUiAnchors = null;
    }

    internal static PaperBodyTheme CurrentPluginSurfaceTheme()
    {
        static string Hex(Brush brush, string fallback) => brush is SolidColorBrush solid
            ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : fallback;
        return new PaperBodyTheme(Theme.IsDark, Hex(Theme.PaperBrush, "#FFF8E6"),
            Hex(Theme.TextBrush, "#202020"), Hex(Theme.WeakTextBrush, "#707070"),
            Hex(Theme.ActiveBrush, "#B07A31"), Hex(Theme.PaperBorderBrush, "#807050"),
            AppTypography.UiFontFamily.Source, AppTypography.ScaleFactor);
    }
}
