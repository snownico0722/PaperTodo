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
        Disabled,
        Stopped,
        Running,
        Issue
    }

    private sealed record PluginStateSwitchParts(
        Button Button,
        Border Track,
        Border Thumb);

    private PluginPageStatus PluginStatusFor(
        PaperBodyPluginDescriptor descriptor)
    {
        if (!IsPluginEnabled(descriptor.Id))
        {
            return PluginPageStatus.Disabled;
        }

        if (HasPluginRuntimeFailure(descriptor.Id) ||
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

    private PluginStateSwitchParts CreatePluginStateSwitch(
        PaperBodyPluginDescriptor descriptor)
    {
        var thumb = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var track = new Border
        {
            Width = 29,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Padding = new Thickness(2),
            Child = thumb
        };
        var button = new Button
        {
            Width = 33,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 7, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Content = track,
            Style = BuildSettingsCloseButtonStyle(),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        var parts = new PluginStateSwitchParts(button, track, thumb);
        ApplyPluginStateSwitch(parts, PluginStatusFor(descriptor));
        button.Click += (_, _) =>
        {
            TogglePluginEnabled(descriptor);
            ApplyPluginStateSwitch(parts, PluginStatusFor(descriptor));
        };
        return parts;
    }

    private void ApplyPluginStateSwitch(
        PluginStateSwitchParts parts,
        PluginPageStatus status)
    {
        var enabled = status != PluginPageStatus.Disabled;
        var running = status == PluginPageStatus.Running;
        parts.Thumb.HorizontalAlignment = enabled
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        parts.Track.Background = !enabled
            ? Theme.DangerBrush
            : running
                ? new SolidColorBrush(
                    Theme.IsDark
                        ? Color.FromRgb(93, 190, 121)
                        : Color.FromRgb(55, 145, 82))
                : TrayWeakTextBrush;
        parts.Track.Opacity = enabled && !running ? 0.62 : 1;
        parts.Button.ToolTip = Strings.Get(status switch
        {
            PluginPageStatus.Disabled => "PluginsStatusDisabled",
            PluginPageStatus.Running => "PluginsStatusRunning",
            _ => "PluginsStatusStopped"
        });
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
