using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private FrameworkElement? _workspaceSettingsPanel;

    private UIElement BuildWorkspaceSettingsPage()
    {
        var root = new StackPanel { Margin = new Thickness(2, 4, 4, 0) };
        _workspaceSettingsPanel = root;
        root.IsEnabled = !_workspaceSwitching;
        root.Children.Add(SettingsSectionLabel(Strings.Get("WorkspaceSettingsTitle")));
        root.Children.Add(WorkspaceDescription(Strings.Get("WorkspaceSettingsDescription")));
        root.Children.Add(SettingsToggle(Strings.Get("WorkspaceEnabled"), WorkspaceOptions.Enabled, async () =>
        {
            var next = WorkspaceOptions.Normalized();
            next.Enabled = !next.Enabled;
            await ChangeWorkspaceConfigurationAsync(next);
        }));
        root.Children.Add(SettingsSectionLabel(Strings.Get("WorkspaceCount")));
        var counts = Enumerable.Range(2, 4).Select(n =>
            (n.ToString(System.Globalization.CultureInfo.InvariantCulture), n.ToString(UiLanguages.EffectiveCulture)))
            .ToArray();
        var selector = CreateSegmentSelector(counts,
            WorkspaceOptions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), async value =>
            {
                var next = WorkspaceOptions.Normalized();
                next.Count = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                await ChangeWorkspaceConfigurationAsync(next);
            });
        selector.IsEnabled = WorkspaceOptions.Enabled;
        root.Children.Add(selector);
        root.Children.Add(WorkspaceDescription(Strings.Get("WorkspaceCountHint")));
        root.Children.Add(SettingsSectionLabel(Strings.Format("WorkspaceCurrent", WorkspaceNumber)));
        root.Children.Add(WorkspaceDescription(System.IO.Path.GetFileName(_workspace.Paths.Data) + "  ·  " +
            System.IO.Path.GetFileName(_workspace.Paths.Images)));
        if (WorkspaceNumber > 1)
        {
            root.Children.Add(SettingsToggle(Strings.Get("WorkspaceFollowMain"), _workspace.FollowsMain, async () =>
            {
                var next = WorkspaceOptions.Normalized();
                next.FollowMainSettings[WorkspaceNumber] = !_workspace.FollowsMain;
                await ChangeWorkspaceConfigurationAsync(next);
            }));
            root.Children.Add(WorkspaceDescription(Strings.Get("WorkspaceFollowHint")));
        }
        else root.Children.Add(WorkspaceDescription(Strings.Get("WorkspaceMainHint")));

        var cycle = PluginPageButton(Strings.Get("WorkspaceNext"));
        cycle.HorizontalAlignment = HorizontalAlignment.Left;
        cycle.Margin = new Thickness(0, 12, 0, 0);
        cycle.IsEnabled = WorkspaceOptions.Enabled;
        cycle.Click += async (_, _) => await CycleWorkspaceAsync();
        root.Children.Add(cycle);
        return root;
    }

    private static TextBlock WorkspaceDescription(string text) => new()
    {
        Text = text,
        Foreground = TrayWeakTextBrush,
        FontFamily = AppTypography.UiFontFamily,
        FontSize = AppTypography.Scale(11.5),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8)
    };

    private UIElement WithWorkspaceSettingsInheritance(UIElement content)
    {
        if (!FollowsMainWorkspaceSettings || _settingsPage == SettingsPage.Workspaces) return content;
        var root = new StackPanel();
        root.Children.Add(WorkspaceDescription(Strings.Get(_settingsPage == SettingsPage.Plugins
            ? "WorkspacePluginSettingsHint" : "WorkspaceSettingsReadOnly")));
        var manage = PluginPageButton(Strings.Get("WorkspaceSettingsTitle"));
        manage.HorizontalAlignment = HorizontalAlignment.Left;
        manage.Margin = new Thickness(0, 0, 0, 10);
        manage.Click += (_, _) => ShowSettingsWindow(SettingsPage.Workspaces);
        root.Children.Add(manage);
        // Plugin-private configuration/data remains workspace-local, not an AppState setting.
        // Its host enable/disable switch is separately governed by the inherited AppState.
        if (_settingsPage != SettingsPage.Plugins) content.IsEnabled = false;
        root.Children.Add(content);
        return root;
    }
}
