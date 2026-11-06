using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal static class ScriptShortcutDialog
{
    // Editing only returns text. Execution belongs exclusively to the run command.
    internal static string? Edit(Window? owner, string text, bool readOnly = false)
    {
        if (owner?.IsVisible != true) owner = null;
        var dialog = new Window
        {
            Owner = owner,
            Title = Strings.Get("TopBarEditScript"),
            Width = 620, Height = 420, MinWidth = 380, MinHeight = 270,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = owner == null,
            Topmost = owner?.Topmost ?? false,
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            UseLayoutRounding = true
        };
        AppTypography.ApplyTextRendering(dialog);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Strings.Get("TopBarEditScript"), Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(16), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };
        layout.Children.Add(title);
        var hint = new TextBlock
        {
            Text = Strings.Get(readOnly ? "WorkspaceSettingsReadOnly" : "TopBarScriptHint"), Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(12), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hint, 1);
        layout.Children.Add(hint);
        var editor = new TextBox
        {
            Text = text, IsReadOnly = readOnly, AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Foreground = Theme.TextBrush, CaretBrush = Theme.TextBrush,
            Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1), Padding = new Thickness(10),
            FontFamily = new FontFamily("Consolas"), FontSize = AppTypography.Scale(13),
            IsUndoEnabled = true
        };
        Grid.SetRow(editor, 2);
        layout.Children.Add(editor);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = PaperNoticeDialog.CreateButton(Strings.Get("CommonCancel"), primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = PaperNoticeDialog.CreateButton(Strings.Get("TopBarScriptSave"), primary: true);
        save.IsEnabled = !readOnly;
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        dialog.Content = new Border
        {
            Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18), Child = layout
        };
        dialog.Loaded += (_, _) => editor.Focus();
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (!readOnly && e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                dialog.DialogResult = true;
                e.Handled = true;
            }
        };
        return dialog.ShowDialog() == true ? editor.Text : null;
    }
}
