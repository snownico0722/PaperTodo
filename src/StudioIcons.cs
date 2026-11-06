using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo;

internal enum StudioIcon
{
    Studio, ReturnToPaper, Sidebar, Minimize, Maximize, Restore, Close,
    NewNote, NewTodo, Open, Save, Settings, Pin, More, Association, ExternalOpen, Workspace,
    Undo, Redo, Heading, Bold, Italic, Strike, Bullets, Numbers, Quote, Code, Link, Image, ExportImage
}

// One 24-unit, rounded 1.8-unit stroke family for both Studio hosts. Paths do not
// depend on installed symbol fonts; the owner supplies theme and active colors.
internal static class StudioIcons
{
    internal static FrameworkElement Create(Control owner, StudioIcon icon, double size = 16)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(GeometryFor(icon)),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        canvas.Children.Add(path);
        return new Viewbox
        {
            Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
    }

    internal static Button Button(StudioIcon icon, string hint)
    {
        var button = new Button
        {
            Width = 30, Height = 28, Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = false,
            ToolTip = hint
        };
        button.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey")));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrushKey")));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .65));
        template.Triggers.Add(pressed);
        button.Template = template;
        button.Content = Create(button, icon, 16 * Math.Clamp(AppTypography.ScaleFactor, .85, 1.15));
        AutomationProperties.SetName(button, hint);
        return button;
    }

    private static string GeometryFor(StudioIcon icon) => icon switch
    {
        StudioIcon.Studio => "M5 4 H19 A2 2 0 0 1 21 6 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V6 A2 2 0 0 1 5 4 Z M3 8 H21 M8 8 V20 M12 12 H17 M12 16 H16",
        StudioIcon.ReturnToPaper => "M10 3 H19 A2 2 0 0 1 21 5 V15 M8 8 H15 A1 1 0 0 1 16 9 V20 H3 V8 H8 M3 12 H16 M9 17 H19 M16 14 L19 17 L16 20",
        StudioIcon.Sidebar => "M5 4 H19 A2 2 0 0 1 21 6 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V6 A2 2 0 0 1 5 4 Z M9 4 V20 M5.5 9 H6.5 M5.5 12 H6.5 M5.5 15 H6.5",
        StudioIcon.Minimize => "M5 16 H19",
        StudioIcon.Maximize => "M6 4 H18 A2 2 0 0 1 20 6 V18 A2 2 0 0 1 18 20 H6 A2 2 0 0 1 4 18 V6 A2 2 0 0 1 6 4 Z",
        StudioIcon.Restore => "M8 7 V4 H20 V16 H17 M4 8 H16 V20 H4 Z",
        StudioIcon.Close => "M6 6 L18 18 M18 6 L6 18",
        StudioIcon.NewNote => "M13 4 H5 A1 1 0 0 0 4 5 V19 A1 1 0 0 0 5 20 H18 A1 1 0 0 0 19 19 V12 M10 14 L11 10 L18 3 L21 6 L14 13 Z M16 5 L19 8",
        StudioIcon.NewTodo => "M10 5 H20 M10 12 H20 M10 19 H20 M3 5 L4.5 6.5 L7 3.5 M3 12 L4.5 13.5 L7 10.5 M3 19 L4.5 20.5 L7 17.5",
        StudioIcon.Open => "M3 8 V5 H9 L12 8 H21 M3 8 H21 L18 20 H3 Z",
        StudioIcon.Save => "M5 3 H17 L21 7 V20 H3 V3 H5 M7 3 V9 H16 V3 M7 20 V14 H17 V20",
        StudioIcon.Settings => "M9 3 H15 L16 6 L19 7 L21 12 L19 17 L16 18 L15 21 H9 L8 18 L5 17 L3 12 L5 7 L8 6 Z M12 8 A4 4 0 1 1 11.99 8 Z",
        StudioIcon.Pin => "M8 3 H16 M9 3 V9 L6 14 H18 L15 9 V3 M12 14 V21",
        StudioIcon.More => "M5 12 H5.1 M12 12 H12.1 M19 12 H19.1",
        StudioIcon.Association => "M9 8 L6 5 L3 8 L6 11 Z M18 13 L21 16 L18 19 L15 16 Z M9 8 H14 A4 4 0 0 1 18 12 V13 M6 11 V15 A3 3 0 0 0 9 18 H13",
        StudioIcon.ExternalOpen => "M13 3 H21 V11 M21 3 L11 13 M10 5 H5 A2 2 0 0 0 3 7 V19 A2 2 0 0 0 5 21 H17 A2 2 0 0 0 19 19 V14",
        StudioIcon.Workspace => "M3 4 H10 V11 H3 Z M14 4 H21 V11 H14 Z M3 15 H10 V22 H3 Z M14 15 H21 V22 H14 Z",
        StudioIcon.Undo => "M8 5 L3 10 L8 15 M3 10 H14 A6 6 0 0 1 14 22",
        StudioIcon.Redo => "M16 5 L21 10 L16 15 M21 10 H10 A6 6 0 0 0 10 22",
        StudioIcon.Heading => "M5 5 V19 M18 5 V19 M5 12 H18",
        StudioIcon.Bold => "M6 4 H12 C19 4 19 12 12 12 H6 V4 M6 12 H13 C21 12 21 20 13 20 H6 Z",
        StudioIcon.Italic => "M10 4 H19 M5 20 H14 M15 4 L9 20",
        StudioIcon.Strike => "M18 6 C16 2 7 3 6 7 C5 10 8 11 12 12 M3 12 H21 M17 14 C22 21 7 23 5 18",
        StudioIcon.Bullets => "M4 6 H4.1 M4 12 H4.1 M4 18 H4.1 M9 6 H21 M9 12 H21 M9 18 H21",
        StudioIcon.Numbers => "M3 4 L5 3 V9 M3 9 H7 M3 14 C3 11 7 11 7 14 L3 19 H7 M11 6 H21 M11 16 H21",
        StudioIcon.Quote => "M4 7 H10 V13 H6 C6 16 5 18 3 19 M14 7 H20 V13 H16 C16 16 15 18 13 19",
        StudioIcon.Code => "M7 6 L2 12 L7 18 M17 6 L22 12 L17 18 M14 3 L10 21",
        StudioIcon.Link => "M9 15 L15 9 M7 14 L5 16 A3.5 3.5 0 0 0 10 21 L13 18 A3.5 3.5 0 0 0 13 13 M11 11 A3.5 3.5 0 0 1 11 6 L14 3 A3.5 3.5 0 0 1 19 8 L17 10",
        StudioIcon.Image => "M5 4 H19 A2 2 0 0 1 21 6 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V6 A2 2 0 0 1 5 4 Z M3 17 L9 11 L14 16 L17 13 L21 17 M16 8 H16.1",
        StudioIcon.ExportImage => "M13 3 H5 A2 2 0 0 0 3 5 V19 A2 2 0 0 0 5 21 H18 A2 2 0 0 0 20 19 V12 M3 17 L8 12 L13 17 M16 4 L19 1 L22 4 M19 1 V10",
        _ => throw new ArgumentOutOfRangeException(nameof(icon))
    };
}
