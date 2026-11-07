using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement BuildSettingsSidebarGeneralPage()
    {
        var columns = new Grid
        {
            Margin = new Thickness(2, 4, 6, 0)
        };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftColumn = BuildSettingsLiveRegion("general.options", BuildSettingsGeneralOptions);
        var rightColumn = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0)
        };

        rightColumn.Children.Add(BuildSettingsLiveRegion("general.capsules", BuildSettingsCapsuleOptions));
        rightColumn.Children.Add(BuildSettingsLiveRegion(
            "general.edgeBrowsing", BuildLabsEdgeCapsuleHoverIntentSettings));
        if (State.AdvancedSettingsMode)
            rightColumn.Children.Add(BuildSettingsLiveRegion(
                "general.capsuleAppearance", BuildSettingsCapsuleAppearanceOptions));

        var separator = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 10, 0, 4),
            Background = TrayBorderBrush,
            Opacity = 0.65
        };
        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(rightColumn, 2);
        columns.Children.Add(leftColumn);
        columns.Children.Add(separator);
        columns.Children.Add(rightColumn);

        return WithSettingsPageRestoreFooter(
            columns,
            RestoreSettingsSidebarGeneralDefaults);
    }

    private UIElement BuildSettingsCapsuleOptions()
    {
        var content = new StackPanel();
        void Add(string key, bool value, Action toggle, string hint) =>
            content.Children.Add(WrapWithHint(SettingsToggle(Strings.Get(key), value, toggle), hint));

        content.Children.Add(SettingsSectionLabel(Strings.Get("SettingsCapsule")));
        Add("TrayCapsuleMode", State.UseCapsuleMode, ToggleCapsuleMode, "TipCapsuleMode");
        if (!State.UseCapsuleMode) return content;

        Add("TrayDeepCapsuleMode", State.UseDeepCapsuleMode, ToggleDeepCapsuleMode, "TipDeepCapsuleMode");
        if (State.UseDeepCapsuleMode)
        {
            Add("LabsDockedCapsulesNonTopmost", State.ExperimentalDockedCapsulesNonTopmost,
                ToggleExperimentalDockedCapsulesNonTopmost, "TipLabsDockedCapsulesNonTopmost");
            Add("SettingsShowDeepCapsuleWhileExpanded", State.ShowDeepCapsuleWhileExpanded,
                ToggleDeepCapsuleExpandedSlot, "TipShowDeepCapsuleWhileExpanded");
            Add("SettingsRememberDeepCapsuleExpandedPosition", State.RememberDeepCapsuleExpandedPosition,
                ToggleRememberDeepCapsuleExpandedPosition, "TipRememberDeepCapsuleExpandedPosition");
            Add("SettingsCapsuleCollapseAll", State.UseCapsuleCollapseAll,
                ToggleCapsuleCollapseAll, "TipCapsuleCollapseAll");
            Add("SettingsCapsuleFreeQueues", State.UseCapsuleFreeQueues,
                ToggleCapsuleFreeQueues, "TipCapsuleFreeQueues");
        }
        // Linked papers excluded from capsules can still use the same second-click action.
        if ((State.UseDeepCapsuleMode && State.ShowDeepCapsuleWhileExpanded) ||
            (State.EnableTodoPaperLinks && State.HideLinkedPapersFromCapsules))
            Add("SettingsCollapseExpandedDeepCapsuleOnClick", State.CollapseExpandedDeepCapsuleOnClick,
                ToggleCollapseExpandedDeepCapsuleOnClick, "TipCollapseExpandedDeepCapsuleOnClick");
        return content;
    }

    private UIElement BuildSettingsCapsuleAppearanceOptions()
    {
        // The ordinary paper title limit is useful even when capsule mode is disabled.
        var options = new List<UIElement>
        {
            CompactSettingsField(Strings.Get("SettingsMaxTitleLength"), CreateMaxTitleLengthStepper(),
                editorWidth: 132, tipKey: "TipMaxTitleLength", topMargin: 8)
        };
        if (State.UseCapsuleMode && State.UseDeepCapsuleMode)
        {
            options.Add(WrapWithHint(
                SettingsToggle(Strings.Get("SettingsHideEdgeCapsuleCloseButtonOnHover"),
                    State.HideEdgeCapsuleCloseButtonOnHover, ToggleHideEdgeCapsuleCloseButtonOnHover),
                BuildSettingsHintTooltip(HideEdgeCapsuleCloseButtonOnHoverTip())));
            options.Add(CompactSettingsField(Strings.Get("SettingsDeepCapsuleTitleMeasureLimit"),
                CreateDeepCapsuleTitleMeasureLimitStepper(), editorWidth: 132,
                tipKey: "TipDeepCapsuleTitleMeasureLimit", topMargin: 8));
            if (State.UseCapsuleCollapseAll)
                options.Add(WrapWithHint(SettingsToggle(Strings.Get("SettingsCompactMasterCapsule"),
                    State.CompactMasterCapsule, ToggleCompactMasterCapsule), "TipCompactMasterCapsule"));
        }
        return AdvancedSettingsBlock(options.ToArray());
    }

    private UIElement BuildSettingsGeneralOptions()
    {
        var leftColumn = new StackPanel
        {
            Margin = new Thickness(0, 0, 14, 0)
        };
        leftColumn.Children.Add(CreateUiLanguageSettingsRow());
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("TrayStartup"),
                SystemSettingsHelper.IsStartupEnabled(),
                ToggleStartup),
            "TipStartup"));
        leftColumn.Children.Add(BuildSettingsLiveRegion(
            "general.telemetry",
            CreateAnonymousUsageStatisticsSettingsRow));

        if (State.AdvancedSettingsMode)
        {
            leftColumn.Children.Add(SettingsSectionLabel(
                SettingsSidebarLocalized("窗口", "Windows", "ウィンドウ", "창")));
            _settingsHidePapersFromTaskbarCheckBox = SettingsToggle(
                Strings.Get("SettingsHidePapersFromTaskbar"),
                State.HidePapersFromTaskbar,
                ToggleHidePapersFromTaskbar);
            _settingsHidePapersFromWindowSwitcherCheckBox = SettingsToggle(
                Strings.Get("SettingsHidePapersFromWindowSwitcher"),
                State.HidePapersFromWindowSwitcher,
                ToggleHidePapersFromWindowSwitcher);
            leftColumn.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    _settingsHidePapersFromTaskbarCheckBox,
                    "TipHidePapersFromTaskbar"),
                WrapWithHint(
                    _settingsHidePapersFromWindowSwitcherCheckBox,
                    "TipHidePapersFromWindowSwitcher"),
                CompactSettingsField(
                    Strings.Get("SettingsFullscreenTopmostMode"),
                    CreateFullscreenTopmostModeSegmentSelector(),
                    editorWidth: 156,
                    tipKey: "TipFullscreenTopmostMode",
                    topMargin: 8)));
        }

        leftColumn.Children.Add(SettingsSoftDivider());
        leftColumn.Children.Add(BuildSettingsLiveRegion(
            "general.topbar", BuildTopBarShortcutSettings));

        RefreshSettingsSystemVisibilityToggleStates();
        return leftColumn;
    }

    private void ToggleEdgeCapsulePreviewPreferDownward() =>
        SetSettingFromUi("edge.preview_prefer_downward", !State.EdgeCapsulePreviewPreferDownward);

    private string HideEdgeCapsuleCloseButtonOnHoverTip() =>
        SettingsSidebarLocalized(
            "开启后，普通边缘胶囊在悬停或激活时不显示关闭按钮，也不保留关闭区域；需要隐藏时可在右键菜单中选择「隐藏」。",
            "When enabled, ordinary edge capsules show neither the close button nor its reserved area while hovered or active. To hide one, choose Hide in its context menu.",
            "有効にすると、通常の端のカプセルはホバー中やアクティブ時に閉じるボタンもその領域も表示しません。隠すには右クリックメニューから「非表示」を選びます。",
            "켜면 일반 가장자리 캡슐은 마우스 오버나 활성 상태에서 닫기 버튼과 그 영역을 표시하지 않습니다. 숨기려면 오른쪽 클릭 메뉴에서 '숨김'을 선택하세요.");

    private void RestoreSettingsSidebarGeneralDefaults()
    {
        State.UiLanguage = UiLanguages.Default;
        State.HidePapersFromTaskbar = true;
        State.HidePapersFromWindowSwitcher = true;
        State.FullscreenTopmostMode = FullscreenTopmostModes.Avoid;
        State.ShowTopBarNewTodoButton = true;
        State.ShowTopBarNewNoteButton = true;
        State.ShowTopBarExternalOpenButton = false;
        State.TopBarShortcutOrder = PaperTopBarShortcuts.NormalizeOrder(null);
        State.TopBarPinnedCount = 2;
        State.HiddenTopBarShortcuts = [];
        State.TodoTopBarShortcuts = null;
        State.NoteTopBarShortcuts = null;
        State.UseCapsuleMode = true;
        State.UseDeepCapsuleMode = true;
        State.ShowDeepCapsuleWhileExpanded = true;
        State.HideEdgeCapsuleCloseButtonOnHover = false;
        State.RememberDeepCapsuleExpandedPosition = true;
        State.UseCapsuleCollapseAll = true;
        State.UseCapsuleFreeQueues = false;
        State.CompactMasterCapsule = false;
        State.CollapseExpandedDeepCapsuleOnClick = false;
        State.EdgeCapsulePreviewPreferDownward = false;
        State.ExperimentalEdgeCapsuleHoverPreview = true;
        State.ExperimentalEdgeCapsuleHoverIntent = true;
        State.ExperimentalEdgeCapsuleHoverIntentSensitivity =
            EdgeCapsuleHoverIntentSensitivities.Medium;
        State.ExperimentalDockedCapsulesNonTopmost = false;
        State.MaxTitleLength = PaperTitles.DefaultMaxTitleLength;
        State.DeepCapsuleTitleMeasureCharacterLimit = 0;

        NormalizePaperSystemVisibilitySettings();
        SaveNow();
        ApplyGeneralSettingsAfterRestore();
        RefreshEdgeCapsuleHoverIntentRuntime();
        foreach (var window in _windows.Values)
        {
            window.RefreshDeepCapsuleSlotTopmost();
        }
        foreach (var master in _masterCapsules.Values)
        {
            master.RefreshEffectiveTopmost();
        }
        RefreshSettingsWindowContent();
    }


    private UIElement CreateAnonymousUsageStatisticsSettingsRow() =>
        WrapWithHint(
            SettingsToggle(
                TelemetryStrings.Get("HelpImprove"),
                State.TelemetryEnabled,
                ToggleAnonymousUsageStatistics),
            BuildSettingsHintTooltip(TelemetryStrings.Get("Description")));

    private void ToggleAnonymousUsageStatistics() =>
        SetSettingFromUi("privacy.anonymous_usage", !State.TelemetryEnabled);
}
