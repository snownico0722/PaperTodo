using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal static class PaperNoticeDialog
{
    public static void Show(Window owner, string titleText, string messageText)
    {
        _ = ShowCore(
            owner,
            titleText,
            messageText,
            secondaryText: null,
            primaryText: Strings.Get("CommonOk"));
    }

    public static bool ShowChoice(
        Window owner,
        string titleText,
        string messageText,
        string secondaryText,
        string primaryText)
    {
        return ShowCore(
            owner,
            titleText,
            messageText,
            secondaryText,
            primaryText);
    }

    private static bool ShowCore(
        Window owner,
        string titleText,
        string messageText,
        string? secondaryText,
        string primaryText)
    {
        var accepted = false;
        var dialog = new Window
        {
            Owner = owner,
            Title = titleText,
            Width = 360,
            MinHeight = 164,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = owner.Topmost,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        AppTypography.ApplyTextRendering(dialog);

        var root = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = titleText,
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(16),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var message = new TextBlock
        {
            Text = messageText,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(13),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            MaxWidth = 324,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var secondary = CreateButton(secondaryText, primary: false);
            secondary.IsCancel = true;
            secondary.Click += (_, _) => dialog.Close();
            buttonRow.Children.Add(secondary);
        }

        var primary = CreateButton(
            primaryText,
            primary: !string.IsNullOrWhiteSpace(secondaryText));
        primary.IsDefault = true;
        if (buttonRow.Children.Count > 0)
        {
            primary.Margin = new Thickness(8, 0, 0, 0);
        }
        primary.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        buttonRow.Children.Add(primary);

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            e.Handled = true;
            dialog.Close();
        };

        Grid.SetRow(title, 0);
        Grid.SetRow(message, 1);
        Grid.SetRow(buttonRow, 2);
        layout.Children.Add(title);
        layout.Children.Add(message);
        layout.Children.Add(buttonRow);

        root.Child = layout;
        dialog.Content = root;
        dialog.ShowDialog();
        return accepted;
    }

    private static Button CreateButton(string text, bool primary)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            primary ? Theme.Tint(44) : Theme.Tint(28)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextBrush));
        style.Setters.Add(new Setter(Control.FontSizeProperty, AppTypography.Scale(13)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Tint(46)));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));

        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }
}
