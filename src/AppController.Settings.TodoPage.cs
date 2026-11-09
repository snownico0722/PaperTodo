using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
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
        State.ShowTodoBottomBar = true;
        State.AutoClearCompletedTodos = false;
        State.AutoMoveCompletedTodosToBottom = false;
        State.EnableTodoPaperLinks = true;
        State.ShowLinkedPaperName = false;
        State.AllowLongLinkedPaperTitles = false;
        State.ShowLinkedPathExtensionOnly = false;
        State.HideLinkedPapersFromCapsules = false;
        State.RunLinkedScriptCapsulesOnClick = false;
        State.ExperimentalTodoReminders = false;
        State.ExperimentalTodoReminderShowButton = true;
        State.ExperimentalTodoReminderQuickMinutes =
            ExperimentalTodoReminderOptions.DefaultQuickMinutes;
        State.ExperimentalTodoReminderSoundEnabled = false;
        State.ExperimentalTodoReminderSound =
            TodoReminderSoundOptions.Asterisk;

        foreach (var window in _windows.Values)
        {
            window.UpdateTodoLinkFeature();
        }
        RefreshCapsuleEligibilityForLinkedPapers();
        ArrangeDeepCapsules(animate: false);
        SaveNow();
        RefreshTodoReminderFeature();
        RefreshSettingsWindowContent();
    }
}
