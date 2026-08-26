using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private bool _pluginStatusRefreshQueued;
    private readonly Dictionary<string, Action> _pluginStatusRefreshers =
        new(StringComparer.Ordinal);

    private enum PluginPageStatus
    {
        Stopped,
        Running,
        Issue
    }

    private PluginPageStatus PluginStatusFor(
        PaperBodyPluginDescriptor descriptor,
        bool hasDataIssue)
    {
        if (hasDataIssue ||
            HasPluginRuntimeFailure(descriptor.Id) ||
            (descriptor.Kind != PaperBodyPluginKind.BuiltIn &&
             _paperBodyPlugins.Issues.Any(issue =>
                 PluginIssueMatchesDescriptor(issue, descriptor))) ||
            _windows.Values.Any(window =>
                window.HasFailedPluginBody(descriptor.Id)))
        {
            return PluginPageStatus.Issue;
        }

        return IsPluginRuntimeRunning(descriptor.Id) ||
               _windows.Values.Any(window =>
                   window.HasRunningPluginBody(descriptor.Id))
            ? PluginPageStatus.Running
            : PluginPageStatus.Stopped;
    }

    private static bool PluginIssueMatchesDescriptor(
        PaperBodyPluginLoadIssue issue,
        PaperBodyPluginDescriptor descriptor)
    {
        try
        {
            var issuePath = Path.GetFullPath(issue.SourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pluginDirectory = Path.GetFullPath(descriptor.PluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(
                    issuePath,
                    pluginDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                issuePath.StartsWith(
                    pluginDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    issuePath,
                    Path.GetFullPath(descriptor.SourcePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private Border CreatePluginStatusDot(PluginPageStatus status)
    {
        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyPluginStatusDot(dot, status);
        return dot;
    }

    private void ApplyPluginStatusDot(
        Border dot,
        PluginPageStatus status)
    {
        dot.Background = status switch
        {
            PluginPageStatus.Issue => Theme.DangerBrush,
            PluginPageStatus.Running => new SolidColorBrush(
                Theme.IsDark
                    ? Color.FromRgb(93, 190, 121)
                    : Color.FromRgb(55, 145, 82)),
            _ => TrayWeakTextBrush
        };
        var tipKey = status switch
        {
            PluginPageStatus.Issue => "PluginsStatusIssue",
            PluginPageStatus.Running => "PluginsStatusRunning",
            _ => "PluginsStatusStopped"
        };

        dot.Opacity = status == PluginPageStatus.Stopped ? 0.62 : 1;
        dot.ToolTip = Strings.Get(tipKey);
    }

    internal void QueuePluginStatusRefresh()
    {
        // Body attach/remove/provider switch is also the existing low-frequency signal that an
        // entity plugin paper may have appeared or disappeared. Reconciliation itself is gated
        // until startupPaper handling has completed.
        ReconcilePluginRuntimes();
        QueuePluginStatusUiRefresh();
    }

    private void QueuePluginStatusUiRefresh()
    {
        if (_pluginStatusRefreshQueued ||
            _settingsWindow is not { IsVisible: true } ||
            _settingsPage != SettingsPage.Plugins)
        {
            return;
        }

        _pluginStatusRefreshQueued = true;
        _ = Application.Current.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _pluginStatusRefreshQueued = false;
                if (_settingsWindow is { IsVisible: true } &&
                    _settingsPage == SettingsPage.Plugins)
                {
                    foreach (var refresh in
                             _pluginStatusRefreshers.Values.ToList())
                    {
                        refresh();
                    }
                }
            }),
            DispatcherPriority.Background);
    }
}
