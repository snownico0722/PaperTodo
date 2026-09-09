using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PaperTodo;

internal static class TodoReminderDialogControls
{
    public static Button Button(
        string text,
        bool primary = false,
        bool compact = false)
    {
        return new Button
        {
            Content = text,
            Style = CreateButtonStyle(
                typeof(Button),
                primary,
                compact),
            MinWidth = compact ? 28 : 72,
            Height = compact ? 28 : double.NaN,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(compact ? 14 : 13)
        };
    }

    public static Button IconButton(VectorPrimitiveIconKind kind)
    {
        var button = Button(string.Empty, compact: true);
        button.Content = new VectorPrimitiveIconElement(kind) { IconSize = AppTypography.Scale(14) };
        return button;
    }

    public static RepeatButton RepeatButton(VectorPrimitiveIconKind kind)
    {
        return new RepeatButton
        {
            Content = new VectorPrimitiveIconElement(kind) { IconSize = AppTypography.Scale(14) },
            Style = CreateButtonStyle(
                typeof(RepeatButton),
                primary: false,
                compact: true),
            MinWidth = 28,
            Height = 28,
            Delay = 360,
            Interval = 70,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(14)
        };
    }

    private static Style CreateButtonStyle(
        Type targetType,
        bool primary,
        bool compact)
    {
        var style = new Style(targetType);
        style.Setters.Add(new Setter(
            Control.PaddingProperty,
            compact
                ? new Thickness(0)
                : new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(
            Control.BorderThicknessProperty,
            new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            primary ? Theme.ActiveBrush : Theme.Tint(28)));
        style.Setters.Add(new Setter(
            Control.ForegroundProperty,
            primary ? Theme.PaperBrush : Theme.TextBrush));
        style.Setters.Add(new Setter(
            Control.CursorProperty,
            Cursors.Hand));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(compact ? 7 : 8));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(
                Control.BackgroundProperty));
        border.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(
                Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(
            typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(
                ContentControl.ContentProperty));
        presenter.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        presenter.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(targetType)
        {
            VisualTree = border
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(
            Control.BackgroundProperty,
            primary
                ? Theme.CheckBoxActiveHoverBrush
                : Theme.Tint(46)));
        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(
            UIElement.OpacityProperty,
            0.82));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            template));
        return style;
    }
}
