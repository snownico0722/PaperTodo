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

    private sealed record PluginPageState(
        bool Enabled,
        bool Running,
        bool HasIssue);

    private sealed record PluginStateSwitchParts(
        Button Button,
        Border Track,
        Border Thumb);

    private PluginPageState PluginStateFor(
        PaperBodyPluginDescriptor descriptor)
    {
        var enabled = IsPluginEnabled(descriptor.Id);
        var running = enabled &&
            (IsPluginRuntimeRunning(descriptor.Id) ||
             _windows.Values.Any(window =>
                 window.HasRunningPluginBody(descriptor.Id)));
        var hasIssue =
            HasPluginRuntimeFailure(descriptor.Id) ||
            (descriptor.Kind != PaperBodyPluginKind.BuiltIn &&
             _paperBodyPlugins.Issues.Any(issue =>
                 PluginIssueMatchesDescriptor(issue, descriptor))) ||
            _windows.Values.Any(window =>
                window.HasFailedPluginBody(descriptor.Id));
        return new PluginPageState(enabled, running, hasIssue);
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

    private Border CreatePluginIssueDot()
    {
        return new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.DangerBrush,
            ToolTip = Strings.Get("PluginsStatusIssue")
        };
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
        ApplyPluginStateSwitch(parts, PluginStateFor(descriptor));
        button.Click += (_, _) =>
        {
            TogglePluginEnabled(descriptor);
            ApplyPluginStateSwitch(parts, PluginStateFor(descriptor));
        };
        return parts;
    }

    private void ApplyPluginStateSwitch(
        PluginStateSwitchParts parts,
        PluginPageState state)
    {
        parts.Thumb.HorizontalAlignment = state.Enabled
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        parts.Track.Background = !state.Enabled
            ? Theme.DangerBrush
            : state.Running
                ? new SolidColorBrush(
                    Theme.IsDark
                        ? Color.FromRgb(93, 190, 121)
                        : Color.FromRgb(55, 145, 82))
                : TrayWeakTextBrush;
        parts.Track.Opacity = state.Enabled && !state.Running ? 0.62 : 1;
        parts.Button.ToolTip = Strings.Get(!state.Enabled
            ? "PluginsStatusDisabled"
            : state.Running
                ? "PluginsStatusRunning"
                : "PluginsStatusStopped");
    }

    internal void QueuePluginStatusUiRefresh()
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
