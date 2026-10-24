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
            ("ToggleFastAnimations", "appearance.fast_animations", false),
            ("ToggleAnonymousUsageStatistics", "privacy.anonymous_usage", false),
            ("ToggleAutoClearCompletedTodos", "todo.auto_clear_completed", false),
            ("ToggleAutoCompressLargeImages", "note.compress_large_images", false),
            ("ToggleAutoMoveCompletedTodosToBottom", "todo.move_completed_to_bottom", false),
            ("ToggleTodoBottomBar", "todo.bottom_bar", false),
            ("ToggleCapsuleCollapseAll", "capsule.master_enabled", false),
            ("ToggleCapsuleFreeQueues", "capsule.free_queues", false),
            ("ToggleCompactMasterCapsule", "capsule.master_compact", false),
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
        Check(title.State.MaxTitleLength == 2 && title.State.Papers[0].Title == "abcd", "UI title changes reuse weighted catalog clamping.");
        var failed = Controller();
        SettingsFor(failed, () => false, _ => throw new Exception("Failed writes must not publish."));
        Invoke(failed, "ToggleTodoPaperLinks");
        Check(failed.State.EnableTodoPaperLinks, "UI save failure restores the setting just like the API.");
        failed.State.UseCapsuleMode = failed.State.UseDeepCapsuleMode = failed.State.UseCapsuleCollapseAll = false;
        SettingsFor(failed, () => true, _ => { });
        Invoke(failed, "ToggleCapsuleCollapseAll");
        Check(failed.State.UseCapsuleMode && failed.State.UseDeepCapsuleMode && failed.State.UseCapsuleCollapseAll,
            "UI master enable uses the shared prerequisite mutation.");

        // D-058: one edge can host several free queues, which leaves slot 0 without a single
        // owner, so the master pill and free queues cannot be on at the same time. The rule lives
        // in the catalog setters so the UI path and the public API cannot disagree.
        var exclusive = Controller();
        SettingsFor(exclusive, () => true, _ => { });
        Invoke(exclusive, "ToggleCapsuleFreeQueues");
        Check(exclusive.State.UseCapsuleFreeQueues && !exclusive.State.UseCapsuleCollapseAll,
            "Enabling free queues turns the master capsule off.");
        Invoke(exclusive, "ToggleCapsuleCollapseAll");
        Check(exclusive.State.UseCapsuleCollapseAll && !exclusive.State.UseCapsuleFreeQueues,
            "Enabling the master capsule turns free queues off.");

        // FQ-08：前置胶囊模式关闭时写 free_queues=true，必须联动开启 capsule/deep。
        // 否则设置返回 Changed:true 却运行期不生效，重启时又被 StateStore 归一化静默丢弃——
        // 「保存成功」不再等价于「最终生效」。语义与 master_enabled 对称。
        var cascade = Controller();
        cascade.State.UseCapsuleMode = false;
        cascade.State.UseDeepCapsuleMode = false;
        cascade.State.UseCapsuleCollapseAll = false;
        var cascadeService = SettingsFor(cascade, () => true, _ => { });
        Check(
            cascadeService.Set("capsule.free_queues", Json(true)).Changed,
            "Enabling free queues while capsule mode is off must still report a change.");
        Check(
            cascade.State.UseCapsuleFreeQueues && cascade.State.UseCapsuleMode &&
            cascade.State.UseDeepCapsuleMode && !cascade.State.UseCapsuleCollapseAll,
            "Writing free queues cascades the capsule prerequisites on, like the master pill does.");

        // 级联开启的开关也必须参与回滚，否则失败写入会把用户从「两个都关」推到
        // 「free=true 但 capsule 仍关」——正是 FQ-08 描述的静默失效状态。
        var cascadeFail = Controller();
        cascadeFail.State.UseCapsuleMode = false;
        cascadeFail.State.UseDeepCapsuleMode = false;
        cascadeFail.State.UseCapsuleCollapseAll = false;
        var cascadeFailService = SettingsFor(cascadeFail, () => false,
            _ => throw new Exception("Failed writes must not publish."));
        Throws<PaperSettingsException>(
            () => cascadeFailService.Set("capsule.free_queues", Json(true)), "save_failed");
        Check(
            !cascadeFail.State.UseCapsuleFreeQueues && !cascadeFail.State.UseCapsuleMode &&
            !cascadeFail.State.UseDeepCapsuleMode,
            "A failed free-queue write also rolls back the cascaded capsule flags.");

        // 保存失败时，互斥 setter 连带改写的另一个开关也必须恢复；否则内存会停在
        // 「两个都未开启」的状态，与写入前不一致。
        var rollbackFree = Controller();
        rollbackFree.State.CapsuleCollapseAllActiveQueues["|right"] = true;
        var rollbackFreeService = SettingsFor(rollbackFree, () => false,
            _ => throw new Exception("Failed writes must not publish."));
        Throws<PaperSettingsException>(
            () => rollbackFreeService.Set("capsule.free_queues", Json(true)), "save_failed");
        Check(rollbackFree.State.UseCapsuleCollapseAll && !rollbackFree.State.UseCapsuleFreeQueues &&
              rollbackFree.State.CapsuleCollapseAllActiveQueues.ContainsKey("|right"),
            "A failed free-queue write restores both exclusive flags and collapse state.");

        var rollbackMaster = Controller();
        rollbackMaster.State.UseCapsuleCollapseAll = false;
        rollbackMaster.State.UseCapsuleFreeQueues = true;
        var rollbackMasterService = SettingsFor(rollbackMaster, () => false,
            _ => throw new Exception("Failed writes must not publish."));
        Throws<PaperSettingsException>(
            () => rollbackMasterService.Set("capsule.master_enabled", Json(true)), "save_failed");
        Check(!rollbackMaster.State.UseCapsuleCollapseAll && rollbackMaster.State.UseCapsuleFreeQueues,
            "A failed master-capsule write restores the free-queue switch it turned off.");

        // 审查第 6 点：free 已是 true 时单独关掉 edge，再写 free=true 不能被判为 no-op；
        // 否则 API 报告成功，但运行期仍不可用，重启还会被 StateStore 清掉。
        var reenable = Controller();
        var reenableCommits = 0;
        var reenablePublishes = 0;
        var reenableService = SettingsFor(
            reenable,
            () => { reenableCommits++; return true; },
            _ => reenablePublishes++);
        Check(reenableService.Set("capsule.free_queues", Json(true)).Changed,
            "The first free-queue enable must change state.");
        Check(reenableService.Set("capsule.edge_enabled", Json(false)).Changed,
            "Disabling edge mode must change state.");
        Check(reenable.State.UseCapsuleFreeQueues && !reenable.State.UseDeepCapsuleMode,
            "The fixture must leave free queues marked on while edge mode is off.");

        var commitsBeforeRepair = reenableCommits;
        var publishesBeforeRepair = reenablePublishes;
        var repaired = reenableService.Set("capsule.free_queues", Json(true));
        Check(repaired.Changed,
            "Re-enabling free queues must repair missing prerequisites.");
        Check(repaired.PreviousValue.GetBoolean(),
            "PreviousValue reports the pre-write value even when the repair changed other flags.");
        Check(reenable.State.UseCapsuleFreeQueues && reenable.State.UseCapsuleMode &&
              reenable.State.UseDeepCapsuleMode && !reenable.State.UseCapsuleCollapseAll,
            "Re-enabling free queues restores the full composite invariant.");
        Check(reenableCommits == commitsBeforeRepair + 1 &&
              reenablePublishes == publishesBeforeRepair + 1,
            "The repair writes and publishes exactly once.");

        Check(!reenableService.Set("capsule.free_queues", Json(true)).Changed &&
              reenableCommits == commitsBeforeRepair + 1 &&
              reenablePublishes == publishesBeforeRepair + 1,
            "Once the composite state holds, a repeated true write is a real no-op.");

        var freeFalse = Controller();
        var freeFalseCommits = 0;
        var freeFalseService = SettingsFor(
            freeFalse,
            () => { freeFalseCommits++; return true; },
            _ => { });
        Check(!freeFalseService.Set("capsule.free_queues", Json(false)).Changed &&
              freeFalseCommits == 0,
            "Writing false while free queues are already off remains a no-op.");

        // master_enabled 共享同一条级联路径，存在对称缺陷。
        var masterRepair = Controller();
        var masterService = SettingsFor(
            masterRepair,
            () => true,
            _ => { });
        Check(!masterService.Set("capsule.master_enabled", Json(true)).Changed,
            "The default master state is a truthful no-op.");
        Check(masterService.Set("capsule.edge_enabled", Json(false)).Changed,
            "Disabling edge mode must change state for the master repair fixture.");
        Check(masterService.Set("capsule.master_enabled", Json(true)).Changed &&
              masterRepair.State.UseCapsuleMode &&
              masterRepair.State.UseDeepCapsuleMode,
            "Re-enabling the master capsule must repair missing prerequisites.");
        Check(!masterService.Set("capsule.master_enabled", Json(true)).Changed,
            "A repeated master true write is a no-op once the composite state holds.");

        // capsule.edge_enabled 也存在对称缺口：capsule 缺失但 edge 已是 true 时，
        // 只看 getter 会把写入判成 no-op，跳过 Apply 的 capsule 前置修复。
        var edgeRepair = Controller();
        edgeRepair.State.UseCapsuleMode = false;
        edgeRepair.State.UseDeepCapsuleMode = true;
        var edgeRepairCommits = 0;
        var edgeRepairService = SettingsFor(
            edgeRepair,
            () => { edgeRepairCommits++; return true; },
            _ => { });
        Check(
            edgeRepairService.Set("capsule.edge_enabled", Json(true)).Changed &&
            edgeRepair.State.UseCapsuleMode &&
            edgeRepair.State.UseDeepCapsuleMode,
            "Writing edge=true must repair a missing capsule prerequisite.");
        Check(
            !edgeRepairService.Set("capsule.edge_enabled", Json(true)).Changed &&
            edgeRepairCommits == 1,
            "A repeated edge true write is a no-op once capsule is on.");

        // 修复写入的级联字段也必须参与回滚。
        var repairFail = Controller();
        repairFail.State.UseCapsuleFreeQueues = true;
        repairFail.State.UseCapsuleMode = true;
        repairFail.State.UseDeepCapsuleMode = false;
        repairFail.State.UseCapsuleCollapseAll = false;
        var repairFailService = SettingsFor(repairFail, () => false,
            _ => throw new Exception("Failed writes must not publish."));
        Throws<PaperSettingsException>(
            () => repairFailService.Set("capsule.free_queues", Json(true)), "save_failed");
        Check(repairFail.State.UseCapsuleFreeQueues && !repairFail.State.UseDeepCapsuleMode,
            "A failed prerequisite repair must roll back the cascaded flags.");
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
            editor = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");
            Check(c.State.ExternalMarkdownExtension == ".txt" && persistedExtension == ".txt" && editor.Text == ".txt",
                "External write updates live editor without writing its old value back.");
            Check(editor.IsKeyboardFocusWithin, "External extension update preserves keyboard focus.");
            editor.Text = ".draft";
            service.Set("todo.auto_clear_completed", Json(!c.State.AutoClearCompletedTodos));
            service.Set("note.edit_animations", Json(!c.State.MarkdownEditAnimationEnabled));
            DrainSettingsUi();
            editor = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");
            Check(editor.IsKeyboardFocusWithin && editor.Text == ".draft" &&
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
            Check(refreshes == 1, "Theme changes notify existing consumers once.");
            var replacement = ReadField<TextBox>(c, "_settingsExternalMarkdownTextBox");
            Check(replacement.Text == ".rst", "Whole-tree theme refresh preserves the accepted extension.");
            if (!ReferenceEquals(editor, replacement))
            {
                editor.Text = ".stale";
                editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, editor, replacement)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                Check(c.State.ExternalMarkdownExtension == ".rst" && replacement.Text == ".rst", "Late focus event from a detached editor is ignored.");
            }
        }
        finally
        {
            Field(c, "_settingsWindow", null!);
            window.Close();
            DrainSettingsUi();
        }
    }
}
