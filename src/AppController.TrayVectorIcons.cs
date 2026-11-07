using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private FrameworkElement CreateTrayAddVectorIcon(string paperType)
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var icon = new ContentControl
        {
            Template = new ControlTemplate(typeof(ContentControl)) { VisualTree = presenter },
            Foreground = TrayTextBrush,
            Focusable = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        icon.Content = paperType == PaperTypes.Note
            ? PaperWindow.CreateTopBarNewNoteIcon(icon)
            : PaperWindow.CreateTopBarNewTodoIcon(icon);
        return icon;
    }

    private FrameworkElement CreateTrayPaperTypeVectorIcon(PaperData paper)
    {
        return new VectorGlyphElement(
            PaperTypeIcon(paper),
            new FontFamily("Segoe UI Symbol"),
            PaperTypeIconFontSize(paper),
            FontWeights.SemiBold)
        {
            Foreground = TrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private FrameworkElement CreateTraySettingsVectorIcon()
    {
        return new VectorPrimitiveIconElement(
            VectorPrimitiveIconKind.Settings)
        {
            Width = 15,
            Height = 15,
            Foreground = TrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
