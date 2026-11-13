using System;
using System.Collections.Generic;
using System.Globalization;
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

    private UIElement CreateMaxTitleLengthStepper() =>
        CreateSettingsStepper(
            () => State.MaxTitleLength.ToString(CultureInfo.InvariantCulture),
            () => SetMaxTitleLength(State.MaxTitleLength - 1),
            () => SetMaxTitleLength(State.MaxTitleLength + 1));

    private void SetMaxTitleLength(int value) =>
        SetSettingFromUi("title.max_length", PaperTitles.NormalizeMaxTitleLength(value));

    private UIElement CreateDeepCapsuleTitleMeasureLimitStepper() =>
        CreateSettingsStepper(
            DeepCapsuleTitleMeasureLimitText,
            () => SetDeepCapsuleTitleMeasureCharacterLimit(EdgeCapsuleTitleLimit.Step(
                State.DeepCapsuleTitleMeasureCharacterLimit, increase: false)),
            () => SetDeepCapsuleTitleMeasureCharacterLimit(EdgeCapsuleTitleLimit.Step(
                State.DeepCapsuleTitleMeasureCharacterLimit, increase: true)));

    private string DeepCapsuleTitleMeasureLimitText()
    {
        var limit = State.DeepCapsuleTitleMeasureCharacterLimit;
        return limit == EdgeCapsuleTitleLimit.Unlimited
            ? Strings.Get("SettingsAllCharacters")
            : (limit == EdgeCapsuleTitleLimit.Hidden ? 0 : limit).ToString(CultureInfo.InvariantCulture);
    }

    private void SetDeepCapsuleTitleMeasureCharacterLimit(int value) =>
        SetSettingFromUi("capsule.title_measure_limit", EdgeCapsuleTitleLimit.Normalize(value));

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
                CreateCaptureExclusionSettingsRow(),
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

    private UIElement CreateCaptureExclusionSettingsRow()
    {
        var toggles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var paper = SettingsToggle(
            SettingsSidebarLocalized("纸片", "Papers", "付箋", "메모"),
            State.HidePapersInCapture,
            () => SetSettingFromUi("window.hide_papers_in_capture", !State.HidePapersInCapture));
        paper.Margin = new Thickness(0);
        paper.FontSize = AppTypography.Scale(11.5);
        var capsule = SettingsToggle(
            SettingsSidebarLocalized("胶囊", "Capsules", "カプセル", "캡슐"),
            State.HideCapsulesInCapture,
            () => SetSettingFromUi("window.hide_capsules_in_capture", !State.HideCapsulesInCapture));
        capsule.Margin = new Thickness(9, 0, 0, 0);
        capsule.FontSize = AppTypography.Scale(11.5);
        toggles.Children.Add(paper);
        toggles.Children.Add(capsule);

        return WrapWithHint(
            (FrameworkElement)CompactSettingsField(
                SettingsSidebarLocalized("在截图和录屏中隐藏", "Hide from capture",
                    "スクリーンショット・録画から隠す", "스크린샷·녹화에서 숨기기"),
                toggles,
                editorWidth: 144,
                topMargin: 5),
            BuildSettingsHintTooltip(SettingsSidebarLocalized(
                "仅尝试从受支持的 Windows 截图、录屏和屏幕共享中排除勾选的窗口。透明窗口或第三方工具可能不支持，不能作为隐私安全保证。",
                "Attempts to exclude selected windows from supported Windows screenshots, recordings and sharing. Transparent windows and some third-party tools may not support it; this is not a privacy guarantee.",
                "対応する Windows のスクリーンショット・録画・共有から選択したウィンドウを除外します。透明ウィンドウや一部のツールでは動作せず、プライバシー保護を保証するものではありません。",
                "지원되는 Windows 캡처·녹화·공유에서 선택한 창을 제외합니다. 투명 창이나 일부 도구에서는 작동하지 않을 수 있으며 개인정보 보호를 보장하지 않습니다.")));
    }

    private void SetUiLanguage(string language)
    {
        var normalized = UiLanguages.Normalize(language);
        if (string.Equals(State.UiLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (!SetSettingFromUi("general.language", normalized)) return;

        if (_settingsWindow == null ||
            !PaperNoticeDialog.ShowChoice(
                _settingsWindow,
                SettingsSidebarLocalized(
                    "重启 PaperTodo",
                    "Restart PaperTodo",
                    "PaperTodo を再起動",
                    "PaperTodo 다시 시작"),
                SettingsSidebarLocalized(
                    "界面语言将在重启 PaperTodo 后生效。要现在立即重启吗？",
                    "The interface language will take effect after restarting PaperTodo. Restart now?",
                    "表示言語は PaperTodo の再起動後に反映されます。今すぐ再起動しますか？",
                    "인터페이스 언어는 PaperTodo를 다시 시작한 후 적용됩니다. 지금 다시 시작할까요?"),
                SettingsSidebarLocalized("稍后", "Later", "後で", "나중에"),
                SettingsSidebarLocalized(
                    "立即重启",
                    "Restart now",
                    "今すぐ再起動",
                    "지금 다시 시작")))
        {
            return;
        }

        if (Application.Current is App app)
        {
            app.RequestRestartAfterExit();
        }
        Exit();
    }

    private UIElement CreateUiLanguageSettingsRow() =>
        CompactSettingsField(
            Strings.Get("SettingsUiLanguage"),
            CreateUiLanguageSelector(),
            editorWidth: 156,
            tipKey: "TipSettingsUiLanguage",
            topMargin: 4);

    private UIElement CreateUiLanguageSelector()
    {
        return CreateSettingsSelect(
            [
                (UiLanguages.System, Strings.Get("UiLanguageSystem")),
                (UiLanguages.ChineseSimplified, Strings.Get("UiLanguageZhHans")),
                (UiLanguages.English, Strings.Get("UiLanguageEnglish")),
                (UiLanguages.Japanese, Strings.Get("UiLanguageJapanese")),
                (UiLanguages.Korean, Strings.Get("UiLanguageKorean"))
            ],
            UiLanguages.Normalize(State.UiLanguage),
            SetUiLanguage);
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
        var defaults = new AppState();
        State.UiLanguage = defaults.UiLanguage;
        State.HidePapersFromTaskbar = defaults.HidePapersFromTaskbar;
        State.HidePapersFromWindowSwitcher = defaults.HidePapersFromWindowSwitcher;
        State.HidePapersInCapture = defaults.HidePapersInCapture;
        State.HideCapsulesInCapture = defaults.HideCapsulesInCapture;
        State.FullscreenTopmostMode = defaults.FullscreenTopmostMode;
        State.ShowTopBarNewTodoButton = defaults.ShowTopBarNewTodoButton;
        State.ShowTopBarNewNoteButton = defaults.ShowTopBarNewNoteButton;
        State.ShowTopBarExternalOpenButton = defaults.ShowTopBarExternalOpenButton;
        State.TopBarShortcutOrder = defaults.TopBarShortcutOrder;
        State.TopBarPinnedCount = defaults.TopBarPinnedCount;
        State.HiddenTopBarShortcuts = defaults.HiddenTopBarShortcuts;
        State.TodoTopBarShortcuts = defaults.TodoTopBarShortcuts;
        State.NoteTopBarShortcuts = defaults.NoteTopBarShortcuts;
        State.UseCapsuleMode = defaults.UseCapsuleMode;
        State.UseDeepCapsuleMode = defaults.UseDeepCapsuleMode;
        State.ShowDeepCapsuleWhileExpanded = defaults.ShowDeepCapsuleWhileExpanded;
        State.HideEdgeCapsuleCloseButtonOnHover = defaults.HideEdgeCapsuleCloseButtonOnHover;
        State.RememberDeepCapsuleExpandedPosition = defaults.RememberDeepCapsuleExpandedPosition;
        State.UseCapsuleCollapseAll = defaults.UseCapsuleCollapseAll;
        State.UseCapsuleFreeQueues = defaults.UseCapsuleFreeQueues;
        State.CompactMasterCapsule = defaults.CompactMasterCapsule;
        State.CollapseExpandedDeepCapsuleOnClick = defaults.CollapseExpandedDeepCapsuleOnClick;
        State.EdgeCapsulePreviewPreferDownward = defaults.EdgeCapsulePreviewPreferDownward;
        State.ExperimentalEdgeCapsuleHoverPreview = defaults.ExperimentalEdgeCapsuleHoverPreview;
        State.ExperimentalEdgeCapsuleHoverIntent = defaults.ExperimentalEdgeCapsuleHoverIntent;
        State.ExperimentalEdgeCapsuleHoverIntentSensitivity =
            defaults.ExperimentalEdgeCapsuleHoverIntentSensitivity;
        State.ExperimentalDockedCapsulesNonTopmost = defaults.ExperimentalDockedCapsulesNonTopmost;
        State.MaxTitleLength = defaults.MaxTitleLength;
        State.DeepCapsuleTitleMeasureCharacterLimit = defaults.DeepCapsuleTitleMeasureCharacterLimit;

        NormalizePaperSystemVisibilitySettings();
        SaveNow();
        ApplyGeneralSettingsAfterRestore();
        PublishSettingEffects(SettingEffects.HoverIntent);
        PublishSettingEffects(SettingEffects.EdgeTopmost);
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
