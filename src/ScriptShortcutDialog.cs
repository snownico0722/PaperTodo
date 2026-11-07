using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
            ResizeMode = ResizeMode.CanResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = owner == null,
            Topmost = owner?.Topmost ?? false,
            FontFamily = AppTypography.UiFontFamily,
            Language = AppTypography.Language,
            UseLayoutRounding = true
        };
        // Without a visible resize grip, this borderless window handles its own edge hits.
        dialog.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(dialog) is HwndSource source)
                source.AddHook(ResizeHitTest);
        };
        AppTypography.ApplyTextRendering(dialog);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Background = Brushes.Transparent
        };
        var title = new TextBlock
        {
            Text = Strings.Get("TopBarEditScript"), Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(16), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var help = AppController.CreateSettingsHintGlyph("TipTopBarRunScript", new Thickness(5, 0, 0, 0));
        titleRow.Children.Add(title);
        titleRow.Children.Add(help);
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (!help.IsMouseOver && e.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };
        layout.Children.Add(titleRow);
        if (readOnly)
        {
            var hint = new TextBlock
            {
                Text = Strings.Get("WorkspaceSettingsReadOnly"), Foreground = Theme.WeakTextBrush,
                FontSize = AppTypography.Scale(12), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(hint, 1);
            layout.Children.Add(hint);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    private static IntPtr ResizeHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0084 || !GetWindowRect(hwnd, out var bounds))
            return IntPtr.Zero;

        var position = lParam.ToInt64();
        var x = unchecked((short)(position & 0xFFFF));
        var y = unchecked((short)((position >> 16) & 0xFFFF));
        if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
            return IntPtr.Zero;

        var scale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var edge = (int)Math.Ceiling(6 * Math.Max(1.0, scale));
        var left = x < bounds.Left + edge;
        var right = x >= bounds.Right - edge;
        var top = y < bounds.Top + edge;
        var bottom = y >= bounds.Bottom - edge;
        var hit = top && left ? 13
            : top && right ? 14
            : bottom && left ? 16
            : bottom && right ? 17
            : left ? 10
            : right ? 11
            : top ? 12
            : bottom ? 15
            : 0;
        if (hit == 0)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(hit);
    }

}
