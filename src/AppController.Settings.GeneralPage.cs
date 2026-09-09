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

        var leftColumn = new StackPanel
        {
            Margin = new Thickness(0, 0, 14, 0)
        };
        var rightColumn = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0)
        };

        leftColumn.Children.Add(CreateUiLanguageSettingsRow());
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("TrayStartup"),
                SystemSettingsHelper.IsStartupEnabled(),
                ToggleStartup),
            "TipStartup"));
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsEnableToolTips"),
                State.EnableToolTips,
                ToggleToolTips),
            "TipEnableToolTips"));
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsEnableAnimations"),
                State.EnableAnimations,
                ToggleAnimations),
            "TipEnableAnimations"));

        leftColumn.Children.Add(BuildSettingsLiveRegion(
            "general.telemetry",
            CreateAnonymousUsageStatisticsSettingsRow));

        if (State.AdvancedSettingsMode)
        {
            leftColumn.Children.Add(SettingsSectionLabel(
                SettingsSidebarLocalized("窗口", "Windows", "ウィンドウ", "창")));
            _settingsHidePapersFromTaskbarCheckBox = MarkAdvancedSetting(SettingsToggle(
                Strings.Get("SettingsHidePapersFromTaskbar"),
                State.HidePapersFromTaskbar,
                ToggleHidePapersFromTaskbar));
            _settingsHidePapersFromWindowSwitcherCheckBox = MarkAdvancedSetting(SettingsToggle(
                Strings.Get("SettingsHidePapersFromWindowSwitcher"),
                State.HidePapersFromWindowSwitcher,
                ToggleHidePapersFromWindowSwitcher));
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

        leftColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTopBarButtons")));
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsShowTopBarNewTodoButton"),
                State.ShowTopBarNewTodoButton,
                ToggleTopBarNewTodoButton),
            "TipNewTodoButton"));
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsShowTopBarNewNoteButton"),
                State.ShowTopBarNewNoteButton,
                ToggleTopBarNewNoteButton),
            "TipNewNoteButton"));
        leftColumn.Children.Add(WrapWithHint(
            SettingsToggle(
                Strings.Get("SettingsShowTopBarExternalOpenButton"),
                State.ShowTopBarExternalOpenButton,
                ToggleTopBarExternalOpenButton),
            "TipExternalOpenButton"));

        rightColumn.Children.Add(SettingsSectionLabel(Strings.Get("SettingsCapsule")));
        _settingsCapsuleModeCheckBox = SettingsToggle(
            Strings.Get("TrayCapsuleMode"),
            State.UseCapsuleMode,
            ToggleCapsuleMode);
        _settingsDeepCapsuleModeCheckBox = SettingsToggle(
            Strings.Get("TrayDeepCapsuleMode"),
            State.UseDeepCapsuleMode,
            ToggleDeepCapsuleMode);
        _settingsDeepCapsuleExpandedSlotCheckBox = SettingsToggle(
            Strings.Get("SettingsShowDeepCapsuleWhileExpanded"),
            State.ShowDeepCapsuleWhileExpanded,
            ToggleDeepCapsuleExpandedSlot);
        _settingsRememberDeepCapsuleExpandedPositionCheckBox = SettingsToggle(
            Strings.Get("SettingsRememberDeepCapsuleExpandedPosition"),
            State.RememberDeepCapsuleExpandedPosition,
            ToggleRememberDeepCapsuleExpandedPosition);
        _settingsCapsuleCollapseAllCheckBox = SettingsToggle(
            Strings.Get("SettingsCapsuleCollapseAll"),
            State.UseCapsuleCollapseAll,
            ToggleCapsuleCollapseAll);
        _settingsCollapseExpandedDeepCapsuleOnClickCheckBox = SettingsToggle(
            Strings.Get("SettingsCollapseExpandedDeepCapsuleOnClick"),
            State.CollapseExpandedDeepCapsuleOnClick,
            ToggleCollapseExpandedDeepCapsuleOnClick);

        rightColumn.Children.Add(WrapWithHint(_settingsCapsuleModeCheckBox, "TipCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(_settingsDeepCapsuleModeCheckBox, "TipDeepCapsuleMode"));
        rightColumn.Children.Add(WrapWithHint(
            _settingsDeepCapsuleExpandedSlotCheckBox,
            "TipShowDeepCapsuleWhileExpanded"));
        rightColumn.Children.Add(WrapWithHint(
            _settingsRememberDeepCapsuleExpandedPositionCheckBox,
            "TipRememberDeepCapsuleExpandedPosition"));
        rightColumn.Children.Add(WrapWithHint(
            _settingsCapsuleCollapseAllCheckBox,
            "TipCapsuleCollapseAll"));
        rightColumn.Children.Add(WrapWithHint(
            _settingsCollapseExpandedDeepCapsuleOnClickCheckBox,
            "TipCollapseExpandedDeepCapsuleOnClick"));

        if (State.AdvancedSettingsMode)
        {
            rightColumn.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    MarkAdvancedSetting(SettingsToggle(
                        Strings.Get("SettingsHideEdgeCapsuleCloseButtonOnHover"),
                        State.HideEdgeCapsuleCloseButtonOnHover,
                        ToggleHideEdgeCapsuleCloseButtonOnHover)),
                    "TipHideEdgeCapsuleCloseButtonOnHover"),
                CompactSettingsField(
                    Strings.Get("SettingsMaxTitleLength"),
                    CreateMaxTitleLengthStepper(),
                    editorWidth: 132,
                    tipKey: "TipMaxTitleLength",
                    topMargin: 8),
                CompactSettingsField(
                    Strings.Get("SettingsDeepCapsuleTitleMeasureLimit"),
                    CreateDeepCapsuleTitleMeasureLimitStepper(),
                    editorWidth: 132,
                    tipKey: "TipDeepCapsuleTitleMeasureLimit",
                    topMargin: 8)));
        }

        RefreshSettingsCapsuleToggleStates();

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

    private void RestoreSettingsSidebarGeneralDefaults()
    {
        State.EnableToolTips = true;
        State.EnableAnimations = true;
        State.UiLanguage = UiLanguages.Default;
        State.HidePapersFromTaskbar = true;
        State.HidePapersFromWindowSwitcher = true;
        State.FullscreenTopmostMode = FullscreenTopmostModes.Avoid;
        State.ShowTopBarNewTodoButton = true;
        State.ShowTopBarNewNoteButton = true;
        State.ShowTopBarExternalOpenButton = true;
        State.UseCapsuleMode = true;
        State.UseDeepCapsuleMode = true;
        State.ShowDeepCapsuleWhileExpanded = true;
        State.HideEdgeCapsuleCloseButtonOnHover = false;
        State.RememberDeepCapsuleExpandedPosition = true;
        State.UseCapsuleCollapseAll = true;
        State.CollapseExpandedDeepCapsuleOnClick = false;
        State.MaxTitleLength = PaperTitles.DefaultMaxTitleLength;
        State.DeepCapsuleTitleMeasureCharacterLimit = 0;

        NormalizePaperSystemVisibilitySettings();
        ClampPaperTitlesToMaxLength(State.MaxTitleLength);
        SaveNow();
        ApplyGeneralSettingsAfterRestore();
        RefreshSettingsWindowContent();
    }
}
