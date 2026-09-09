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
            SettingsToggle(
                Strings.Get("SettingsAutoClearCompletedTodos"),
                State.AutoClearCompletedTodos,
                ToggleAutoClearCompletedTodos),
            "TipAutoClearCompletedTodos"));

        var autoMoveCompletedToggle = SettingsToggle(
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
            SettingsToggle(
                Strings.Get("SettingsEnableTodoPaperLinks"),
                State.EnableTodoPaperLinks,
                ToggleTodoPaperLinks),
            "TipEnableTodoPaperLinks"));

        var showLinkedPaperNameToggle = SettingsToggle(
            Strings.Get("SettingsShowLinkedPaperName"),
            State.ShowLinkedPaperName,
            ToggleLinkedPaperNameDisplay);
        showLinkedPaperNameToggle.IsEnabled = State.EnableTodoPaperLinks;
        showLinkedPaperNameToggle.Opacity = showLinkedPaperNameToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            showLinkedPaperNameToggle,
            "TipShowLinkedPaperName"));

        var allowLongLinkedPaperTitlesToggle = SettingsToggle(
            Strings.Get("SettingsAllowLongLinkedPaperTitles"),
            State.AllowLongLinkedPaperTitles,
            ToggleLongLinkedPaperTitles);
        allowLongLinkedPaperTitlesToggle.IsEnabled =
            State.EnableTodoPaperLinks && State.ShowLinkedPaperName;
        allowLongLinkedPaperTitlesToggle.Opacity =
            allowLongLinkedPaperTitlesToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            allowLongLinkedPaperTitlesToggle,
            "TipAllowLongLinkedPaperTitles"));

        var linkedPathExtensionOnlyToggle = SettingsToggle(
            Strings.Get("SettingsShowLinkedPathExtensionOnly"),
            State.ShowLinkedPathExtensionOnly,
            ToggleLinkedPathExtensionOnly);
        linkedPathExtensionOnlyToggle.IsEnabled =
            State.EnableTodoPaperLinks &&
            State.ShowLinkedPaperName &&
            !State.AllowLongLinkedPaperTitles;
        linkedPathExtensionOnlyToggle.Opacity =
            linkedPathExtensionOnlyToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            linkedPathExtensionOnlyToggle,
            "TipShowLinkedPathExtensionOnly"));

        var hideLinkedPapersFromCapsulesToggle = SettingsToggle(
            Strings.Get("SettingsHideLinkedPapersFromCapsules"),
            State.HideLinkedPapersFromCapsules,
            ToggleHideLinkedPapersFromCapsules);
        hideLinkedPapersFromCapsulesToggle.IsEnabled = State.EnableTodoPaperLinks;
        hideLinkedPapersFromCapsulesToggle.Opacity =
            hideLinkedPapersFromCapsulesToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            hideLinkedPapersFromCapsulesToggle,
            "TipHideLinkedPapersFromCapsules"));

        var runLinkedScriptCapsulesToggle = SettingsToggle(
            Strings.Get("SettingsRunLinkedScriptCapsulesOnClick"),
            State.RunLinkedScriptCapsulesOnClick,
            ToggleRunLinkedScriptCapsulesOnClick);
        runLinkedScriptCapsulesToggle.IsEnabled = State.EnableTodoPaperLinks;
        runLinkedScriptCapsulesToggle.Opacity =
            runLinkedScriptCapsulesToggle.IsEnabled ? 1.0 : 0.55;
        content.Children.Add(WrapWithHint(
            runLinkedScriptCapsulesToggle,
            "TipRunLinkedScriptCapsulesOnClick"));
        return content;
    }

    private void RestoreSettingsSidebarTodoDefaults()
    {
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
