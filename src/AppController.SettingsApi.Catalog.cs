using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private IEnumerable<PaperSettingDefinition> CreatePublicSettingsCatalog()
    {
        yield return DefineSetting<string>("general.language",
            () => State.UiLanguage,
            value => State.UiLanguage = value,
            SettingEffects.None,
            title: Strings.Get("SettingsUiLanguage"),
            options: ["system", "zh-CN", "en-US", "ja-JP", "ko-KR"],
            requiresRestart: true);
        yield return DefineSetting<bool>("general.advanced_settings",
            () => State.AdvancedSettingsMode,
            value => State.AdvancedSettingsMode = value,
            SettingEffects.Advanced,
            title: Strings.Get("SettingsAdvancedMode"));
        yield return DefineSetting<bool>("privacy.anonymous_usage",
            () => State.TelemetryEnabled,
            value => State.TelemetryEnabled = value,
            SettingEffects.Telemetry,
            title: TelemetryStrings.Get("HelpImprove"));
        yield return DefineSetting<bool>("general.tooltips",
            () => State.EnableToolTips,
            value => State.EnableToolTips = value,
            title: Strings.Get("SettingsEnableToolTips"));
        yield return DefineSetting<bool>("appearance.animations",
            () => State.EnableAnimations,
            value => State.EnableAnimations = value,
            SettingEffects.Animations,
            title: Strings.Get("SettingsEnableAnimations"));
        yield return DefineSetting<string>("appearance.theme",
            () => State.Theme,
            value => State.Theme = value,
            SettingEffects.Theme,
            title: Strings.Get("TrayThemeMode"),
            options: ["system", "light", "dark"]);
        yield return DefineSetting<string>("appearance.color_scheme",
            () => State.ColorScheme,
            value => State.ColorScheme = value,
            SettingEffects.Theme,
            title: Strings.Get("SettingsColorScheme"),
            options: ColorSchemes.All);
        yield return DefineSetting<string>("appearance.paper_skin",
            () => PaperSkins.Resolve(State),
            value => State.PaperSkin = value,
            SettingEffects.Theme,
            title: Strings.Get("SettingsPaperSkin"),
            options: PaperSkins.All,
            description: "Paper surface style. A native style may require restarting a session that was created with layered windows.");
        yield return DefineSetting<bool>("appearance.match_auxiliary_material",
            () => State.MatchAuxiliaryMaterialStrength,
            value => State.MatchAuxiliaryMaterialStrength = value,
            SettingEffects.Skin,
            title: Strings.Get("SettingsMatchAuxiliaryMaterial"));
        yield return DefineSetting<string>("appearance.material_transparency",
            () => MaterialTransparencyLevels.Normalize(State.MaterialTransparency),
            value => State.MaterialTransparency = value,
            SettingEffects.MaterialTransparency,
            title: SettingsSidebarLocalized("材质透明度", "Material transparency", "素材の透明度", "재질 투명도"),
            options: MaterialTransparencyLevels.All);
        yield return DefineSetting<bool>("appearance.hide_surface_outline",
            () => State.HideSurfaceOutline,
            value => State.HideSurfaceOutline = value,
            SettingEffects.SurfaceOutline,
            title: SettingsSidebarLocalized("隐藏外轮廓", "Hide outer border", "外枠を隠す", "외곽선 숨기기"));
        yield return DefineSetting<bool>("appearance.native_material_always_active",
            () => State.MicaAlwaysActive,
            value => State.MicaAlwaysActive = value,
            SettingEffects.NativeMaterial,
            title: Strings.Get("SettingsMicaAlwaysActive"));
        yield return DefineSetting<string>("appearance.font_preset",
            () => State.UiFontPreset,
            value => State.UiFontPreset = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsUiFont"),
            options: ["default", "yahei", "dengxian"]);
        yield return DefineSetting<string>("appearance.text_rendering",
            () => State.TextRenderingProfile,
            value => State.TextRenderingProfile = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsTextRenderingProfile"),
            options: [TextRenderingProfiles.Standard, TextRenderingProfiles.Soft, TextRenderingProfiles.Sharp]);
        yield return DefineSetting<double>("appearance.font_scale",
            () => State.Zoom,
            value => State.Zoom = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsOverallFontScale"),
            minimum: 0.8,
            maximum: 1.2,
            step: 0.05);
        yield return DefineSetting<bool>("appearance.enhanced_bold",
            () => State.CustomFontEnhancedBold,
            value => State.CustomFontEnhancedBold = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsCustomFontEnhancedBold"));
        yield return DefineSetting<bool>("note.text_bold",
            () => State.NoteTextBold,
            value => State.NoteTextBold = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsNoteBodyText"));
        yield return DefineSetting<string>("note.text_size",
            () => State.NoteTextSize,
            value => State.NoteTextSize = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsNoteBodyText"),
            options: ["small", "medium", "large"]);
        yield return DefineSetting<bool>("todo.text_bold",
            () => State.TodoTextBold,
            value => State.TodoTextBold = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsTodoBodyText"));
        yield return DefineSetting<bool>("title.text_bold",
            () => State.TitleTextBold,
            value => State.TitleTextBold = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsTitleText"));
        yield return DefineSetting<string>("title.text_size",
            () => State.TitleTextSize,
            value => State.TitleTextSize = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsTitleText"),
            options: ["small", "medium", "large"]);
        yield return DefineSetting<bool>("capsule.text_bold",
            () => State.CapsuleTextBold,
            value => State.CapsuleTextBold = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsCapsuleText"));
        yield return DefineSetting<string>("capsule.text_size",
            () => State.CapsuleTextSize,
            value => State.CapsuleTextSize = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsCapsuleText"),
            options: ["small", "medium", "large"]);
        yield return DefineSetting<string>("todo.visual_size",
            () => State.TodoVisualSize,
            value => State.TodoVisualSize = value,
            SettingEffects.Typography,
            title: Strings.Get("SettingsTodoVisualSize"),
            options: ["small", "medium", "large"]);
        yield return DefineSetting<string>("note.markdown_mode",
            () => State.MarkdownRenderMode,
            value => State.MarkdownRenderMode = value,
            SettingEffects.Markdown,
            title: Strings.Get("TrayMarkdownRenderMode"),
            options: ["off", "basic", "full"]);
        yield return DefineSetting<string>("note.image_reference_text",
            () => State.ImageReferenceTextMode,
            value => State.ImageReferenceTextMode = value,
            SettingEffects.ImageReferences,
            title: Strings.Get("SettingsImageReferenceText"),
            options: ["always", "editing", "hidden"]);
        yield return DefineSetting<bool>("note.edit_animations",
            () => State.MarkdownEditAnimationEnabled,
            value => State.MarkdownEditAnimationEnabled = value,
            SettingEffects.MarkdownAnimations,
            title: Strings.Get("SettingsMarkdownEditAnimation"));
        yield return DefineSetting<string>("note.external_extension",
            () => State.ExternalMarkdownExtension,
            value => State.ExternalMarkdownExtension = value,
            SettingEffects.ExternalExtension,
            title: Strings.Get("SettingsExternalMarkdownExtension"),
            description: "Filename extension used for external notes; up to 32 characters including the leading dot. Empty restores .md.");
        yield return DefineSetting<bool>("note.compress_large_images",
            () => State.AutoCompressLargeImages,
            value => State.AutoCompressLargeImages = value,
            SettingEffects.Compress,
            title: Strings.Get("SettingsAutoCompressLargeImages"));
        yield return DefineSetting<bool>("todo.bottom_bar",
            () => State.ShowTodoBottomBar,
            value => State.ShowTodoBottomBar = value,
            SettingEffects.TodoRows,
            title: Strings.Get("SettingsShowTodoBottomBar"));
        yield return DefineSetting<bool>("todo.auto_clear_completed",
            () => State.AutoClearCompletedTodos,
            value => State.AutoClearCompletedTodos = value,
            SettingEffects.None,
            title: Strings.Get("SettingsAutoClearCompletedTodos"));
        yield return DefineSetting<bool>("todo.move_completed_to_bottom",
            () => State.AutoMoveCompletedTodosToBottom,
            value => State.AutoMoveCompletedTodosToBottom = value,
            SettingEffects.TodoOrder,
            title: Strings.Get("SettingsAutoMoveCompletedTodosToBottom"));
        yield return DefineSetting<bool>("todo.paper_links",
            () => State.EnableTodoPaperLinks,
            value => State.EnableTodoPaperLinks = value,
            SettingEffects.TodoLinks,
            title: Strings.Get("SettingsEnableTodoPaperLinks"));
        yield return DefineSetting<bool>("todo.show_linked_paper_name",
            () => State.ShowLinkedPaperName,
            value => State.ShowLinkedPaperName = value,
            SettingEffects.TodoRows,
            title: Strings.Get("SettingsShowLinkedPaperName"));
        yield return DefineSetting<bool>("todo.allow_long_linked_titles",
            () => State.AllowLongLinkedPaperTitles,
            value => State.AllowLongLinkedPaperTitles = value,
            SettingEffects.TodoRows,
            title: Strings.Get("SettingsAllowLongLinkedPaperTitles"));
        yield return DefineSetting<bool>("todo.linked_path_extension_only",
            () => State.ShowLinkedPathExtensionOnly,
            value => State.ShowLinkedPathExtensionOnly = value,
            SettingEffects.TodoRows,
            title: Strings.Get("SettingsShowLinkedPathExtensionOnly"));
        yield return DefineSetting<bool>("todo.hide_linked_paper_capsules",
            () => State.HideLinkedPapersFromCapsules,
            value => State.HideLinkedPapersFromCapsules = value,
            SettingEffects.LinkedCapsules,
            title: Strings.Get("SettingsHideLinkedPapersFromCapsules"));
        yield return DefineSetting<bool>("scripts.run_linked_on_click",
            () => State.RunLinkedScriptCapsulesOnClick,
            value => State.RunLinkedScriptCapsulesOnClick = value,
            SettingEffects.TodoRows,
            title: Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"));
        yield return DefineSetting<bool>("topbar.new_todo",
            () => State.ShowTopBarNewTodoButton,
            value => State.ShowTopBarNewTodoButton = value,
            SettingEffects.TopBar,
            title: Strings.Get("SettingsShowTopBarNewTodoButton"));
        yield return DefineSetting<bool>("topbar.new_note",
            () => State.ShowTopBarNewNoteButton,
            value => State.ShowTopBarNewNoteButton = value,
            SettingEffects.TopBar,
            title: Strings.Get("SettingsShowTopBarNewNoteButton"));
        yield return DefineSetting<bool>("topbar.external_open",
            () => State.ShowTopBarExternalOpenButton,
            value => State.ShowTopBarExternalOpenButton = value,
            SettingEffects.TopBar,
            title: Strings.Get("SettingsShowTopBarExternalOpenButton"));
        yield return DefineSetting<bool>("window.hide_from_taskbar",
            () => State.HidePapersFromTaskbar,
            value => State.HidePapersFromTaskbar = value,
            SettingEffects.SystemVisibility,
            title: Strings.Get("SettingsHidePapersFromTaskbar"));
        yield return DefineSetting<bool>("window.hide_from_switcher",
            () => State.HidePapersFromWindowSwitcher,
            value => State.HidePapersFromWindowSwitcher = value,
            SettingEffects.SystemVisibility,
            title: Strings.Get("SettingsHidePapersFromWindowSwitcher"));
        yield return DefineSetting<string>("window.fullscreen_mode",
            () => State.FullscreenTopmostMode,
            value => State.FullscreenTopmostMode = value,
            SettingEffects.Fullscreen,
            title: Strings.Get("SettingsFullscreenTopmostMode"),
            options: ["avoid", "stayOnTop"]);
        yield return DefineSetting<string>("window.resize_grip",
            () => State.ResizeGripMode,
            value => State.ResizeGripMode = value,
            SettingEffects.Resize,
            title: Strings.Get("SettingsResizeGripMode"),
            options: ["standard", "soft", "hidden"]);
        yield return DefineSetting<bool>("capsule.enabled",
            () => State.UseCapsuleMode,
            value => State.UseCapsuleMode = value,
            SettingEffects.CapsuleMode,
            title: Strings.Get("TrayCapsuleMode"));
        yield return DefineSetting<bool>("capsule.edge_enabled",
            () => State.UseDeepCapsuleMode,
            value => State.UseDeepCapsuleMode = value,
            SettingEffects.CapsuleMode,
            title: Strings.Get("TrayDeepCapsuleMode"));
        yield return DefineSetting<bool>("capsule.master_enabled",
            () => State.UseCapsuleCollapseAll,
            value => State.UseCapsuleCollapseAll = value,
            SettingEffects.CapsuleMode,
            title: Strings.Get("SettingsCapsuleCollapseAll"));
        yield return DefineSetting<bool>("capsule.show_while_expanded",
            () => State.ShowDeepCapsuleWhileExpanded,
            value => State.ShowDeepCapsuleWhileExpanded = value,
            SettingEffects.CapsuleMode,
            title: Strings.Get("SettingsShowDeepCapsuleWhileExpanded"));
        yield return DefineSetting<string>("capsule.gap",
            () => State.DeepCapsuleGapSize,
            value => State.DeepCapsuleGapSize = value,
            SettingEffects.Arrange,
            title: Strings.Get("SettingsDeepCapsuleGap"),
            options: ["narrow", "standard", "wide"]);
        yield return DefineSetting<bool>("capsule.remember_expanded_position",
            () => State.RememberDeepCapsuleExpandedPosition,
            value => State.RememberDeepCapsuleExpandedPosition = value,
            SettingEffects.None,
            title: Strings.Get("SettingsRememberDeepCapsuleExpandedPosition"));
        yield return DefineSetting<bool>("capsule.hide_close_button",
            () => State.HideEdgeCapsuleCloseButtonOnHover,
            value => State.HideEdgeCapsuleCloseButtonOnHover = value,
            SettingEffects.CapsuleClose,
            title: Strings.Get("SettingsHideEdgeCapsuleCloseButtonOnHover"));
        yield return DefineSetting<bool>("capsule.click_to_collapse",
            () => State.CollapseExpandedDeepCapsuleOnClick,
            value => State.CollapseExpandedDeepCapsuleOnClick = value,
            SettingEffects.None,
            title: Strings.Get("SettingsCollapseExpandedDeepCapsuleOnClick"));
        yield return DefineSetting<int>("title.max_length",
            () => State.MaxTitleLength,
            value => State.MaxTitleLength = value,
            SettingEffects.Titles,
            title: Strings.Get("SettingsMaxTitleLength"),
            minimum: 2,
            maximum: 20,
            step: 1,
            description: "Existing custom titles are shortened to this Unicode text-element limit.");
        yield return DefineSetting<int>("capsule.title_measure_limit",
            () => State.DeepCapsuleTitleMeasureCharacterLimit,
            value => State.DeepCapsuleTitleMeasureCharacterLimit = value,
            SettingEffects.Titles,
            title: Strings.Get("SettingsDeepCapsuleTitleMeasureLimit"),
            minimum: -1,
            maximum: 20,
            step: 1,
            description: "0 means unlimited; -1 hides title text.");
        yield return DefineSetting<bool>("edge.preview_enabled",
            () => State.ExperimentalEdgeCapsuleHoverPreview,
            value => State.ExperimentalEdgeCapsuleHoverPreview = value,
            SettingEffects.Preview,
            title: Strings.Get("LabsEnableEdgeCapsuleHoverPreview"));
        yield return DefineSetting<bool>("edge.preview_prefer_downward",
            () => State.EdgeCapsulePreviewPreferDownward,
            value => State.EdgeCapsulePreviewPreferDownward = value,
            SettingEffects.None,
            title: SettingsSidebarLocalized("浏览时优先向下展开", "Prefer downward expansion while browsing", "閲覧時は下方向への展開を優先", "탐색 중 아래로 펼치기 우선"));
        yield return DefineSetting<bool>("edge.hover_intent",
            () => State.ExperimentalEdgeCapsuleHoverIntent,
            value => State.ExperimentalEdgeCapsuleHoverIntent = value,
            SettingEffects.HoverIntent,
            title: Strings.Get("LabsEdgeCapsuleHoverIntent"));
        yield return DefineSetting<string>("edge.hover_sensitivity",
            () => State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
            value => State.ExperimentalEdgeCapsuleHoverIntentSensitivity = value,
            SettingEffects.HoverIntent,
            title: Strings.Get("LabsEdgeCapsuleHoverIntentSensitivity"),
            options: ["veryLow", "low", "medium", "high", "veryHigh"]);
        yield return DefineSetting<bool>("edge.non_topmost",
            () => State.ExperimentalDockedCapsulesNonTopmost,
            value => State.ExperimentalDockedCapsulesNonTopmost = value,
            SettingEffects.EdgeTopmost,
            title: Strings.Get("LabsDockedCapsulesNonTopmost"));
        yield return DefineSetting<bool>("focus.inactive_opacity_enabled",
            () => State.ExperimentalInactivePaperOpacity,
            value => State.ExperimentalInactivePaperOpacity = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsEnableInactivePaperOpacity"));
        yield return DefineSetting<bool>("focus.capsule_opacity_enabled",
            () => State.ExperimentalRestingCapsuleOpacity,
            value => State.ExperimentalRestingCapsuleOpacity = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsFocusRestingOpacity"));
        yield return DefineSetting<bool>("focus.opacity_include_master",
            () => State.ExperimentalRestingCapsuleOpacityIncludesMaster,
            value => State.ExperimentalRestingCapsuleOpacityIncludesMaster = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsFocusRestingIncludeMaster"));
        yield return DefineSetting<bool>("focus.capsule_opacity_always",
            () => State.ExperimentalRestingCapsuleOpacityAlways,
            value => State.ExperimentalRestingCapsuleOpacityAlways = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsFocusRestingAlways"));
        yield return DefineSetting<double>("focus.inactive_opacity",
            () => State.ExperimentalInactivePaperOpacityLevel,
            value => State.ExperimentalInactivePaperOpacityLevel = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsInactivePaperOpacityLevel"),
            minimum: 0.3,
            maximum: 1,
            step: 0.05);
        yield return DefineSetting<double>("focus.capsule_opacity",
            () => State.ExperimentalRestingCapsuleOpacityLevel,
            value => State.ExperimentalRestingCapsuleOpacityLevel = value,
            SettingEffects.Opacity,
            title: Strings.Get("LabsRestingCapsuleOpacityLevel"),
            minimum: 0.3,
            maximum: 1,
            step: 0.05);
        yield return DefineSetting<bool>("focus.collapse_on_deactivate",
            () => State.ExperimentalCollapsePaperOnDeactivate,
            value => State.ExperimentalCollapsePaperOnDeactivate = value,
            SettingEffects.None,
            title: Strings.Get("LabsCollapsePaperOnDeactivate"));
        yield return DefineSetting<bool>("focus.hide_inactive_buttons",
            () => State.ExperimentalHideInactiveTopBarButtons,
            value => State.ExperimentalHideInactiveTopBarButtons = value,
            SettingEffects.Focus,
            title: Strings.Get("LabsHideInactiveTopBarButtons"));
        yield return DefineSetting<bool>("focus.hide_inactive_titlebar",
            () => State.ExperimentalHideInactiveTitleBar,
            value => State.ExperimentalHideInactiveTitleBar = value,
            SettingEffects.Focus,
            title: Strings.Get("LabsHideInactiveTitleBar"));
        yield return DefineSetting<bool>("interaction.allow_icon_unlock",
            () => State.ExperimentalAllowLockIconUnlock,
            value => State.ExperimentalAllowLockIconUnlock = value,
            SettingEffects.Passive,
            title: Strings.Get("LabsAllowLockIconUnlock"));
        yield return DefineSetting<double>("interaction.shortcut_opacity",
            () => State.ExperimentalShortcutOpacityLevel,
            value => State.ExperimentalShortcutOpacityLevel = value,
            SettingEffects.Passive,
            title: Strings.Get("LabsShortcutOpacityLevel"),
            minimum: 0.3,
            maximum: 1,
            step: 0.05);
        yield return DefineSetting<bool>("todo.reminders",
            () => State.ExperimentalTodoReminders,
            value => State.ExperimentalTodoReminders = value,
            SettingEffects.Reminders,
            title: Strings.Get("LabsEnableTodoReminders"));
        yield return DefineSetting<bool>("todo.reminder_button",
            () => State.ExperimentalTodoReminderShowButton,
            value => State.ExperimentalTodoReminderShowButton = value,
            SettingEffects.Reminders,
            title: Strings.Get("LabsTodoReminderShowButton"));
        yield return DefineSetting<int>("todo.reminder_quick_minutes",
            () => State.ExperimentalTodoReminderQuickMinutes,
            value => State.ExperimentalTodoReminderQuickMinutes = value,
            SettingEffects.None,
            title: Strings.Get("LabsTodoReminderQuickMinutes"),
            minimum: 5,
            maximum: 240,
            step: 5);
        yield return DefineSetting<bool>("todo.reminder_sound_enabled",
            () => State.ExperimentalTodoReminderSoundEnabled,
            value => State.ExperimentalTodoReminderSoundEnabled = value,
            SettingEffects.None,
            title: Strings.Get("LabsTodoReminderSoundEnabled"));
        yield return DefineSetting<string>("todo.reminder_sound",
            () => State.ExperimentalTodoReminderSound,
            value => State.ExperimentalTodoReminderSound = value,
            SettingEffects.None,
            title: Strings.Get("LabsTodoReminderSound"),
            options: ["asterisk", "beep", "exclamation", "hand", "question"]);
        yield return DefineSetting<bool>("window.magnet_enabled",
            () => State.ExperimentalCapsuleMagnetism,
            value => State.ExperimentalCapsuleMagnetism = value,
            SettingEffects.Magnet,
            title: Strings.Get("LabsEnableCapsuleMagnetism"));
        yield return DefineSetting<bool>("window.magnet_screen_edges",
            () => State.ExperimentalCapsuleMagnetScreenEdges,
            value => State.ExperimentalCapsuleMagnetScreenEdges = value,
            SettingEffects.Magnet,
            title: Strings.Get("LabsMagnetScreenEdges"));
        yield return DefineSetting<bool>("window.magnet_window_edges",
            () => State.ExperimentalCapsuleMagnetWindowEdges,
            value => State.ExperimentalCapsuleMagnetWindowEdges = value,
            SettingEffects.Magnet,
            title: Strings.Get("LabsMagnetWindowEdges"));
        yield return DefineSetting<int>("window.magnet_distance",
            () => State.ExperimentalCapsuleMagnetDistance,
            value => State.ExperimentalCapsuleMagnetDistance = value,
            SettingEffects.None,
            title: Strings.Get("LabsMagnetDistance"),
            minimum: 6,
            maximum: 48,
            step: 2);
        yield return DefineSetting<bool>("window.tether_enabled",
            () => State.ExperimentalWindowTethering,
            value => State.ExperimentalWindowTethering = value,
            SettingEffects.Tether,
            title: Strings.Get("LabsEnableWindowTethering"));
        yield return DefineSetting<string>("window.tether_edge",
            () => State.ExperimentalWindowTetherPreferredEdge,
            value => State.ExperimentalWindowTetherPreferredEdge = value,
            SettingEffects.TetherOptions,
            title: Strings.Get("LabsWindowTetherPreferredEdge"),
            options: ["auto", "left", "right", "top", "bottom"]);
        yield return DefineSetting<int>("window.tether_gap",
            () => State.ExperimentalWindowTetherGap,
            value => State.ExperimentalWindowTetherGap = value,
            SettingEffects.TetherOptions,
            title: Strings.Get("LabsWindowTetherGap"),
            minimum: 0,
            maximum: 24,
            step: 2);
        yield return DefineSetting<bool>("window.tether_visibility_link",
            () => State.ExperimentalTetherVisibilityLink,
            value => State.ExperimentalTetherVisibilityLink = value,
            SettingEffects.TetherVisibility,
            title: Strings.Get("LabsEnableTetherVisibility"));
        yield return DefineSetting<string>("window.tether_minimized",
            () => State.ExperimentalTetherMinimizedBehavior,
            value => State.ExperimentalTetherMinimizedBehavior = value,
            SettingEffects.TetherVisibility,
            title: Strings.Get("LabsTetherMinimizedBehavior"),
            options: ["hide", "capsule"]);
        yield return DefineSetting<bool>("scripts.persistent_process",
            () => State.UsePersistentPowerShellProcess,
            value => State.UsePersistentPowerShellProcess = value,
            SettingEffects.Scripts,
            title: Strings.Get("SettingsPersistentPowerShellProcess"));
        yield return DefineSetting<bool>("scripts.prefer_powershell7",
            () => State.PreferPowerShell7,
            value => State.PreferPowerShell7 = value,
            SettingEffects.None,
            title: Strings.Get("SettingsPreferPowerShell7"));
        yield return DefineSetting<bool>("scripts.hide_run_window",
            () => State.HideScriptRunWindow,
            value => State.HideScriptRunWindow = value,
            SettingEffects.None,
            title: Strings.Get("SettingsHideScriptRunWindow"));
        yield return DefineSetting<bool>("shortcuts.preserve_linked_hidden",
            () => State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts,
            value => State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts = value,
            SettingEffects.VisibilitySnapshot,
            title: Strings.Get("ShortcutPreserveLinkedPaperHiddenState"));
        yield return DefineSetting<bool>("shortcuts.open_edge_at_cursor",
            () => State.OpenEdgeCapsuleShortcutAtCursor,
            value => State.OpenEdgeCapsuleShortcutAtCursor = value,
            SettingEffects.None,
            title: Strings.Get("SettingsOpenEdgeCapsuleShortcutAtCursor"));
        yield return DefineSetting<bool>("shortcuts.distinguish_numpad",
            () => State.DistinguishNumpadShortcutDigits,
            value => State.DistinguishNumpadShortcutDigits = value,
            SettingEffects.Shortcuts,
            title: Strings.Get("SettingsDistinguishNumpadShortcutDigits"));
        yield return DefineSetting<bool>("mcp.enabled",
            () => State.McpEnabled,
            value => State.McpEnabled = value,
            SettingEffects.Mcp,
            title: Strings.Get("LabsMcpEnable"));
        yield return DefineSetting<bool>("mcp.additive_writes",
            () => State.McpAllowBlankWrites,
            value => State.McpAllowBlankWrites = value,
            SettingEffects.Mcp,
            title: Strings.Get("LabsMcpBlankWrites"));
        yield return DefineSetting<bool>("mcp.full_writes",
            () => State.McpAllowFullWrites,
            value => State.McpAllowFullWrites = value,
            SettingEffects.Mcp,
            title: Strings.Get("LabsMcpFullWrites"));
        yield return DefineSetting<bool>("mcp.deletes",
            () => State.McpAllowDeletes,
            value => State.McpAllowDeletes = value,
            SettingEffects.Mcp,
            title: Strings.Get("LabsMcpDeletes"));
        foreach (var definition in CreateExternalSettingsCatalog()) yield return definition;
        foreach (var definition in CreateShortcutSettingsCatalog()) yield return definition;
    }
}
