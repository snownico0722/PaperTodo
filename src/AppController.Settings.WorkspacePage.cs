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
    // One active inline rename per settings window. The same window-wide pointer
    // handler cancels it whether the user clicks another card, the sidebar, or
    // the empty background; the editor and the checkmark remain interactive.
    private Action<DependencyObject?>? _workspaceRenameOutsideClick;

    private void CancelWorkspaceRenameOnPointer(DependencyObject? hit) =>
        _workspaceRenameOutsideClick?.Invoke(hit);

    private string WorkspaceUi(string zh, string en, string ja, string ko) =>
        SettingsSidebarLocalized(zh, en, ja, ko);

    private UIElement BuildWorkspaceSettingsPage()
    {
        CancelWorkspaceRenameOnPointer(null);
        var root = new StackPanel { Margin = new Thickness(2, 4, 4, 0) };
        _workspaceSettingsPanel = root;
        root.IsEnabled = !_workspaceSwitching;
        var enabled = WorkspaceOptions.Enabled;
        var title = SettingsSectionLabel(Strings.Get("WorkspaceSettingsTitle"));
        title.Opacity = enabled ? 1 : 0.5;
        root.Children.Add(title);
        // Keep only this toggle active when workspaces are disabled.
        root.Children.Add(SettingsToggle(Strings.Get("WorkspaceEnabled"), enabled, async () =>
        {
            var next = WorkspaceOptions.Normalized();
            next.Enabled = !next.Enabled;
            await ChangeWorkspaceConfigurationAsync(next);
        }));
        var optionsPanel = new StackPanel
        {
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0.5
        };
        root.Children.Add(optionsPanel);

        var heading = new DockPanel { Margin = new Thickness(0, 12, 0, 5) };
        var create = SettingsTextButton(WorkspaceUi("＋ 新建", "＋ New", "＋ 新規", "＋ 새로 만들기"));
        create.HorizontalAlignment = HorizontalAlignment.Right;
        create.IsEnabled = WorkspaceOptions.Enabled && WorkspaceOptions.SlotNumbers.Count < 5;
        DockPanel.SetDock(create, Dock.Right);
        heading.Children.Add(create);
        heading.Children.Add(SettingsFieldLabel(Strings.Get("WorkspaceSettingsTitle")));
        optionsPanel.Children.Add(heading);
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
            var label = WorkspaceOptions.DisplayName(slot);
            var card = new Border
            {
                BorderBrush = selected == slot ? Theme.ActiveBrush : TrayBorderBrush,
                BorderThickness = new Thickness(selected == slot ? 1.5 : 1),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 5),
                MinHeight = 37,
                Cursor = Cursors.Hand,
                IsEnabled = WorkspaceOptions.Enabled
            };
            AutomationProperties.SetAutomationId(card, "WorkspaceSlot" + slot);
            AutomationProperties.SetName(card, label + " #" + slot);

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new TextBlock
            {
                Text = label,
                FontSize = AppTypography.Scale(12.5),
                FontWeight = selected == slot ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = TrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = AppTypography.Scale(154),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var editor = new TextBox
            {
                Text = label,
                Width = AppTypography.Scale(154),
                MaxLength = 64,
                Height = AppTypography.FitChrome(28),
                FontSize = AppTypography.Scale(12),
                Foreground = TrayTextBrush,
                CaretBrush = TrayTextBrush,
                Background = Brushes.Transparent,
                BorderBrush = TrayBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = BuildSettingsTextBoxStyle(),
                Visibility = Visibility.Collapsed
            };
            var numberLabel = new TextBlock
            {
                Text = "#" + slot.ToString(UiLanguages.EffectiveCulture),
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(11.5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 3, 0)
            };
            var rename = PluginPageButton("✎");
            rename.MinWidth = 27;
            rename.Width = 27;
            rename.Height = 25;
            rename.Padding = new Thickness(3, 1, 3, 1);
            rename.FontFamily = AppTypography.SymbolFontFamily;
            rename.VerticalAlignment = VerticalAlignment.Center;
            rename.ToolTip = WorkspaceUi("编辑名称", "Edit name", "名前を編集", "이름 편집");
            AutomationProperties.SetAutomationId(rename, "WorkspaceRename" + slot);
            AutomationProperties.SetName(rename, WorkspaceUi("编辑工作区名称", "Edit workspace name",
                "ワークスペース名を編集", "작업 공간 이름 편집"));
            nameRow.Children.Add(name);
            nameRow.Children.Add(editor);
            nameRow.Children.Add(numberLabel);
            nameRow.Children.Add(rename);
            Grid.SetColumn(nameRow, 0);
            line.Children.Add(nameRow);

            var editing = false;
            Action<DependencyObject?>? outsideClick = null;
            void ExitEditing()
            {
                editing = false;
                name.Visibility = Visibility.Visible;
                editor.Visibility = Visibility.Collapsed;
                rename.Content = "✎";
                rename.ToolTip = WorkspaceUi("编辑名称", "Edit name", "名前を編集", "이름 편집");
                rename.IsEnabled = true;
                if (ReferenceEquals(_workspaceRenameOutsideClick, outsideClick))
                    _workspaceRenameOutsideClick = null;
            }
            async void SaveName()
            {
                var value = editor.Text.Trim();
                if (value.Length == 0) { editor.Focus(); return; }
                if (value == WorkspaceOptions.DisplayName(slot)) { ExitEditing(); return; }
                rename.IsEnabled = false;
                var next = WorkspaceOptions.Normalized();
                next.Names[slot] = value;
                if (!await ChangeWorkspaceConfigurationAsync(next) && editing)
                {
                    // A rejected save must not refocus an editor already dismissed
                    // by an outside click or by page navigation.
                    rename.IsEnabled = true;
                    editor.Focus();
                }
                // A successful save refreshes the settings page through the catalog update.
            }
            outsideClick = hit =>
            {
                if (!IsWithinElement(hit, editor) && !IsWithinElement(hit, rename))
                    ExitEditing();
            };
            rename.Click += (_, _) =>
            {
                if (editing) { SaveName(); return; }
                // A second editor replaces the first draft rather than saving it.
                CancelWorkspaceRenameOnPointer(null);
                editing = true;
                editor.Text = WorkspaceOptions.DisplayName(slot);
                name.Visibility = Visibility.Collapsed;
                editor.Visibility = Visibility.Visible;
                rename.Content = "✓";
                rename.ToolTip = WorkspaceUi("保存名称", "Save name", "名前を保存", "이름 저장");
                _workspaceRenameOutsideClick = outsideClick;
                editor.SelectAll();
                editor.Focus();
            };
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { SaveName(); e.Handled = true; }
                else if (e.Key == Key.Escape) { ExitEditing(); e.Handled = true; }
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (slot > 1)
            {
                var follow = SettingsToggle(Strings.Get("WorkspaceFollowMain"),
                    WorkspaceOptions.FollowsMain(slot), async () =>
                    {
                        var next = WorkspaceOptions.Normalized();
                        next.FollowMainSettings[slot] = !next.FollowsMain(slot);
                        await ChangeWorkspaceConfigurationAsync(next);
                    });
                follow.Margin = new Thickness(9, 0, 7, 0);
                follow.FontSize = AppTypography.Scale(11.5);
                follow.VerticalAlignment = VerticalAlignment.Center;
                controls.Children.Add(follow);

                var delete = PluginPageButton(WorkspaceUi("删除", "Delete", "削除", "삭제"));
                delete.MinWidth = 48;
                delete.Height = 25;
                delete.Padding = new Thickness(5, 1, 5, 1);
                delete.VerticalAlignment = VerticalAlignment.Center;
                delete.Click += async (_, _) => await DeleteWorkspaceFromSettingsAsync(slot);
                controls.Children.Add(delete);
            }
            Grid.SetColumn(controls, 1);
            line.Children.Add(controls);
            card.Child = line;

            // The whole card selects its workspace, except its independent controls.
            // Inspect the routed hit to avoid selection on rename, delete or follow clicks.
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (_workspaceSwitching) return;
                var hit = e.OriginalSource as DependencyObject;
                while (hit != null && !ReferenceEquals(hit, card))
                {
                    if (hit is System.Windows.Controls.Primitives.ButtonBase or TextBox) return;
                    hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
                }
                if (!ReferenceEquals(hit, card)) return;
                _workspaceSelectionDraft = slot;
                e.Handled = true;
                RefreshSettingsWindowContent();
            };
            optionsPanel.Children.Add(card);
        }

        var confirm = SettingsTextButton(WorkspaceUi(
            "确认切换", "Confirm switch", "切り替えを確認", "전환 확인"));
        confirm.HorizontalAlignment = HorizontalAlignment.Right;
        confirm.Margin = new Thickness(0, 8, 0, 2);
        confirm.IsEnabled = WorkspaceOptions.Enabled && selected != WorkspaceNumber;
        confirm.Click += async (_, _) =>
        {
            if (selected == WorkspaceNumber) return;
            _workspaceSelectionDraft = 0;
            if (!await SwitchWorkspaceAsync(selected)) RefreshSettingsWindowContent();
        };
        optionsPanel.Children.Add(confirm);

        optionsPanel.Children.Add(SettingsSectionLabel(WorkspaceUi(
            "顶栏快捷", "Top-bar shortcut", "上部バーのショートカット", "상단 표시줄 바로 가기")));
        optionsPanel.Children.Add(SettingsToggle(WorkspaceUi(
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
        var manage = SettingsTextButton(Strings.Get("WorkspaceSettingsTitle"));
        manage.Margin = new Thickness(0, 0, 0, 10);
        manage.HorizontalAlignment = HorizontalAlignment.Left;
        manage.Click += (_, _) => ShowSettingsWindow(SettingsPage.Workspaces);
        root.Children.Add(manage);
        // Following the main workspace makes the entire settings page visibly read-only.
        // Keep only the workspace-management link enabled so follow mode can be changed.
        content.IsEnabled = false;
        content.Opacity = 0.5;
        root.Children.Add(content);
        return root;
    }
}
