using System;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement BuildSettingsSidebarNotesAndTodosPage()
    {
        var columns = new Grid { Margin = new Thickness(2, 4, 6, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var note = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        note.Children.Add(SettingsSectionLabel(Strings.Get("PaperKindNote")));
        note.Children.Add(BuildSettingsSidebarNotePage());
        columns.Children.Add(note);
        var divider = new Border
        {
            Width = 1, Background = TrayBorderBrush, Opacity = 0.4,
            Margin = new Thickness(0, 5, 0, 5)
        };
        Grid.SetColumn(divider, 1);
        columns.Children.Add(divider);
        var todo = BuildSettingsSidebarTodoPage();
        ((FrameworkElement)todo).Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(todo, 2);
        columns.Children.Add(todo);
        return columns;
    }

    private UIElement BuildSettingsSidebarNotePage()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(2, 4, 6, 0)
        };

        content.Children.Add(BuildSettingsLiveRegion("note.markdown", BuildSettingsNoteMarkdownOptions));

        content.Children.Add(SettingsSectionLabel(Strings.Get("SettingsExternalOpen")));
        content.Children.Add(WrapWithHint(
            SettingsFieldLabel(Strings.Get("SettingsExternalMarkdownExtension")),
            "TipExternalExtension"));
        content.Children.Add(CreateExternalMarkdownExtensionEditor());

        if (State.AdvancedSettingsMode)
        {
            content.Children.Add(BuildSettingsLiveRegion("note.images", () => AdvancedSettingsBlock(
                SettingsSectionLabel(
                    SettingsSidebarLocalized("图片", "Images", "画像", "이미지")),
                WrapWithHint(
                    WrappingSettingsToggle(
                        Strings.Get("SettingsAutoCompressLargeImages"),
                        State.AutoCompressLargeImages,
                        ToggleAutoCompressLargeImages),
                    "TipAutoCompressLargeImages"))));

            content.Children.Add(BuildSettingsLiveRegion("note.scripts", () => AdvancedSettingsBlock(
                SettingsSectionLabelWithHint(Strings.Get("SettingsScriptCapsule"), "TipScriptCapsule"),
                WrapWithHint(
                    WrappingSettingsToggle(
                        Strings.Get("SettingsPersistentPowerShellProcess"),
                        State.UsePersistentPowerShellProcess,
                        TogglePersistentPowerShellProcess),
                    "TipPersistentPowerShellProcess"),
                WrapWithHint(
                    WrappingSettingsToggle(
                        Strings.Get("SettingsPreferPowerShell7"),
                        State.PreferPowerShell7,
                        TogglePreferPowerShell7),
                    "TipPreferPowerShell7"),
                WrapWithHint(
                    WrappingSettingsToggle(
                        Strings.Get("SettingsHideScriptRunWindow"),
                        State.HideScriptRunWindow,
                        ToggleHideScriptRunWindow),
                    "TipHideScriptRunWindow"))));
        }

        return WithSettingsPageRestoreFooter(
            content,
            RestoreSettingsSidebarNoteDefaults);
    }

    private UIElement BuildSettingsNoteMarkdownOptions()
    {
        var content = new StackPanel();
        content.Children.Add(SettingsSectionLabel(
            SettingsSidebarLocalized("Markdown", "Markdown", "Markdown", "Markdown")));
        content.Children.Add(WrapWithHint(
            SettingsFieldLabel(Strings.Get("TrayMarkdownRenderMode")),
            "TipMarkdownRender"));

        UIElement? markdownAnimationRow = null;
        content.Children.Add(CreateSettingsSidebarMarkdownRenderSelector(isFullRender =>
        {
            if (markdownAnimationRow != null)
            {
                markdownAnimationRow.Visibility = isFullRender
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }));

        markdownAnimationRow = WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("SettingsMarkdownEditAnimation"),
                State.MarkdownEditAnimationEnabled,
                ToggleMarkdownEditAnimation),
            "TipMarkdownEditAnimation");
        markdownAnimationRow.Visibility =
            State.MarkdownRenderMode == MarkdownRenderModes.Full
                ? Visibility.Visible
                : Visibility.Collapsed;
        content.Children.Add(markdownAnimationRow);

        if (State.AdvancedSettingsMode)
        {
            content.Children.Add(AdvancedSettingsBlock(
                WrapWithHint(
                    SettingsFieldLabel(Strings.Get("SettingsImageReferenceText"), topMargin: 8),
                    "TipImageReferenceText"),
                CreateImageReferenceTextModeSelector()));
        }

        return content;
    }

    private UIElement CreateSettingsSidebarMarkdownRenderSelector(
        Action<bool> onFullModeChanged)
    {
        var segments = new[]
        {
            (MarkdownRenderModes.Off, Strings.Get("MarkdownRenderOff")),
            (MarkdownRenderModes.Basic, Strings.Get("MarkdownRenderBasic")),
            (MarkdownRenderModes.Full, Strings.Get("MarkdownRenderFull"))
        };

        return CreateSegmentSelector(
            segments,
            State.MarkdownRenderMode,
            mode =>
            {
                SetMarkdownRenderMode(mode);
                onFullModeChanged(
                    State.MarkdownRenderMode == MarkdownRenderModes.Full);
            });
    }

    private void ToggleMarkdownEditAnimation() =>
        SetSettingFromUi("note.edit_animations", !State.MarkdownEditAnimationEnabled);

    private void RestoreSettingsSidebarNoteDefaults()
    {
        var defaults = new AppState();
        State.MarkdownRenderMode = defaults.MarkdownRenderMode;
        State.ImageReferenceTextMode = defaults.ImageReferenceTextMode;
        State.MarkdownEditAnimationEnabled = defaults.MarkdownEditAnimationEnabled;
        State.ExternalMarkdownExtension = defaults.ExternalMarkdownExtension;
        State.AutoCompressLargeImages = defaults.AutoCompressLargeImages;
        State.UsePersistentPowerShellProcess = defaults.UsePersistentPowerShellProcess;
        State.PreferPowerShell7 = defaults.PreferPowerShell7;
        State.HideScriptRunWindow = defaults.HideScriptRunWindow;
        PublishSettingEffects(SettingEffects.Compress);
        PublishSettingEffects(SettingEffects.Scripts);
        RefreshWindowSettingEffects(SettingEffects.Markdown, SettingEffects.ImageReferences,
            SettingEffects.MarkdownAnimations, SettingEffects.ExternalExtension);

        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private UIElement BuildSettingsSidebarTodoPage()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(2, 4, 6, 0)
        };
        content.Children.Add(SettingsSectionLabel(Strings.Get("SettingsTodoPaper")));
        content.Children.Add(BuildSettingsLiveRegion(
            "general.todos",
            BuildSettingsSidebarTodoOptions));
        content.Children.Add(SettingsSectionLabel(Strings.Get("LabsTodoReminders")));
        content.Children.Add(BuildSettingsLiveRegion(
            "labs.reminders",
            BuildLabsTodoReminderSettings));

        return WithSettingsPageRestoreFooter(
            content,
            RestoreSettingsSidebarTodoDefaults);
    }

    private UIElement BuildSettingsSidebarTodoOptions()
    {
        var content = new StackPanel();
        content.Children.Add(WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("SettingsShowTodoBottomBar"),
                State.ShowTodoBottomBar,
                ToggleTodoBottomBar),
            "TipShowTodoBottomBar"));
        content.Children.Add(WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("SettingsAutoClearCompletedTodos"),
                State.AutoClearCompletedTodos,
                ToggleAutoClearCompletedTodos),
            "TipAutoClearCompletedTodos"));

        var autoMoveCompletedToggle = WrappingSettingsToggle(
            Strings.Get("SettingsAutoMoveCompletedTodosToBottom"),
            State.AutoMoveCompletedTodosToBottom,
            ToggleAutoMoveCompletedTodosToBottom);
        autoMoveCompletedToggle.IsEnabled = !State.AutoClearCompletedTodos;
        autoMoveCompletedToggle.Opacity =
            autoMoveCompletedToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            autoMoveCompletedToggle,
            "TipAutoMoveCompletedTodosToBottom"));

        content.Children.Add(SettingsSectionLabel(
            SettingsSidebarLocalized("纸片关联", "Paper links", "紙片リンク", "종이 연결")));
        content.Children.Add(WrapWithHint(
            WrappingSettingsToggle(
                Strings.Get("SettingsEnableTodoPaperLinks"),
                State.EnableTodoPaperLinks,
                ToggleTodoPaperLinks),
            "TipEnableTodoPaperLinks"));

        if (State.EnableTodoPaperLinks)
        {
            content.Children.Add(WrapWithHint(
                WrappingSettingsToggle(Strings.Get("SettingsShowLinkedPaperName"),
                    State.ShowLinkedPaperName, ToggleLinkedPaperNameDisplay),
                "TipShowLinkedPaperName"));
            if (State.ShowLinkedPaperName)
            {
                content.Children.Add(WrapWithHint(
                    WrappingSettingsToggle(Strings.Get("SettingsAllowLongLinkedPaperTitles"),
                        State.AllowLongLinkedPaperTitles, ToggleLongLinkedPaperTitles),
                    "TipAllowLongLinkedPaperTitles"));
                if (!State.AllowLongLinkedPaperTitles)
                    content.Children.Add(WrapWithHint(
                        WrappingSettingsToggle(Strings.Get("SettingsShowLinkedPathExtensionOnly"),
                            State.ShowLinkedPathExtensionOnly, ToggleLinkedPathExtensionOnly),
                        "TipShowLinkedPathExtensionOnly"));
            }
            content.Children.Add(WrapWithHint(
                WrappingSettingsToggle(Strings.Get("SettingsHideLinkedPapersFromCapsules"),
                    State.HideLinkedPapersFromCapsules, ToggleHideLinkedPapersFromCapsules),
                "TipHideLinkedPapersFromCapsules"));
            content.Children.Add(WrapWithHint(
                WrappingSettingsToggle(Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"),
                    State.RunLinkedScriptCapsulesOnClick, ToggleRunLinkedScriptCapsulesOnClick),
                "TipRunLinkedScriptCapsulesOnClick"));
        }
        return content;
    }

    private void ToggleLinkedPathExtensionOnly() =>
        SetSettingFromUi("todo.linked_path_extension_only", !State.ShowLinkedPathExtensionOnly);

    private void RestoreSettingsSidebarTodoDefaults()
    {
        var defaults = new AppState();
        State.ShowTodoBottomBar = defaults.ShowTodoBottomBar;
        State.AutoClearCompletedTodos = defaults.AutoClearCompletedTodos;
        State.AutoMoveCompletedTodosToBottom = defaults.AutoMoveCompletedTodosToBottom;
        State.EnableTodoPaperLinks = defaults.EnableTodoPaperLinks;
        State.ShowLinkedPaperName = defaults.ShowLinkedPaperName;
        State.AllowLongLinkedPaperTitles = defaults.AllowLongLinkedPaperTitles;
        State.ShowLinkedPathExtensionOnly = defaults.ShowLinkedPathExtensionOnly;
        State.HideLinkedPapersFromCapsules = defaults.HideLinkedPapersFromCapsules;
        State.RunLinkedScriptCapsulesOnClick = defaults.RunLinkedScriptCapsulesOnClick;
        State.ExperimentalTodoReminders = defaults.ExperimentalTodoReminders;
        State.ExperimentalTodoReminderShowButton = defaults.ExperimentalTodoReminderShowButton;
        State.ExperimentalTodoReminderQuickMinutes = defaults.ExperimentalTodoReminderQuickMinutes;
        State.ExperimentalTodoReminderSoundEnabled = defaults.ExperimentalTodoReminderSoundEnabled;
        State.ExperimentalTodoReminderSound = defaults.ExperimentalTodoReminderSound;

        RefreshWindowSettingEffects(SettingEffects.TodoLinks);
        RefreshCapsuleEligibilityForLinkedPapers();
        ArrangeDeepCapsules(animate: false);
        SaveNow();
        PublishSettingEffects(SettingEffects.Reminders);
        RefreshSettingsWindowContent();
    }
}
