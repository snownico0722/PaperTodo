using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PaperTodo;

/// <summary>
/// PaperTodo-owned ComboBox chrome for settings. The popup, selection rows and hover state
/// follow the active app theme instead of falling back to the system ComboBox theme.
/// </summary>
internal static class SettingsSelectControl
{

    /// <summary>
    /// Reuse the same host-owned dropdown chrome for PaperTodo's own settings surfaces.
    /// </summary>
    public static void ApplyAppTheme(
        ComboBox comboBox,
        double fontSize)
    {
        ApplyCore(
            comboBox,
            Theme.PaperBrush,
            Theme.TextBrush,
            Theme.WeakTextBrush,
            Theme.PaperBorderBrush,
            Theme.ActiveBrush,
            Theme.Tint((byte)(Theme.IsDark ? 24 : 14)),
            Theme.HoverBrush,
            Theme.Tint((byte)(Theme.IsDark ? 62 : 40)),
            AppTypography.UiFontFamily,
            fontSize);
    }

    private static void ApplyCore(
        ComboBox comboBox,
        Brush paper,
        Brush text,
        Brush weak,
        Brush border,
        Brush accent,
        Brush surface,
        Brush hover,
        Brush selected,
        FontFamily fontFamily,
        double fontSize)
    {
        ArgumentNullException.ThrowIfNull(comboBox);
        comboBox.Dispatcher.VerifyAccess();
        comboBox.IsDropDownOpen = false;
        comboBox.Foreground = text;
        comboBox.Background = surface;
        comboBox.BorderBrush = border;
        comboBox.BorderThickness = new Thickness(1);
        comboBox.Padding = new Thickness(9, 2, 8, 2);
        comboBox.FontFamily = fontFamily;
        comboBox.FontSize = fontSize;
        comboBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        comboBox.MaxDropDownHeight = Math.Max(120, comboBox.MaxDropDownHeight);
        comboBox.Style = BuildComboBoxStyle(paper, hover, weak, border, accent);
        comboBox.ItemContainerStyle = BuildItemStyle(hover, selected, text, accent);
    }

    private static Style BuildComboBoxStyle(
        Brush paper,
        Brush hover,
        Brush weak,
        Brush border,
        Brush accent)
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 28.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            BuildComboBoxTemplate(paper, hover, weak, border, accent)));
        return style;
    }

    private static ControlTemplate BuildComboBoxTemplate(
        Brush paper,
        Brush hover,
        Brush weak,
        Brush border,
        Brush accent)
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(DockPanel));
        content.SetValue(DockPanel.LastChildFillProperty, true);

        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.Name = "Arrow";
        arrow.SetValue(Shape.StrokeProperty, weak);
        arrow.SetValue(Shape.StrokeThicknessProperty, 1.35);
        arrow.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0 1 L 4 5 L 8 1"));
        arrow.SetValue(FrameworkElement.WidthProperty, 8.0);
        arrow.SetValue(FrameworkElement.HeightProperty, 6.0);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 1, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
        arrow.SetValue(DockPanel.DockProperty, Dock.Right);
        content.AppendChild(arrow);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateSelectorProperty,
            new TemplateBindingExtension(ItemsControl.ItemTemplateSelectorProperty));
        presenter.SetValue(
            ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        presenter.SetValue(UIElement.IsHitTestVisibleProperty, false);
        content.AppendChild(presenter);

        chrome.AppendChild(content);
        root.AppendChild(chrome);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(Control.TemplateProperty, TransparentToggleTemplate());
        toggle.SetBinding(
            ToggleButton.IsCheckedProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
        root.AppendChild(toggle);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(
            Popup.PlacementProperty,
            PlacementMode.Bottom);
        popup.SetBinding(
            Popup.IsOpenProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
        popup.SetBinding(
            Popup.PlacementTargetProperty,
            new Binding
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, paper);
        popupBorder.SetValue(Border.BorderBrushProperty, border);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(3));
        popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        popupBorder.SetBinding(
            FrameworkElement.WidthProperty,
            new Binding(nameof(FrameworkElement.ActualWidth))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
        popupBorder.SetValue(
            UIElement.EffectProperty,
            new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 1,
                Opacity = 0.14
            });

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        scroll.SetBinding(
            FrameworkElement.MaxHeightProperty,
            new Binding(nameof(ComboBox.MaxDropDownHeight))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        items.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        scroll.AppendChild(items);
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(ComboBox)) { VisualTree = root };

        var hoverTrigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
        template.Triggers.Add(hoverTrigger);

        var focusTrigger = new Trigger
        {
            Property = UIElement.IsKeyboardFocusWithinProperty,
            Value = true
        };
        focusTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "Chrome"));
        template.Triggers.Add(focusTrigger);

        var openTrigger = new Trigger
        {
            Property = ComboBox.IsDropDownOpenProperty,
            Value = true
        };
        openTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
        openTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "Chrome"));
        openTrigger.Setters.Add(new Setter(
            UIElement.RenderTransformProperty,
            new RotateTransform(180),
            "Arrow"));
        template.Triggers.Add(openTrigger);

        var disabledTrigger = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabledTrigger);
        return template;
    }

    private static ControlTemplate TransparentToggleTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
    }

    private static Style BuildItemStyle(
        Brush hover,
        Brush selected,
        Brush text,
        Brush accent)
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(Control.ForegroundProperty, text));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 28.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, BuildItemTemplate(hover, selected, accent)));
        return style;
    }

    private static ControlTemplate BuildItemTemplate(
        Brush hover,
        Brush selected,
        Brush accent)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };
        var highlighted = new Trigger
        {
            Property = ComboBoxItem.IsHighlightedProperty,
            Value = true
        };
        highlighted.Setters.Add(new Setter(Border.BackgroundProperty, hover, "ItemChrome"));
        template.Triggers.Add(highlighted);

        var isSelected = new Trigger
        {
            Property = ComboBoxItem.IsSelectedProperty,
            Value = true
        };
        isSelected.Setters.Add(new Setter(Border.BackgroundProperty, selected, "ItemChrome"));
        isSelected.Setters.Add(new Setter(Control.ForegroundProperty, accent));
        isSelected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        template.Triggers.Add(isSelected);

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        return template;
    }


}
