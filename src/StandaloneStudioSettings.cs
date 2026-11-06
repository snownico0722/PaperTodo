using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo;

internal static class StandaloneStudioSettings
{
    // Read only settings from the committed main workspace. Never construct StateStore,
    // deserialize Papers, perform recovery/migration writes or initialize a workspace.
    internal static AppState Load()
    {
        var settings = new AppState();
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "data.json");
        if (!File.Exists(path)) return settings;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var property in typeof(AppState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.SetMethod?.IsPublic == true && WorkspaceFieldScopes.Inherits(property.Name)))
            {
                var name = StateStore.JsonOptions.PropertyNamingPolicy!.ConvertName(property.Name);
                if (document.RootElement.TryGetProperty(name, out var value))
                    property.SetValue(settings, value.Deserialize(property.PropertyType, StateStore.JsonOptions));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine($"Studio settings unavailable: {ex.Message}");
        }
        return settings;
    }

    internal static void Apply(AppState settings)
    {
        Theme.SettingsOverride = settings;
        Theme.Invalidate();
        AppTypography.Configure(settings.UiFontPreset, settings.Zoom,
            settings.CustomFontEnhancedBold, settings.TextRenderingProfile);
        NoteTypography.Configure(settings.NoteTextSize, settings.NoteTextBold);
    }

    internal static void Show(Window owner, AppState settings, Action apply)
    {
        var dialog = new Window
        {
            Owner = owner, Title = Strings.Get("TraySettings"), Width = 430,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Theme.PaperBrush, Foreground = Theme.TextBrush,
            FontFamily = AppTypography.UiFontFamily, FontSize = AppTypography.Scale(13)
        };
        var body = new StackPanel { Margin = new Thickness(22) };
        var themedControls = new List<Control>();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        title.Children.Add(new TextBlock { Text = Strings.Get("TraySettings"), FontSize = 19, FontWeight = FontWeights.SemiBold });
        var temporary = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), ToolTip = Strings.Get("StudioTemporarySettingsTip")
        };
        ToolTipPreferences.SetAlwaysEnabled(temporary, true);
        temporary.Children.Add(new TextBlock { Text = Strings.Get("StudioTemporary"), Foreground = Theme.WeakTextBrush, FontSize = 11 });
        var notice = new Grid { Width = 14, Height = 14, Margin = new Thickness(5, 0, 0, 0) };
        notice.Children.Add(new Ellipse { Stroke = Theme.WeakTextBrush, StrokeThickness = 1.1 });
        notice.Children.Add(new TextBlock { Text = "!", Foreground = Theme.WeakTextBrush, FontWeight = FontWeights.Bold,
            FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        temporary.Children.Add(notice);
        title.Children.Add(temporary);
        body.Children.Add(title);
        void Changed()
        {
            apply();
            dialog.Background = Theme.PaperBrush;
            dialog.Foreground = Theme.TextBrush;
            foreach (var control in themedControls)
            {
                control.Foreground = Theme.TextBrush;
                control.Background = Theme.PaperBrush;
                control.BorderBrush = Theme.PaperBorderBrush;
            }
            ((TextBlock)temporary.Children[0]).Foreground = Theme.WeakTextBrush;
            ((Ellipse)notice.Children[0]).Stroke = Theme.WeakTextBrush;
            ((TextBlock)notice.Children[1]).Foreground = Theme.WeakTextBrush;
        }
        void Choice(string resource, (string Id, string Label)[] choices, string selected, Action<string> change)
        {
            body.Children.Add(new TextBlock { Text = Strings.Get(resource), Margin = new Thickness(0, 0, 0, 5) });
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 14), MinHeight = 28,
                ItemsSource = choices.Select(choice => new ComboBoxItem { Content = Strings.Get(choice.Label), Tag = choice.Id }).ToArray() };
            themedControls.Add(combo);
            foreach (var item in combo.Items.Cast<ComboBoxItem>()) themedControls.Add(item);
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(choices, choice => choice.Id == selected));
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem item) { change((string)item.Tag); Changed(); } };
            body.Children.Add(combo);
        }
        Choice("StudioTheme", [("system", "ThemeSystem"), ("light", "ThemeLight"), ("dark", "ThemeDark")], settings.Theme, value => settings.Theme = value);
        Choice("TrayMarkdownRenderMode", [(MarkdownRenderModes.Off, "MarkdownRenderOff"), (MarkdownRenderModes.Basic, "MarkdownRenderBasic"),
            (MarkdownRenderModes.Full, "MarkdownRenderFull")], settings.MarkdownRenderMode, value => settings.MarkdownRenderMode = value);
        Choice("SettingsImageReferenceText", [(ImageReferenceTextModes.Always, "ImageReferenceTextAlways"),
            (ImageReferenceTextModes.Editing, "ImageReferenceTextEditing"), (ImageReferenceTextModes.Hidden, "ImageReferenceTextHidden")],
            settings.ImageReferenceTextMode, value => settings.ImageReferenceTextMode = value);
        Choice("StudioTextSize", [(VisualTextSizes.Small, "StudioSizeSmall"), (VisualTextSizes.Medium, "StudioSizeMedium"),
            (VisualTextSizes.Large, "StudioSizeLarge")], settings.NoteTextSize, value => settings.NoteTextSize = value);
        var bold = new CheckBox { Content = Strings.Get("StudioBoldText"), IsChecked = settings.NoteTextBold, Margin = new Thickness(0, 0, 0, 14) };
        bold.Checked += (_, _) => { settings.NoteTextBold = true; Changed(); };
        bold.Unchecked += (_, _) => { settings.NoteTextBold = false; Changed(); };
        body.Children.Add(bold);
        var done = new Button { Content = Strings.Get("CommonOk"), MinWidth = 86, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right };
        done.Click += (_, _) => dialog.Close();
        body.Children.Add(done);
        themedControls.Add(done);
        dialog.Content = body;
        Changed();
        AppTypography.ApplyTextRendering(dialog);
        dialog.ShowDialog();
    }
}
