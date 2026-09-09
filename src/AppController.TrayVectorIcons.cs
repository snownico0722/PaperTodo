using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private FrameworkElement CreateTrayAddVectorIcon(VectorPrimitiveIconKind kind)
    {
        var icon = new Grid
        {
            Width = 17,
            Height = 17,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var paperGlyph = new VectorPrimitiveIconElement(kind)
        {
            IconSize = AppTypography.Scale(12),
            Foreground = TrayTextBrush,
            Margin = new Thickness(0, 0, 4.5, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Children.Add(paperGlyph);

        var plus = new VectorPrimitiveIconElement(
            VectorPrimitiveIconKind.Plus)
        {
            Width = 8,
            Height = 8,
            Foreground = TrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        icon.Children.Add(plus);
        return icon;
    }

    private FrameworkElement CreateTrayPaperTypeVectorIcon(PaperData paper)
    {
        return new VectorPrimitiveIconElement(PaperTypeIconKind(paper))
        {
            IconSize = PaperTypeIconFontSize(paper),
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
