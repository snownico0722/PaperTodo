using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

// This dialog owns a draft only. Its callback commits through the application's
// existing state transaction before a successful Save is allowed to close it.
internal sealed class TodoColorSlotEditor : Window
{
    internal TodoColorSlotEditor(TodoColorSlot original, Func<TodoColorSlot, bool> save)
    {
        Title = Strings.Get("TodoColorsEdit");
        Width = Math.Min(AppTypography.Scale(500), Math.Max(280, SystemParameters.WorkArea.Width - 32));
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.PaperBrush;
        Foreground = Theme.TextBrush;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
        Language = AppTypography.Language;
        ThemePalette.SetScope(this, Theme.Global);
        AppTypography.ApplyTextRendering(this);

        var draft = original.Copy();
        var root = new StackPanel { Margin = new Thickness(20) };
        void Label(string key) => root.Children.Add(new TextBlock
        {
            Text = Strings.Get(key), Margin = new Thickness(0, 8, 0, 5), Foreground = Theme.TextBrush
        });
        TextBox Input(string text, int max) => new()
        {
            Text = text, MaxLength = max, Padding = new Thickness(7),
            Background = Theme.CodeBrush, Foreground = Theme.TextBrush, CaretBrush = Theme.TextBrush,
            BorderBrush = Theme.PaperBorderBrush, BorderThickness = new Thickness(1)
        };
        Label("TodoColorsName");
        var name = Input(TodoColorSlots.DisplayName(draft), 40);
        name.Name = "PaletteName";
        root.Children.Add(name);
        Label("TodoColorsPreset");
        var presets = ColorSchemes.All.Select(id => new KeyValuePair<string, string>(id,
            TodoColorSlots.DisplayName(new TodoColorSlot { Preset = id }))).ToList();
        presets.Add(new("custom", Strings.Get("TodoColorsCustom")));
        var preset = new ComboBox
        {
            Name = "PaletteSource", ItemsSource = presets, DisplayMemberPath = "Value", SelectedValuePath = "Key",
            SelectedValue = draft.PrimaryColor == null ? draft.Preset : "custom", Padding = new Thickness(5)
        };
        PaperSelectControl.ApplyAppTheme(preset, FontSize);
        root.Children.Add(preset);
        Label("TodoColorsPrimary");
        var colorRow = new DockPanel();
        var choose = TodoReminderDialogControls.Button(Strings.Get("TodoColorsPick"));
        choose.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(choose, Dock.Right); colorRow.Children.Add(choose);
        var hex = Input(draft.PrimaryColor ?? BrushHex(ThemePalette.ForSlot(draft, false).ActiveBrush), 7);
        hex.Name = "PrimaryColor";
        colorRow.Children.Add(hex);
        root.Children.Add(colorRow);
        var previews = new Grid { Margin = new Thickness(0, 16, 0, 6) };
        previews.ColumnDefinitions.Add(new ColumnDefinition()); previews.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(previews);
        var error = new TextBlock { Foreground = Theme.DangerBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) };
        root.Children.Add(error);

        bool ReadDraft()
        {
            var displayName = name.Text.Trim();
            draft.Name = displayName == TodoColorSlots.DisplayName(original) ? original.Name : displayName;
            var custom = (string?)preset.SelectedValue == "custom";
            draft.Preset = custom ? null : (string?)preset.SelectedValue;
            draft.PrimaryColor = custom ? hex.Text.Trim().ToUpperInvariant() : null;
            colorRow.IsEnabled = custom;
            var valid = TodoColorSlots.IsValid(draft);
            error.Text = valid ? "" : Strings.Get("TodoColorsInvalid");
            return valid;
        }
        void Preview()
        {
            if (!ReadDraft()) return;
            previews.Children.Clear();
            for (var i = 0; i < 2; i++)
            {
                var palette = ThemePalette.ForSlot(draft, i == 1);
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = Strings.Get(i == 0 ? "ThemeLight" : "ThemeDark"), Foreground = palette.WeakTextBrush, Margin = new Thickness(0, 0, 0, 8) });
                var todo = new TextBlock { Text = "□  " + Strings.Get("TodoColorsSample"), Foreground = palette.TextBrush, TextWrapping = TextWrapping.Wrap };
                content.Children.Add(todo);
                content.Children.Add(new TextBlock { Text = "✓  " + Strings.Get("TodoColorsCompleted"), Foreground = palette.WeakTextBrush, TextDecorations = TextDecorations.Strikethrough, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
                var card = new Border
                {
                    Background = palette.PaperBrush, BorderBrush = palette.ActiveBrush, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7), Padding = new Thickness(12),
                    Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 0 ? 5 : 0, 0), Child = content
                };
                Grid.SetColumn(card, i); previews.Children.Add(card);
            }
        }
        preset.SelectionChanged += (_, _) => Preview();
        hex.TextChanged += (_, _) => Preview();
        name.TextChanged += (_, _) => ReadDraft();
        choose.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (TodoColorSlots.TryColor(hex.Text.Trim(), out var color))
                picker.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
            if (picker.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
                hex.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = TodoReminderDialogControls.Button(Strings.Get("TodoColorsSave"), primary: true);
        accept.Name = "SavePalette"; accept.IsDefault = true; accept.Margin = new Thickness(0, 0, 8, 0);
        accept.Click += (_, _) =>
        {
            if (!ReadDraft()) return;
            if (save(draft.Copy())) DialogResult = true;
            else error.Text = Strings.Get("TodoColorsSaveFailed");
        };
        var cancel = TodoReminderDialogControls.Button(Strings.Get("CommonCancel"));
        cancel.Name = "CancelPalette"; cancel.IsCancel = true;
        buttons.Children.Add(accept); buttons.Children.Add(cancel); root.Children.Add(buttons);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Preview();
    }

    private static string BrushHex(Brush brush) => brush is SolidColorBrush solid
        ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "#588260";
    private sealed class DialogOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }
}
