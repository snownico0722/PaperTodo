using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private FrameworkElement? _workspaceSettingsPanel;
    private int _workspaceSelectionDraft;

    private string WorkspaceUi(string zh, string en, string ja, string ko) =>
        SettingsSidebarLocalized(zh, en, ja, ko);

    private UIElement BuildWorkspaceSettingsPage()
    {
        var root = new StackPanel { Margin = new Thickness(2, 4, 4, 0) };
        _workspaceSettingsPanel = root;
        root.IsEnabled = !_workspaceSwitching;
        root.Children.Add(SettingsSectionLabel(Strings.Get("WorkspaceSettingsTitle")));
        root.Children.Add(SettingsToggle(Strings.Get("WorkspaceEnabled"), WorkspaceOptions.Enabled, async () =>
        {
            var next = WorkspaceOptions.Normalized();
            next.Enabled = !next.Enabled;
            await ChangeWorkspaceConfigurationAsync(next);
        }));

        var heading = new DockPanel { Margin = new Thickness(0, 12, 0, 5) };
        var create = PluginPageButton(WorkspaceUi("＋ 新建", "＋ New", "＋ 新規", "＋ 새로 만들기"));
        create.HorizontalAlignment = HorizontalAlignment.Right;
        create.IsEnabled = WorkspaceOptions.Enabled && WorkspaceOptions.SlotNumbers.Count < 5;
        DockPanel.SetDock(create, Dock.Right);
        heading.Children.Add(create);
        heading.Children.Add(SettingsFieldLabel(Strings.Get("WorkspaceSettingsTitle")));
        root.Children.Add(heading);
        create.Click += async (_, _) =>
        {
            var options = WorkspaceOptions.Normalized();
            // Legacy count reductions may have left hidden files behind. New means new:
            // never overwrite or revive that data without an explicit user action.
            var slot = Enumerable.Range(2, 4).FirstOrDefault(number =>
                !options.ContainsSlot(number) && !_workspaceCatalog.SlotHasData(number));
            if (slot == 0)
            {
                ReportWorkspaceError(WorkspaceUi("没有可安全新建的工作区编号。已有的历史文件不会被覆盖。",
                    "No free workspace slot; existing historical files are preserved.",
                    "空き番号がありません。既存の履歴ファイルは保持されます。",
                    "사용 가능한 작업 공간 번호가 없습니다. 기존 파일은 보존됩니다."));
                return;
            }
            options.Slots = options.SlotNumbers.Append(slot).OrderBy(number => number).ToList();
            options.Count = options.Slots.Max();
            if (await ChangeWorkspaceConfigurationAsync(options))
            {
                _workspaceSelectionDraft = slot;
                RefreshSettingsWindowContent();
            }
        };

        var selected = WorkspaceOptions.ContainsSlot(_workspaceSelectionDraft)
            ? _workspaceSelectionDraft : WorkspaceNumber;
        foreach (var number in WorkspaceOptions.SlotNumbers)
        {
            var slot = number;
            var row = new StackPanel
            {
                Margin = new Thickness(0, 4, 0, 4),
                Background = Brushes.Transparent
            };
            var line = new DockPanel();
            var label = WorkspaceOptions.DisplayName(slot) + "  #" + slot;
            var rename = PluginPageButton(WorkspaceUi("重命名", "Rename", "名前変更", "이름 변경"));
            rename.MinWidth = 60;
            rename.Margin = new Thickness(3, 0, 0, 0);
            rename.IsEnabled = WorkspaceOptions.Enabled;
            DockPanel.SetDock(rename, Dock.Right);
            line.Children.Add(rename);
            if (slot > 1)
            {
                var delete = PluginPageButton(WorkspaceUi("删除", "Delete", "削除", "삭제"));
                delete.MinWidth = 52;
                delete.IsEnabled = WorkspaceOptions.Enabled;
                delete.Margin = new Thickness(3, 0, 0, 0);
                DockPanel.SetDock(delete, Dock.Right);
                line.Children.Add(delete);
                delete.Click += async (_, _) => await DeleteWorkspaceFromSettingsAsync(slot);
            }
            var choose = PluginPageButton(label);
            choose.HorizontalAlignment = HorizontalAlignment.Stretch;
            choose.HorizontalContentAlignment = HorizontalAlignment.Left;
            choose.FontWeight = selected == slot ? FontWeights.SemiBold : FontWeights.Normal;
            choose.BorderBrush = selected == slot ? Theme.ActiveBrush : TrayBorderBrush;
            choose.IsEnabled = WorkspaceOptions.Enabled;
            AutomationProperties.SetAutomationId(choose, "WorkspaceSlot" + slot);
            AutomationProperties.SetName(choose, label);
            choose.Click += (_, _) =>
            {
                _workspaceSelectionDraft = slot;
                RefreshSettingsWindowContent();
            };
            line.Children.Add(choose);
            row.Children.Add(line);

            rename.Click += (_, _) =>
            {
                var editor = new TextBox
                {
                    Text = WorkspaceOptions.DisplayName(slot),
                    Margin = new Thickness(0, 4, 0, 4),
                    MinWidth = 160,
                    MaxLength = 64
                };
                var save = PluginPageButton(WorkspaceUi("保存名称", "Save name", "名前を保存", "이름 저장"));
                var editRow = new DockPanel();
                DockPanel.SetDock(save, Dock.Right);
                editRow.Children.Add(save);
                editRow.Children.Add(editor);
                row.Children.Add(editRow);
                rename.IsEnabled = false;
                async void Commit()
                {
                    if (string.IsNullOrWhiteSpace(editor.Text)) return;
                    var next = WorkspaceOptions.Normalized();
                    next.Names[slot] = editor.Text.Trim();
                    await ChangeWorkspaceConfigurationAsync(next);
                    RefreshSettingsWindowContent();
                }
                save.Click += (_, _) => Commit();
                editor.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                    if (e.Key == Key.Escape) { row.Children.Remove(editRow); rename.IsEnabled = true; e.Handled = true; }
                };
                editor.SelectAll();
                editor.Focus();
            };

            if (slot > 1)
            {
                var follow = SettingsToggle(Strings.Get("WorkspaceFollowMain"),
                    WorkspaceOptions.FollowsMain(slot), async () =>
                    {
                        var next = WorkspaceOptions.Normalized();
                        next.FollowMainSettings[slot] = !next.FollowsMain(slot);
                        await ChangeWorkspaceConfigurationAsync(next);
                        RefreshSettingsWindowContent();
                    });
                follow.Margin = new Thickness(10, 6, 0, 1);
                follow.IsEnabled = WorkspaceOptions.Enabled;
                row.Children.Add(follow);
            }
            root.Children.Add(new Border
            {
                BorderBrush = selected == slot ? Theme.ActiveBrush : TrayBorderBrush,
                BorderThickness = new Thickness(selected == slot ? 1.5 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 8, 9, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Child = row
            });
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 2)
        };
        var cancel = PluginPageButton(Strings.Get("CommonCancel"));
        cancel.Click += (_, _) => { _workspaceSelectionDraft = WorkspaceNumber; RefreshSettingsWindowContent(); };
        actions.Children.Add(cancel);
        var confirm = PluginPageButton(WorkspaceUi("确定切换", "Switch", "切り替える", "전환"));
        confirm.Margin = new Thickness(8, 0, 0, 0);
        confirm.IsEnabled = WorkspaceOptions.Enabled && selected != WorkspaceNumber;
        confirm.Click += async (_, _) =>
        {
            if (selected == WorkspaceNumber) return;
            _workspaceSelectionDraft = 0;
            if (!await SwitchWorkspaceAsync(selected)) RefreshSettingsWindowContent();
        };
        actions.Children.Add(confirm);
        root.Children.Add(actions);

        root.Children.Add(SettingsSectionLabel(WorkspaceUi(
            "顶栏快捷", "Top-bar shortcut", "上部バーのショートカット", "상단 표시줄 바로 가기")));
        root.Children.Add(SettingsToggle(WorkspaceUi(
                "顶栏快捷切换显示具体名称", "Choose workspaces by name in the top bar",
                "上部バーで名前を表示して選択", "상단 표시줄에서 이름으로 선택"),
            WorkspaceOptions.TopBarNamePicker, async () =>
            {
                var next = WorkspaceOptions.Normalized();
                next.TopBarNamePicker = !next.TopBarNamePicker;
                await ChangeWorkspaceConfigurationAsync(next);
                RefreshSettingsWindowContent();
            }));

        return root;
    }

    private async System.Threading.Tasks.Task DeleteWorkspaceFromSettingsAsync(int slot)
    {
        if (slot <= 1 || !WorkspaceOptions.ContainsSlot(slot) || _workspaceSwitching) return;
        var owner = _settingsWindow;
        if (owner == null || !PaperNoticeDialog.ShowChoice(owner,
                WorkspaceUi("删除工作区", "Delete workspace", "ワークスペースを削除", "작업 공간 삭제"),
                WorkspaceUi("将永久删除此工作区的纸片、图片和插件数据。确定删除吗？",
                    "Permanently delete this workspace's papers, images and plugin data?",
                    "この作業領域のメモ、画像、プラグインデータを完全に削除しますか？",
                    "이 작업 공간의 메모, 이미지 및 플러그인 데이터를 영구 삭제하시겠습니까?"),
                Strings.Get("CommonCancel"),
                WorkspaceUi("删除", "Delete", "削除", "삭제")))
            return;

        if (WorkspaceNumber == slot && !await SwitchWorkspaceAsync(1)) return;
        var next = WorkspaceOptions.Normalized();
        next.Slots = next.SlotNumbers.Where(number => number != slot).ToList();
        next.Count = next.Slots.Max();
        next.Names.Remove(slot);
        next.FollowMainSettings.Remove(slot);
        // The workspace is gone from the menu once its catalog update succeeds.
        // External file locks may leave files behind; report the cleanup failure
        // without restoring a deleted workspace or changing its active successor.
        if (!await ChangeWorkspaceConfigurationAsync(next)) return;
        if (_workspaceSelectionDraft == slot) _workspaceSelectionDraft = 0;
        try { _workspaceCatalog.DeleteInactiveSlotFiles(slot); }
        catch (Exception ex) { ReportWorkspaceError(ex.GetBaseException().Message); }
        RefreshSettingsWindowContent();
    }

    private UIElement WithWorkspaceSettingsInheritance(UIElement content)
    {
        if (!FollowsMainWorkspaceSettings || _settingsPage == SettingsPage.Workspaces) return content;
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = Strings.Get(_settingsPage == SettingsPage.Plugins
                ? "WorkspacePluginSettingsHint" : "WorkspaceSettingsReadOnly"),
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 0, 8)
        });
        var manage = PluginPageButton(Strings.Get("WorkspaceSettingsTitle"));
        manage.Margin = new Thickness(0, 0, 0, 10);
        manage.HorizontalAlignment = HorizontalAlignment.Left;
        manage.Click += (_, _) => ShowSettingsWindow(SettingsPage.Workspaces);
        root.Children.Add(manage);
        // Plugin-private settings and their actions are workspace-local; host enablement,
        // global hotkeys and public software settings remain read-only.
        if (_settingsPage != SettingsPage.Plugins) content.IsEnabled = false;
        root.Children.Add(content);
        return root;
    }
}
