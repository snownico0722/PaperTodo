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
                SettingsSectionLabel(Strings.Get("SettingsScriptCapsule")),
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
        State.MarkdownRenderMode = MarkdownRenderModes.Basic;
        State.MarkdownEditAnimationEnabled = true;
        State.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Default;
        State.AutoCompressLargeImages = true;
        State.UsePersistentPowerShellProcess = false;
        State.PreferPowerShell7 = true;
        State.HideScriptRunWindow = true;
        _imageStore.AutoCompressLargeImages = true;

        PaperWindow.StopPersistentScriptProcesses();
        foreach (var window in _windows.Values)
        {
            window.UpdateMarkdownRenderMode();
            window.UpdateMarkdownEditAnimation();
            window.UpdateExternalMarkdownExtension();
        }

        SaveNow();
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }
}
