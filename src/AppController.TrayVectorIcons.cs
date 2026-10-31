using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    private FrameworkElement CreateTrayAddVectorIcon(string glyph)
    {
        var icon = new Grid
        {
            Width = 17,
            Height = 17,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var paperGlyph = new VectorGlyphElement(
            glyph,
            new FontFamily("Segoe UI Symbol"),
            AppTypography.Scale(12),
            FontWeights.SemiBold)
        {
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

}
