using System;
using System.Globalization;
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
    private Button? _workspaceSwitchConfirmation;
    private int _workspaceSelectionDraft;
    // One active inline rename per settings window. The same window-wide pointer
    // handler cancels it whether the user clicks another card, the sidebar, or
    // the empty background; the editor and the checkmark remain interactive.
    private Action<DependencyObject?>? _workspaceRenameOutsideClick;

    private void CancelWorkspaceRenameOnPointer(DependencyObject? hit) =>
        _workspaceRenameOutsideClick?.Invoke(hit);

    private string WorkspaceUi(string zh, string en, string ja, string ko) =>
        SettingsSidebarLocalized(zh, en, ja, ko);

    private void RefreshWorkspaceSwitchConfirmation()
    {
        if (_workspaceSwitchConfirmation is { } button)
            button.IsEnabled = !IsPaperExportActive && WorkspaceOptions.Enabled &&
                WorkspaceOptions.ContainsSlot(_workspaceSelectionDraft) && _workspaceSelectionDraft != WorkspaceNumber;
    }

    private UIElement BuildWorkspaceSettingsPage()
    {
        CancelWorkspaceRenameOnPointer(null);
        var root = new Grid { Margin = new Thickness(2, 4, 6, 0) };
        _workspaceSettingsPanel = root;
        root.IsEnabled = !_workspaceSwitching;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var workspaceColumn = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var dataColumn = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        var separator = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 10, 0, 4),
            Background = TrayBorderBrush,
            Opacity = 0.65
        };
        Grid.SetColumn(workspaceColumn, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(dataColumn, 2);
        root.Children.Add(workspaceColumn);
        root.Children.Add(separator);
        root.Children.Add(dataColumn);

        var enabled = WorkspaceOptions.Enabled;
        workspaceColumn.Children.Add(SettingsSectionLabelWithHint(Strings.Get("WorkspaceSettingsTitle"), "WorkspaceSettingsDescription"));
        // Only workspace controls depend on this switch, not the adjacent data column.
        workspaceColumn.Children.Add(SettingsToggle(Strings.Get("WorkspaceEnabled"), enabled, async () =>
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
        workspaceColumn.Children.Add(optionsPanel);

        optionsPanel.Children.Add(WrapWithHint(WrappingSettingsToggle(
            Strings.Get("WorkspaceTopBarNamePicker"), WorkspaceOptions.TopBarNamePicker, async () =>
            {
                var next = WorkspaceOptions.Normalized();
                next.TopBarNamePicker = !next.TopBarNamePicker;
                await ChangeWorkspaceConfigurationAsync(next);
                RefreshSettingsWindowContent();
            }), "TipWorkspaceTopBarNamePicker"));

        var heading = new DockPanel { Margin = new Thickness(0, 12, 0, 5) };
        var create = SettingsTextButton(WorkspaceUi("新建", "New", "新規", "새로 만들기"));
        create.Padding = new Thickness(14, 0, 14, 0);
        create.HorizontalAlignment = HorizontalAlignment.Right;
        create.IsEnabled = WorkspaceOptions.Enabled && WorkspaceOptions.SlotNumbers.Count < 5;
        DockPanel.SetDock(create, Dock.Right);
        heading.Children.Add(create);
        heading.Children.Add(SettingsFieldLabel(Strings.Get("WorkspaceSettingsTitle")));
        optionsPanel.Children.Add(heading);
        create.Click += async (_, _) =>
        {
            var slot = await CreateWorkspaceAsync();
            if (slot == 0) return;
            _workspaceSelectionDraft = slot;
            RefreshSettingsWindowContent();
        };

        var selected = WorkspaceOptions.ContainsSlot(_workspaceSelectionDraft)
            ? _workspaceSelectionDraft : WorkspaceNumber;
        foreach (var slot in WorkspaceOptions.SlotNumbers)
            optionsPanel.Children.Add(BuildWorkspaceSlotCard(slot, selected == slot));

        var confirm = SettingsTextButton(WorkspaceUi(
            "确认切换", "Confirm switch", "切り替えを確認", "전환 확인"));
        confirm.Padding = new Thickness(14, 0, 14, 0);
        confirm.HorizontalAlignment = HorizontalAlignment.Right;
        confirm.Margin = new Thickness(0, 8, 0, 2);
        _workspaceSwitchConfirmation = confirm;
        RefreshWorkspaceSwitchConfirmation();
        confirm.Click += async (_, _) =>
        {
            if (selected == WorkspaceNumber) return;
            _workspaceSelectionDraft = 0;
            if (!await SwitchWorkspaceAsync(selected)) RefreshSettingsWindowContent();
        };
        optionsPanel.Children.Add(confirm);

        if (FollowsMainWorkspaceSettings)
            dataColumn.Children.Add(new TextBlock
            {
                Text = Strings.Get("WorkspaceSettingsReadOnly"),
                Foreground = TrayWeakTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        dataColumn.Children.Add(BuildSettingsLiveRegion("workspace.data", BuildWorkspaceDataSettings));
        dataColumn.Children.Add(BuildSettingsLiveRegion("workspace.mcp", BuildMcpSettings));
        return root;
    }

    private UIElement BuildWorkspaceDataSettings()
    {
        var content = new StackPanel
        {
            IsEnabled = !FollowsMainWorkspaceSettings,
            Opacity = FollowsMainWorkspaceSettings ? 0.55 : 1
        };
        content.Children.Add(SettingsSectionLabel(Strings.Get("SettingsDataProcessing")));
        content.Children.Add(SettingsSectionLabelWithHint(
            Strings.Get("SettingsDataMergeConflicts"), "TipDataMergeConflicts"));
        var selector = CreateSettingsSelect(
            [
                (DataMergeConflictPolicies.Ask, Strings.Get("DataMergeConflictAsk")),
                (DataMergeConflictPolicies.External, Strings.Get("DataMergeConflictExternal")),
                (DataMergeConflictPolicies.Internal, Strings.Get("DataMergeConflictInternal"))
            ],
            DataMergeConflictPolicies.Normalize(State.DataMergeConflictPolicy),
            value => SetSettingFromUi("general.data_merge_conflicts", value));
        if (selector is FrameworkElement control)
        {
            control.Width = AppTypography.Scale(166);
            control.HorizontalAlignment = HorizontalAlignment.Left;
            control.Margin = new Thickness(2, 4, 0, 2);
        }
        content.Children.Add(selector);
        return content;
    }

    private UIElement BuildMcpSettings()
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 5),
            Margin = new Thickness(0, 1, 0, 3)
        };
        var content = new StackPanel();
        content.Children.Add(SettingsSectionLabel(Strings.Get("LabsMcp")));
        // Inherited settings are read-only; status and copy actions stay usable.
        var permissions = new StackPanel
        {
            IsEnabled = !FollowsMainWorkspaceSettings,
            Opacity = FollowsMainWorkspaceSettings ? 0.55 : 1
        };
        content.Children.Add(permissions);
        permissions.Children.Add(WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("LabsMcpEnable"),
                State.McpEnabled,
                ToggleMcpEnabled),
            "TipLabsMcpEnable"));
        var permissionChoices = new StackPanel
        {
            IsEnabled = State.McpEnabled,
            Opacity = State.McpEnabled ? 1.0 : 0.55
        };
        permissions.Children.Add(permissionChoices);
        var blankWrites = WrappingSettingsToggle(
            Strings.Get("LabsMcpBlankWrites"),
            State.McpAllowBlankWrites || State.McpAllowFullWrites,
            ToggleMcpBlankWrites);
        blankWrites.IsEnabled = !State.McpAllowFullWrites;
        permissionChoices.Children.Add(WrapWithHint(
            blankWrites,
            "TipLabsMcpBlankWrites"));
        permissionChoices.Children.Add(WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("LabsMcpFullWrites"),
                State.McpAllowFullWrites,
                ToggleMcpFullWrites),
            "TipLabsMcpFullWrites"));
        var directDeletes = WrappingSettingsToggle(
            Strings.Get("LabsMcpDeletes"),
            State.McpAllowDeletes,
            ToggleMcpDeletes);
        if (State.McpAllowDeletes)
        {
            directDeletes.Foreground = Theme.DangerBrush;
        }
        permissionChoices.Children.Add(WrapWithHint(
            directDeletes,
            "TipLabsMcpDeletes"));

        var status = new TextBlock
        {
            Text = State.McpEnabled
                ? Strings.Get("LabsMcpStatusReady")
                : Strings.Get("LabsMcpStatusDisabled"),
            Foreground = State.McpEnabled
                ? Theme.ActiveBrush
                : TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            Margin = new Thickness(2, 8, 2, 0)
        };
        content.Children.Add(status);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0)
        };
        var copyJson = SettingsTextButton(
            Strings.Get("LabsMcpCopyJson"));
        var copySkill = SettingsTextButton(
            Strings.Get("LabsMcpCopyAiSkill"));
        copySkill.Margin = new Thickness(8, 0, 0, 0);
        var feedback = new TextBlock
        {
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0)
        };
        async Task CopyAsync(string text)
        {
            copySkill.IsEnabled = copyJson.IsEnabled = false;
            feedback.Text = string.Empty;
            try
            {
                var copied = await ClipboardHelper.TrySetTextAsync(text);
                feedback.Text = Strings.Get(copied ? "LabsMcpCopied" : "LabsMcpCopyFailed");
            }
            finally
            {
                copySkill.IsEnabled = copyJson.IsEnabled = true;
            }
        }
        copyJson.Click += async (_, _) => await CopyAsync(BuildJsonMcpConfiguration());
        copySkill.Click += async (_, _) => await CopyAsync(BuildAiMcpSkill());
        actions.Children.Add(copyJson);
        actions.Children.Add(copySkill);
        actions.Children.Add(feedback);
        content.Children.Add(actions);
        card.Child = content;
        return card;
    }

    // 15 fullwidth characters or 30 ASCII/halfwidth characters for new names.
    // Existing persisted names are never shortened merely by opening Settings.
    private static int WorkspaceNameUnits(string value) => EdgeCapsuleLayout.DisplayWidth(value);

    private static string LimitWorkspaceName(string value, int maxHalfWidthUnits = 30)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            width += EdgeCapsuleLayout.DisplayWidth((string)elements.Current);
            if (width > maxHalfWidthUnits) return value[..elements.ElementIndex];
        }
        return value;
    }

    private Border BuildWorkspaceSlotCard(int slot, bool isSelected)
    {
        var label = WorkspaceOptions.DisplayName(slot);
        var card = new Border
        {
            BorderBrush = isSelected ? Theme.ActiveBrush : TrayBorderBrush,
            BorderThickness = new Thickness(isSelected ? 1.5 : 1),
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

        Grid? trailingActions = null;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumnSpan(line, 2);
        content.Children.Add(line);
        var name = new TextBlock
        {
            Text = label,
            FontSize = AppTypography.Scale(12.5),
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = TrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        var editor = new TextBox
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxLength = 64,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = AppTypography.FitChrome(28),
            FontSize = AppTypography.Scale(12),
            Foreground = TrayTextBrush,
            CaretBrush = TrayTextBrush,
            Background = Brushes.Transparent,
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 1, 2, 1),
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
            Margin = new Thickness(4, 0, 1, 0)
        };
        var pencil = Geometry.Parse("M1 11 L2 7.5 L8.8 0.7 Q9.3 0.2 9.8 0.7 L11.3 2.2 Q11.8 2.7 11.3 3.2 L4.5 10 Z M7.4 2.1 L9.9 4.6");
        var check = Geometry.Parse("M0.8 6 L4.5 9.7 L11.2 2.3");
        var rename = PluginPageButton(string.Empty);
        var renameIcon = new System.Windows.Shapes.Path
        {
            Width = 12, Height = 12, Data = pencil, Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(AppTypography.ScaleFactor, AppTypography.ScaleFactor),
            StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        renameIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = rename });
        rename.Content = renameIcon;
        rename.VerticalContentAlignment = VerticalAlignment.Center;
        rename.HorizontalContentAlignment = HorizontalAlignment.Center;
        rename.MinWidth = 22;
        rename.Width = 22;
        rename.Height = 25;
        rename.Padding = new Thickness(3, 1, 3, 1);
        rename.VerticalAlignment = VerticalAlignment.Center;
        rename.ToolTip = WorkspaceUi("编辑名称", "Edit name", "名前を編集", "이름 편집");
        AutomationProperties.SetAutomationId(rename, "WorkspaceRename" + slot);
        AutomationProperties.SetName(rename, WorkspaceUi("编辑工作区名称", "Edit workspace name",
            "ワークスペース名を編集", "작업 공간 이름 편집"));
        var renameActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        renameActions.Children.Add(numberLabel);
        renameActions.Children.Add(rename);
        line.Children.Add(name);
        line.Children.Add(editor);
        line.Children.Add(renameActions);
        var editing = false;
        var previousDraft = label;
        var correctingDraft = false;
        editor.TextChanged += (_, _) =>
        {
            if (!editing || correctingDraft) return;
            var current = editor.Text;
            if (WorkspaceNameUnits(current) <= 30) { previousDraft = current; return; }
            // Limit the inserted text, not the old suffix: typing at the beginning of
            // a full name must never silently delete characters at the end.
            var prefix = 0;
            while (prefix < current.Length && prefix < previousDraft.Length &&
                   current[prefix] == previousDraft[prefix]) prefix++;
            var suffix = 0;
            while (suffix < current.Length - prefix && suffix < previousDraft.Length - prefix &&
                   current[^(suffix + 1)] == previousDraft[^(suffix + 1)]) suffix++;
            var start = current[..prefix];
            var tail = current[(current.Length - suffix)..];
            var inserted = current[prefix..(current.Length - suffix)];
            var remaining = Math.Max(0, 30 - WorkspaceNameUnits(start + tail));
            var allowed = LimitWorkspaceName(inserted, remaining);
            var limited = start + allowed + tail;
            if (limited == current) { previousDraft = current; return; }
            correctingDraft = true;
            editor.Text = limited;
            editor.CaretIndex = prefix + allowed.Length;
            correctingDraft = false;
            previousDraft = limited;
        };
        Action<DependencyObject?>? outsideClick = null;
        void ExitEditing()
        {
            editing = false;
            name.Visibility = Visibility.Visible;
            editor.Visibility = Visibility.Collapsed;
            renameIcon.Data = pencil;
            rename.ToolTip = WorkspaceUi("编辑名称", "Edit name", "名前を編集", "이름 편집");
            rename.IsEnabled = true;
            UpdateCardLayout();
            if (ReferenceEquals(_workspaceRenameOutsideClick, outsideClick))
                _workspaceRenameOutsideClick = null;
        }
        async void SaveName()
        {
            var value = editor.Text.Trim();
            if (value.Length == 0) { editor.Focus(); return; }
            if (value == WorkspaceOptions.DisplayName(slot)) { ExitEditing(); return; }
            value = LimitWorkspaceName(value);
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
            editor.Text = WorkspaceOptions.DisplayName(slot);
            previousDraft = editor.Text;
            editing = true;
            name.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            renameIcon.Data = check;
            rename.ToolTip = WorkspaceUi("保存名称", "Save name", "名前を保存", "이름 저장");
            _workspaceRenameOutsideClick = outsideClick;
            editor.SelectAll();
            editor.Focus();
            UpdateCardLayout();
        };
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SaveName(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ExitEditing(); e.Handled = true; }
        };

        if (slot > 1)
        {
            // Keep actions right-aligned: share the name row when there is room,
            // and only wrap to the next row when the content does not fit.
            var controls = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var follow = WrappingSettingsToggle(Strings.Get("WorkspaceFollowMain"),
                WorkspaceOptions.FollowsMain(slot), async () =>
                {
                    var next = WorkspaceOptions.Normalized();
                    next.FollowMainSettings[slot] = !next.FollowsMain(slot);
                    await ChangeWorkspaceConfigurationAsync(next);
                });
            follow.FontSize = AppTypography.Scale(11.5);
            follow.VerticalAlignment = VerticalAlignment.Center;
            var followWithHint = WrapWithHint(follow, "WorkspaceFollowHint");
            if (followWithHint is FrameworkElement followRow)
                followRow.Margin = new Thickness(0, 0, 3, 0);
            Grid.SetColumn(followWithHint, 0);
            controls.Children.Add(followWithHint);

            var delete = PluginPageButton(WorkspaceUi("删除", "Delete", "削除", "삭제"));
            delete.VerticalContentAlignment = VerticalAlignment.Center;
            delete.MinWidth = 28;
            delete.Height = 25;
            delete.Padding = new Thickness(3, 1, 3, 1);
            delete.VerticalAlignment = VerticalAlignment.Center;
            delete.Click += async (_, _) => await DeleteWorkspaceFromSettingsAsync(slot);
            Grid.SetColumn(delete, 1);
            controls.Children.Add(delete);
            Grid.SetRow(controls, 1);
            Grid.SetColumnSpan(controls, 2);
            trailingActions = controls;
            content.Children.Add(controls);
        }
        card.Child = content;

        void UpdateCardLayout()
        {
            var available = Math.Max(1, card.ActualWidth - card.Padding.Left - card.Padding.Right
                - card.BorderThickness.Left - card.BorderThickness.Right);
            name.MaxWidth = available;
            editor.MaxWidth = available;
            if (trailingActions == null || card.ActualWidth <= 0) return;

            // Evaluate natural content width, not the current wrapped line.
            line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            trailingActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var inline = line.DesiredSize.Width + trailingActions.DesiredSize.Width <= available;
            var targetRow = inline ? 0 : 1;
            if (Grid.GetRow(trailingActions) == targetRow) return;
            Grid.SetRow(trailingActions, targetRow);
            Grid.SetColumn(trailingActions, inline ? 1 : 0);
            Grid.SetColumnSpan(trailingActions, inline ? 1 : 2);
            Grid.SetColumnSpan(line, inline ? 1 : 2);
            trailingActions.Margin = inline ? new Thickness(0) : new Thickness(0, 3, 0, 0);
        }

        card.SizeChanged += (_, _) => UpdateCardLayout();
        editor.TextChanged += (_, _) => UpdateCardLayout();

        // The whole card selects its workspace, except its independent controls.
        // Inspect the routed hit to avoid selection on rename, delete or follow clicks.
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (_workspaceSwitching) return;
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null && !ReferenceEquals(hit, card))
            {
                if (hit is System.Windows.Controls.Primitives.ButtonBase or TextBox) return;
                if (hit is Border help && help.Cursor == Cursors.Help) return;
                hit = GetElementParent(hit);
            }
            if (!ReferenceEquals(hit, card)) return;
            _workspaceSelectionDraft = slot;
            e.Handled = true;
            RefreshSettingsWindowContent();
        };
        return card;
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
        manage.Padding = new Thickness(14, 0, 14, 0);
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
