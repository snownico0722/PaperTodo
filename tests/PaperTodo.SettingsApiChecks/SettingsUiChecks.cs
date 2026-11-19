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
        // Representative entry points cover each value kind plus mutations with side effects.
        // Ordinary one-line setting aliases do not need a permanent per-setter matrix.
        foreach (var (method, id, hasArgument) in new[]
        {
            ("ToggleAnimations", "appearance.animations", false),
            ("SetTheme", "appearance.theme", true),
            ("SetOverallFontScale", "appearance.font_scale", true),
            ("SetExternalMarkdownExtension", "note.external_extension", true),
            ("SetMaxTitleLength", "title.max_length", true),
            ("ToggleAutoMoveCompletedTodosToBottom", "todo.move_completed_to_bottom", false),
            ("ToggleHidePapersFromWindowSwitcher", "window.hide_from_switcher", false),
            ("ToggleCapsuleCollapseAll", "capsule.master_enabled", false)
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
        Check(title.State.MaxTitleLength == 2 && title.State.Papers[0].Title == "abcdef" &&
              title.PaperDisplayTitle(title.State.Papers[0]) == "abcd", "UI title limits only change displayed text.");
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
        Field(c, "_settingsPage", Enum.Parse(page, "NotesAndTodos"));
        var root = (UIElement)Invoke(c, "BuildSettingsSidebarNotesAndTodosPage")!;
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
            c.State.ShowLinkedPaperName = true;
            c.State.EnableTodoPaperLinks = false;
            Invoke(c, "RefreshSettingsForChange", "todo.paper_links");
            DrainSettingsUi();
            Check(c.State.ShowLinkedPaperName && !SettingsControls<CheckBox>(window).Any(box =>
                    Equals(box.Content, Strings.Get("SettingsShowLinkedPaperName"))),
                "disabling links hides dependent controls without resetting their saved values");
            c.State.EnableTodoPaperLinks = true;
            Invoke(c, "RefreshSettingsForChange", "todo.paper_links");
            DrainSettingsUi();
            Check(SettingsControls<CheckBox>(window).Any(box => box.IsVisible && box.IsChecked == true &&
                    Equals(box.Content, Strings.Get("SettingsShowLinkedPaperName"))),
                "reenabling links restores the saved child choice immediately");
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

    private static IEnumerable<T> SettingsControls<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in SettingsControls<T>(child)) yield return nested;
        }
    }

    private static void PaperBackgroundSettingsVisibility()
    {
        var candidateImages = new[] { "papertodo.png", "papertodo.jpg", "papertodo.jpeg" };
        var hasBackgroundImage = candidateImages.Any(name =>
            System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, name)));

        var controller = Controller();
        Field(controller, "_settingsRegionRefreshers", new Dictionary<string, Action>());
        var page = (UIElement)Invoke(controller, "BuildVisualSettingsPage")!;
        var window = new Window { Content = page, Width = 900, Height = 760 };
        Field(controller, "_settingsWindow", window);
        try
        {
            window.Show();
            window.Activate();
            window.UpdateLayout();
            DrainSettingsUi();

            var labels = SettingsControls<TextBlock>(window).Select(label => label.Text).ToArray();
            Check(labels.Any(label => label is "纸片背景" or "Paper background" or "紙面背景" or "종이 배경"),
                "Paper background settings stay visible at the bottom of Style & Text without an image.");

            if (!hasBackgroundImage)
            {
                Check(labels.Any(label => label is "未启用" or "Not enabled" or "未有効" or "사용 안 함"),
                    "The always-visible paper background section reports the no-image state.");
                Check(!SettingsControls<CheckBox>(window).Any(toggle =>
                        toggle.Content is string label &&
                        label is "启用背景色混合" or "Enable background color blending" or
                            "背景色のブレンドを有効化" or "배경색 혼합 사용"),
                    "Detailed paper background options stay hidden until an image loads successfully.");
            }
        }
        finally
        {
            Field(controller, "_settingsWindow", null!);
            window.Close();
            DrainSettingsUi();
        }
    }

    private static void LineSpacingAdvancedModeVisibility()
    {
        var controller = Controller();
        Field(controller, "_settingsRegionRefreshers", new Dictionary<string, Action>());
        controller.State.NoteLineSpacing = "large";
        controller.State.TodoItemSpacing = "small";
        controller.State.AdvancedSettingsMode = false;

        var window = new Window { Width = 900, Height = 760 };
        Field(controller, "_settingsWindow", window);
        try
        {
            window.Content = Invoke(controller, "BuildVisualSettingsPage");
            window.Show();
            window.Activate();
            window.UpdateLayout();
            DrainSettingsUi();

            var normalSelectors = SettingsControls<ListBox>(window).ToArray();
            Check(normalSelectors.Length == 1 && Equals(normalSelectors[0].SelectedValue, "small"),
                "Normal mode hides note line spacing but keeps to-do item spacing visible.");

            controller.State.AdvancedSettingsMode = true;
            window.Content = Invoke(controller, "BuildVisualSettingsPage");
            window.UpdateLayout();
            DrainSettingsUi();

            var advancedSelectors = SettingsControls<ListBox>(window).ToArray();
            Check(advancedSelectors.Length == 2 &&
                  Equals(advancedSelectors[0].SelectedValue, "large") &&
                  Equals(advancedSelectors[1].SelectedValue, "small"),
                "Advanced mode reveals the saved note line spacing without changing either spacing value.");

            controller.State.AdvancedSettingsMode = false;
            window.Content = Invoke(controller, "BuildVisualSettingsPage");
            window.UpdateLayout();
            DrainSettingsUi();
            Check(SettingsControls<ListBox>(window).Count() == 1 &&
                  controller.State.NoteLineSpacing == "large",
                "Hiding note line spacing in normal mode preserves its saved value.");
        }
        finally
        {
            Field(controller, "_settingsWindow", null!);
            window.Close();
            DrainSettingsUi();
        }
    }

    private static void SpacingKeyboardBehavior()
    {
        var controller = Controller();
        Field(controller, "_settingsRegionRefreshers", new Dictionary<string, Action>());
        SettingsFor(controller, () => true, action => action());
        var panel = new StackPanel();
        var start = new Button { Content = "Start" };
        panel.Children.Add(start);
        panel.Children.Add((UIElement)Invoke(controller, "CreateSpacingRow", "SettingsLineSpacing",
            (Func<string>)(() => controller.State.NoteLineSpacing), "note.line_spacing")!);
        panel.Children.Add((UIElement)Invoke(controller, "CreateSpacingRow", "SettingsItemSpacing",
            (Func<string>)(() => controller.State.TodoItemSpacing), "todo.item_spacing")!);
        var window = new Window { Content = panel, Width = 350, Height = 200 };
        Field(controller, "_settingsWindow", window);
        try
        {
            window.Show();
            window.Activate();
            DrainSettingsUi();
            var lists = SettingsControls<ListBox>(panel).ToArray();
            start.Focus();
            start.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(lists[0].IsKeyboardFocusWithin, "Tab reaches note spacing");
            Press(Key.Right);
            DrainSettingsUi();
            Check(controller.State.NoteLineSpacing == "large" && lists[0].IsKeyboardFocusWithin,
                "Right changes spacing and keeps keyboard focus after persistence");
            Press(Key.Left);
            DrainSettingsUi();
            Check(controller.State.NoteLineSpacing == "medium", "consecutive arrow keys work");
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(lists[1].IsKeyboardFocusWithin, "the next Tab reaches to-do spacing");
            Press(Key.Left);
            DrainSettingsUi();
            Check(controller.State.TodoItemSpacing == "small" && controller.State.NoteLineSpacing == "medium",
                "keyboard adjusts the two settings independently");
        }
        finally { window.Close(); }

        void Press(Key key)
        {
            var target = (UIElement)Keyboard.FocusedElement;
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target),
                Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

    private static void WorkspaceDataSettingsBehavior()
    {
        var c = Controller();
        Field(c, "_settingsRegionRefreshers", new Dictionary<string, Action>());
        var catalog = (WorkspaceCatalog)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(WorkspaceCatalog));
        var options = new WorkspaceConfiguration { Enabled = false, Slots = [1, 2] };
        Field(catalog, "<Options>k__BackingField", options);
        Field(c, "_workspaceCatalog", catalog);
        c.State.AdvancedSettingsMode = true;
        c.State.McpEnabled = true;
        c.State.McpAllowFullWrites = true;
        var saves = 0;
        SettingsFor(c, () => { saves++; return true; }, _ => { });
        var session = ReadField<WorkspaceSession>(c, "_workspace");
        var window = new Window { Width = 820, Height = 820 };
        Field(c, "_settingsWindow", window);
        void Render()
        {
            window.Content = Invoke(c, "BuildWorkspaceSettingsPage");
            window.UpdateLayout();
            DrainSettingsUi();
        }
        CheckBox Toggle(string key) => SettingsControls<CheckBox>(window)
            .Single(box => Equals(box.Content, Strings.Get(key)));
        try
        {
            Render();
            window.Show();
            window.UpdateLayout();
            Check(window.Content is Grid { ColumnDefinitions.Count: 3 },
                "workspaces and data share a two-column layout");
            Check(Toggle("LabsMcpEnable").IsEnabled &&
                  SettingsControls<ComboBox>(window).Single().IsEnabled,
                "disabling workspaces does not disable data handling or MCP");
            Grid HintRow(string key) => SettingsControls<Grid>(window).Single(row =>
                row.Children.OfType<CheckBox>().Any(box => Equals(box.Content, Strings.Get(key))));
            Check(HintRow("LabsMcpEnable").ToolTip == null,
                "enabled setting rows show descriptions only from the info icon");
            Check(!Toggle("LabsMcpBlankWrites").IsEnabled &&
                  HintRow("LabsMcpBlankWrites").ToolTip == null,
                "disabled settings reveal descriptions only from their info icon");
            options.Enabled = true;
            Field(c, "_workspace", new WorkspaceSession(
                new WorkspacePaths(session.Paths.Directory, 2), session.Store, c.State, true, null));
            Render();
            Check(Toggle("WorkspaceEnabled").IsEnabled && !Toggle("LabsMcpEnable").IsEnabled &&
                  !SettingsControls<ComboBox>(window).Single().IsEnabled,
                "workspace management stays editable while inherited data and MCP settings are read-only");
            Check(!HintRow("LabsMcpEnable").IsEnabled &&
                  HintRow("LabsMcpEnable").ToolTip == null,
                "inherited disabled rows reveal descriptions only from their info icon");
            var disabledHints = SettingsControls<Border>(window)
                .Where(border => border.Cursor == Cursors.Help && border.ToolTip is ToolTip)
                .ToArray();
            Check(disabledHints.Length >= 2 && disabledHints.All(ToolTipService.GetShowOnDisabled),
                "disabled settings keep their info icons accessible");
            Check(SettingsControls<Button>(window).Where(button =>
                    Equals(button.Content, Strings.Get("LabsMcpCopyJson")) ||
                    Equals(button.Content, Strings.Get("LabsMcpCopyAiSkill"))).Count(button => button.IsEnabled) == 2,
                "read-only inherited MCP settings still allow copying connection information");
            var policy = c.State.DataMergeConflictPolicy;
            SettingsControls<ComboBox>(window).Single().SelectedIndex = 2;
            Invoke(c, "ToggleMcpEnabled");
            DrainSettingsUi();
            Check(saves == 0 && c.State.McpEnabled && c.State.DataMergeConflictPolicy == policy,
                "stale control events cannot mutate or save inherited preferences");
            Field(c, "_workspace", session);
            c.State.McpEnabled = false;
            Render();
            Check(SettingsControls<CheckBox>(window).Any(box =>
                    Equals(box.Content, Strings.Get("LabsMcpFullWrites")) &&
                    box.IsVisible && !box.IsEnabled) && c.State.McpAllowFullWrites,
                "MCP off greys out permissions rather than hiding or resetting them");
            c.State.UseCapsuleMode = false;
            c.State.UseDeepCapsuleMode = false;
            window.Content = Invoke(c, "BuildLabsEdgeCapsuleHoverIntentSettings");
            window.UpdateLayout();
            Check(Toggle("LabsEnableEdgeCapsuleHoverPreview").IsVisible &&
                  !Toggle("LabsEnableEdgeCapsuleHoverPreview").IsEnabled &&
                  Toggle("LabsEnableEdgeCapsuleHoverIntent").IsVisible,
                "edge browsing keeps its dependent settings visible but disabled");

            c.State.UseCapsuleMode = true;
            c.State.UseDeepCapsuleMode = false;
            c.State.EnableTodoPaperLinks = c.State.HideLinkedPapersFromCapsules = true;
            window.Content = Invoke(c, "BuildSettingsCapsuleOptions");
            window.UpdateLayout();
            Check(Toggle("SettingsCollapseExpandedDeepCapsuleOnClick").IsVisible &&
                  !SettingsControls<CheckBox>(window).Any(box =>
                    Equals(box.Content, Strings.Get("SettingsCapsuleCollapseAll"))),
                "linked-paper second-click behavior remains available without exposing disabled edge options");

            c.State.EnableTodoPaperLinks = false;
            c.State.ExperimentalWindowTethering = false;
            window.Content = Invoke(c, "BuildTopBarShortcutSettings");
            window.UpdateLayout();
            var inactiveAssociation = SettingsControls<TextBlock>(window).Single(label =>
                Equals(label.Text, Strings.Get("TopBarAssociation")));
            Check(((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(inactiveAssociation)).Opacity == 0.5,
                "the association shortcut is dimmed when neither linking nor window binding is available");
            c.State.ExperimentalWindowTethering = true;
            window.Content = Invoke(c, "BuildTopBarShortcutSettings");
            window.UpdateLayout();
            var activeAssociation = SettingsControls<TextBlock>(window).Single(label =>
                Equals(label.Text, Strings.Get("TopBarAssociation")));
            Check(((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(activeAssociation)).Opacity == 1,
                "the association shortcut becomes active with window binding alone");
            Grid ActionFrame(string key)
            {
                var label = SettingsControls<TextBlock>(window).Single(text =>
                    Equals(text.Text, Strings.Get(key)));
                DependencyObject? current = label;
                while (current != null)
                {
                    if (current is Grid { AllowDrop: true } frame) return frame;
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
                throw new InvalidOperationException("Action frame was not found: " + key);
            }
            Check(Math.Abs(ActionFrame("TopBarRunScript").ActualHeight -
                           ActionFrame("ShortcutNewTodo").ActualHeight) < 1.0,
                "the compact script Edit button must not increase the shortcut row height");
        }
        finally
        {
            Field(c, "_settingsWindow", null!);
            window.Close();
            DrainSettingsUi();
        }
    }
}
