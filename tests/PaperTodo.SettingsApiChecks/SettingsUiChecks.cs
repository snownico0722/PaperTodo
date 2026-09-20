using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static T ReadField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static PaperSettingsService SettingsFor(AppController c, Func<bool> commit, Action<Action> publish)
    {
        var definitions = (IEnumerable<PaperSettingDefinition>)Invoke(c, "CreatePublicSettingsCatalog")!;
        var service = new PaperSettingsService(definitions, () => true, commit, publish);
        Field(c, "_publicSettings", service);
        return service;
    }

    private static void SharedUiSettingBehavior()
    {
        // Actual UI entry points and API callers share the catalog. Persistence/publication are
        // substituted here; editor/publication behavior is exercised with a real WPF window below.
        foreach (var (method, id, hasArgument) in new[]
        {
            ("SetCapsuleTextSize", "capsule.text_size", true),
            ("SetColorScheme", "appearance.color_scheme", true),
            ("SetDeepCapsuleGapSize", "capsule.gap", true),
            ("SetDeepCapsuleTitleMeasureCharacterLimit", "capsule.title_measure_limit", true),
            ("SetExperimentalCapsuleMagnetDistance", "window.magnet_distance", true),
            ("SetExperimentalEdgeCapsuleHoverIntentSensitivity", "edge.hover_sensitivity", true),
            ("SetExperimentalInactivePaperOpacityLevel", "focus.inactive_opacity", true),
            ("SetExperimentalRestingCapsuleOpacityLevel", "focus.capsule_opacity", true),
            ("SetExperimentalShortcutOpacityLevel", "interaction.shortcut_opacity", true),
            ("SetExperimentalTetherMinimizedBehavior", "window.tether_minimized", true),
            ("SetExperimentalTodoReminderQuickMinutes", "todo.reminder_quick_minutes", true),
            ("SetExperimentalTodoReminderSound", "todo.reminder_sound", true),
            ("SetExperimentalWindowTetherGap", "window.tether_gap", true),
            ("SetExperimentalWindowTetherPreferredEdge", "window.tether_edge", true),
            ("SetExternalMarkdownExtension", "note.external_extension", true),
            ("SetFullscreenTopmostMode", "window.fullscreen_mode", true),
            ("SetImageReferenceTextMode", "note.image_reference_text", true),
            ("SetMarkdownRenderMode", "note.markdown_mode", true),
            ("SetMaxTitleLength", "title.max_length", true),
            ("SetNoteTextSize", "note.text_size", true),
            ("SetOverallFontScale", "appearance.font_scale", true),
            ("SetResizeGripMode", "window.resize_grip", true),
            ("SetTextRenderingProfile", "appearance.text_rendering", true),
            ("SetTheme", "appearance.theme", true),
            ("SetTitleTextSize", "title.text_size", true),
            ("SetTodoVisualSize", "todo.visual_size", true),
            ("SetUiFontPreset", "appearance.font_preset", true),
            ("SetUiLanguage", "general.language", true),
            ("ToggleAdvancedSettingsMode", "general.advanced_settings", false),
            ("ToggleAnimations", "appearance.animations", false),
            ("ToggleAnonymousUsageStatistics", "privacy.anonymous_usage", false),
            ("ToggleAutoClearCompletedTodos", "todo.auto_clear_completed", false),
            ("ToggleAutoCompressLargeImages", "note.compress_large_images", false),
            ("ToggleAutoMoveCompletedTodosToBottom", "todo.move_completed_to_bottom", false),
            ("ToggleCapsuleCollapseAll", "capsule.master_enabled", false),
            ("ToggleCapsuleMode", "capsule.enabled", false),
            ("ToggleCapsuleTextBold", "capsule.text_bold", false),
            ("ToggleCollapseExpandedDeepCapsuleOnClick", "capsule.click_to_collapse", false),
            ("ToggleCustomFontEnhancedBold", "appearance.enhanced_bold", false),
            ("ToggleDeepCapsuleExpandedSlot", "capsule.show_while_expanded", false),
            ("ToggleDeepCapsuleMode", "capsule.edge_enabled", false),
            ("ToggleEdgeCapsulePreviewPreferDownward", "edge.preview_prefer_downward", false),
            ("ToggleExperimentalAllowLockIconUnlock", "interaction.allow_icon_unlock", false),
            ("ToggleExperimentalCapsuleMagnetScreenEdges", "window.magnet_screen_edges", false),
            ("ToggleExperimentalCapsuleMagnetWindowEdges", "window.magnet_window_edges", false),
            ("ToggleExperimentalCapsuleMagnetism", "window.magnet_enabled", false),
            ("ToggleExperimentalCollapsePaperOnDeactivate", "focus.collapse_on_deactivate", false),
            ("ToggleExperimentalDockedCapsulesNonTopmost", "edge.non_topmost", false),
            ("ToggleExperimentalEdgeCapsuleHoverIntent", "edge.hover_intent", false),
            ("ToggleExperimentalEdgeCapsuleHoverPreview", "edge.preview_enabled", false),
            ("ToggleExperimentalHideInactiveTitleBar", "focus.hide_inactive_titlebar", false),
            ("ToggleExperimentalHideInactiveTopBarButtons", "focus.hide_inactive_buttons", false),
            ("ToggleExperimentalInactivePaperOpacity", "focus.inactive_opacity_enabled", false),
            ("ToggleExperimentalRestingCapsuleOpacity", "focus.capsule_opacity_enabled", false),
            ("ToggleExperimentalRestingCapsuleOpacityAlways", "focus.capsule_opacity_always", false),
            ("ToggleExperimentalRestingCapsuleOpacityIncludesMaster", "focus.opacity_include_master", false),
            ("ToggleExperimentalTetherVisibilityLink", "window.tether_visibility_link", false),
            ("ToggleExperimentalTodoReminderShowButton", "todo.reminder_button", false),
            ("ToggleExperimentalTodoReminderSoundEnabled", "todo.reminder_sound_enabled", false),
            ("ToggleExperimentalTodoReminders", "todo.reminders", false),
            ("ToggleExperimentalWindowTethering", "window.tether_enabled", false),
            ("ToggleHideEdgeCapsuleCloseButtonOnHover", "capsule.hide_close_button", false),
            ("ToggleHideLinkedPapersFromCapsules", "todo.hide_linked_paper_capsules", false),
            ("ToggleHidePapersFromWindowSwitcher", "window.hide_from_switcher", false),
            ("ToggleHideScriptRunWindow", "scripts.hide_run_window", false),
            ("ToggleLinkedPaperNameDisplay", "todo.show_linked_paper_name", false),
            ("ToggleLinkedPathExtensionOnly", "todo.linked_path_extension_only", false),
            ("ToggleLongLinkedPaperTitles", "todo.allow_long_linked_titles", false),
            ("ToggleMarkdownEditAnimation", "note.edit_animations", false),
            ("ToggleMcpBlankWrites", "mcp.additive_writes", false),
            ("ToggleMcpDeletes", "mcp.deletes", false),
            ("ToggleMcpEnabled", "mcp.enabled", false),
            ("ToggleMcpFullWrites", "mcp.full_writes", false),
            ("ToggleMcpSettingsControl", "mcp.settings_control", false),
            ("ToggleNoteTextBold", "note.text_bold", false),
            ("ToggleOpenEdgeCapsuleShortcutAtCursor", "shortcuts.open_edge_at_cursor", false),
            ("TogglePersistentPowerShellProcess", "scripts.persistent_process", false),
            ("TogglePreferPowerShell7", "scripts.prefer_powershell7", false),
            ("TogglePreserveLinkedPaperHiddenStateInVisibilityShortcuts", "shortcuts.preserve_linked_hidden", false),
            ("ToggleRememberDeepCapsuleExpandedPosition", "capsule.remember_expanded_position", false),
            ("ToggleRunLinkedScriptCapsulesOnClick", "scripts.run_linked_on_click", false),
            ("ToggleTitleTextBold", "title.text_bold", false),
            ("ToggleTodoPaperLinks", "todo.paper_links", false),
            ("ToggleTodoTextBold", "todo.text_bold", false),
            ("ToggleToolTips", "general.tooltips", false),
            ("ToggleTopBarExternalOpenButton", "topbar.external_open", false),
            ("ToggleTopBarNewNoteButton", "topbar.new_note", false),
            ("ToggleTopBarNewTodoButton", "topbar.new_todo", false)
        })
        {
            var ui = Controller();
            var api = Controller();
            var seed = JsonSerializer.Serialize(new AppState
            {
                HidePapersFromWindowSwitcher = false,
                Papers = [new PaperData { Type = PaperTypes.Todo, Title = "abcdef", Items =
                    [new PaperItem { Text = "done", Done = true, Order = 0 }, new PaperItem { Text = "open", Order = 1 }] }]
            });
            foreach (var c in new[] { ui, api })
                Field(c, "<State>k__BackingField", JsonSerializer.Deserialize<AppState>(seed)!);
            var uiSaves = 0;
            var uiService = SettingsFor(ui, () => { uiSaves++; return true; }, _ => { });
            var apiService = SettingsFor(api, () => true, _ => { });
            var setting = apiService.Get(id);
            object value = setting.Type switch
            {
                "boolean" => !setting.Value.GetBoolean(),
                "integer" => setting.Value.GetInt32() == (int)setting.Min! ? (int)setting.Max! : (int)setting.Min!,
                "number" => setting.Value.GetDouble() == setting.Min ? setting.Max!.Value : setting.Min!.Value,
                _ => id == "note.external_extension" ? ".rst" : setting.Options.First(o => o != setting.Value.GetString())
            };
            if (method == "SetExternalMarkdownExtension") Invoke(ui, method, value, true);
            else if (hasArgument) Invoke(ui, method, value);
            else Invoke(ui, method);
            apiService.Set(id, Json(value));
            Check(uiSaves == 1 && JsonElement.DeepEquals(Json(ui.State), Json(api.State)),
                method + " and API apply the same mutation, prerequisites and ordering.");
            Check(!uiService.Set(id, Json(value)).Changed && uiSaves == 1, "UI result is also an API no-op.");
        }
        var exiting = Controller();
        var exitSaves = 0;
        SettingsFor(exiting, () => { exitSaves++; return true; }, _ => { });
        Invoke(exiting, "SetExternalMarkdownExtension", ".txt", false);
        Check(exiting.State.ExternalMarkdownExtension == ".txt" && exitSaves == 0,
            "Exit transfers the suffix draft without adding an extra settings save.");

        var title = Controller();
        title.State.Papers.Add(new PaperData { Title = "abcdef" });
        SettingsFor(title, () => true, _ => { });
        Invoke(title, "SetMaxTitleLength", 2);
        Check(title.State.MaxTitleLength == 2 && title.State.Papers[0].Title == "ab", "UI title changes reuse catalog clamping.");
        var failed = Controller();
        SettingsFor(failed, () => false, _ => throw new Exception("Failed writes must not publish."));
        Invoke(failed, "ToggleTodoPaperLinks");
        Check(failed.State.EnableTodoPaperLinks, "UI save failure restores the setting just like the API.");
        failed.State.UseCapsuleMode = failed.State.UseDeepCapsuleMode = failed.State.UseCapsuleCollapseAll = false;
        SettingsFor(failed, () => true, _ => { });
        Invoke(failed, "ToggleCapsuleCollapseAll");
        Check(failed.State.UseCapsuleMode && failed.State.UseDeepCapsuleMode && failed.State.UseCapsuleCollapseAll,
            "UI master enable uses the shared prerequisite mutation.");
    }

    private static void DrainSettingsUi()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void SettingsEditorBehavior()
    {
        var c = Controller();
        foreach (var name in new[] { "_masterCapsules", "_settingsPageScrollOffsets", "_settingsRegionRefreshers", "_pluginStatusRefreshers" })
        {
            var field = typeof(AppController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(c, Activator.CreateInstance(field.FieldType));
        }
        var page = typeof(AppController).GetNestedType("SettingsPage", BindingFlags.NonPublic)!;
        Field(c, "_settingsPage", Enum.Parse(page, "Note"));
        var root = (UIElement)Invoke(c, "BuildSettingsSidebarNotePage")!;
        var editor = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");
        var window = new Window { Content = root, Width = 900, Height = 640 };
        Field(c, "_settingsWindow", window);
        var commit = true;
        var saves = 0;
        var persistedExtension = c.State.ExternalMarkdownExtension;
        var service = SettingsFor(c, () =>
        {
            saves++;
            if (commit) persistedExtension = c.State.ExternalMarkdownExtension;
            return commit;
        }, action => action());
        try
        {
            window.Show();
            window.Activate();
            editor.Focus();
            DrainSettingsUi();
            Check(editor.IsKeyboardFocusWithin, "Regression runs with real keyboard focus in a visible WPF editor.");
            service.Set("note.external_extension", Json(".txt"));
            DrainSettingsUi();
            Check(c.State.ExternalMarkdownExtension == ".txt" && persistedExtension == ".txt" && editor.Text == ".txt",
                "External write updates live editor without writing its old value back.");
            Check(ReferenceEquals(window.Content, root) && ReferenceEquals(editor, ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox")) &&
                editor.IsKeyboardFocusWithin, "External extension update keeps the same focused editor and page.");
            editor.Text = ".draft";
            service.Set("todo.auto_clear_completed", Json(!c.State.AutoClearCompletedTodos));
            service.Set("note.edit_animations", Json(!c.State.MarkdownEditAnimationEnabled));
            DrainSettingsUi();
            Check(ReferenceEquals(window.Content, root) && editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&
                c.State.ExternalMarkdownExtension == ".txt", "Unrelated and local-region updates leave an uncommitted draft and focus untouched.");
            var beforeInvalid = saves;
            Throws<PaperSettingsException>(() => service.Set("note.external_extension", Json("../bad")), "invalid_setting_value");
            Check(saves == beforeInvalid && editor.Text == ".draft", "Rejected input does not disturb the draft.");
            commit = false;
            Throws<PaperSettingsException>(() => service.Set("note.external_extension", Json(".rst")), "save_failed");
            Check(editor.Text == ".draft" && c.State.ExternalMarkdownExtension == ".txt", "Save failure changes neither the draft nor saved preference.");
            commit = true;
            service.Set("note.external_extension", Json(".rst"));
            Keyboard.ClearFocus();
            DrainSettingsUi();
            Check(c.State.ExternalMarkdownExtension == ".rst" && editor.Text == ".rst", "Real focus loss cannot undo an accepted external write.");
            editor.Focus();
            DrainSettingsUi();
            var refreshes = 0;
            c.PluginPopupThemeChanged += () => refreshes++;
            service.Set("appearance.theme", Json("dark"));
            DrainSettingsUi();
            Check(refreshes == 1 && !ReferenceEquals(window.Content, root), "Theme rebuilds settings chrome exactly once.");
            var replacement = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");
            Check(replacement.Text == ".rst", "Whole-tree theme refresh preserves the accepted extension.");
            editor.Text = ".stale";
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, editor, replacement)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Check(c.State.ExternalMarkdownExtension == ".rst" && replacement.Text == ".rst", "Late focus event from a detached editor is ignored.");
        }
        finally
        {
            Field(c, "_settingsWindow", null!);
            window.Close();
            DrainSettingsUi();
        }
    }
}
