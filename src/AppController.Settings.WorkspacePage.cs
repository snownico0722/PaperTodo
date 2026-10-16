using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;

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
        root.Children.Add(WrapWithHint(
            SettingsToggle(Strings.Get("WorkspaceEnabled"), WorkspaceOptions.Enabled, async () =>
            {
                var next = WorkspaceOptions.Normalized();
                next.Enabled = !next.Enabled;
                await ChangeWorkspaceConfigurationAsync(next);
            }),
            "WorkspaceSettingsDescription"));

        root.Children.Add(SettingsSectionLabelWithHint(Strings.Get("WorkspaceCount"), "WorkspaceCountHint"));
        var counts = Enumerable.Range(2, 4).Select(n =>
            (n.ToString(System.Globalization.CultureInfo.InvariantCulture), n.ToString(UiLanguages.EffectiveCulture)))
            .ToArray();
        var countSelector = CreateSegmentSelector(counts,
            WorkspaceOptions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), async value =>
            {
                var next = WorkspaceOptions.Normalized();
                next.Count = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                await ChangeWorkspaceConfigurationAsync(next);
            });
        countSelector.IsEnabled = WorkspaceOptions.Enabled;
        root.Children.Add(countSelector);

        var currentLabel = Strings.Get("WorkspaceSelectLabel");
        root.Children.Add(WorkspaceNumber == 1
            ? SettingsSectionLabelWithHint(currentLabel, "WorkspaceMainHint")
            : SettingsSectionLabel(currentLabel));
        var workspaceSelector = new ComboBox
        {
            Width = AppTypography.Scale(196),
            Height = AppTypography.FitChrome(28),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = WorkspaceOptions.Enabled
        };
        PaperSelectControl.ApplyAppTheme(workspaceSelector, AppTypography.Scale(12));
        AutomationProperties.SetAutomationId(workspaceSelector, "WorkspaceSelect");
        AutomationProperties.SetName(workspaceSelector, currentLabel);
        for (var number = 1; number <= WorkspaceOptions.Count; number++)
        {
            workspaceSelector.Items.Add(new ComboBoxItem
            {
                Tag = number,
                Content = WorkspaceSlotLabel(number)
            });
        }
        workspaceSelector.SelectedIndex = WorkspaceNumber - 1;
        workspaceSelector.SelectionChanged += async (_, _) =>
        {
            if (_workspaceSwitching || !WorkspaceOptions.Enabled ||
                workspaceSelector.SelectedItem is not ComboBoxItem { Tag: int target } ||
                target == WorkspaceNumber) return;

            // Close the ComboBox popup before switching: the workspace boundary refuses
            // an active mouse capture, and replacing the settings HWND during a click is unsafe.
            workspaceSelector.IsDropDownOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (!_workspaceSwitching && !await SwitchWorkspaceAsync(target))
                RefreshSettingsWindowContent();
        };
        root.Children.Add(workspaceSelector);
        root.Children.Add(WorkspaceDescription(System.IO.Path.GetFileName(_workspace.Paths.Data) + "  ·  " +
            System.IO.Path.GetFileName(_workspace.Paths.Images)));

        if (WorkspaceNumber > 1)
        {
            root.Children.Add(WrapWithHint(
                SettingsToggle(Strings.Get("WorkspaceFollowMain"), _workspace.FollowsMain, async () =>
                {
                    var next = WorkspaceOptions.Normalized();
                    next.FollowMainSettings[WorkspaceNumber] = !_workspace.FollowsMain;
                    await ChangeWorkspaceConfigurationAsync(next);
                }),
                "WorkspaceFollowHint"));
        }

        var cycle = PluginPageButton(Strings.Get("WorkspaceNext"));
        cycle.HorizontalAlignment = HorizontalAlignment.Left;
        cycle.Margin = new Thickness(0, 12, 0, 0);
        cycle.IsEnabled = WorkspaceOptions.Enabled;
        cycle.Click += async (_, _) => await CycleWorkspaceAsync();
        root.Children.Add(cycle);
        return root;
    }

    private static string WorkspaceSlotLabel(int number) => number == 1
        ? Strings.Get("WorkspaceMainSlotLabel")
        : Strings.Format("WorkspaceSlotLabel", number);

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
