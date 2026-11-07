using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    // Keep this WPF geometry aligned with assets/workbench-tray.svg.
    // WPF renders the vector directly without adding a runtime SVG decoder.
    private FrameworkElement CreateTrayWorkbenchIcon()
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3.1 6.4V4.8A1.7 1.7 0 0 1 4.8 3.1H6.4 M13.6 3.1H15.2A1.7 1.7 0 0 1 16.9 4.8V6.4 M16.9 13.6V15.2A1.7 1.7 0 0 1 15.2 16.9H13.6 M6.4 16.9H4.8A1.7 1.7 0 0 1 3.1 15.2V13.6 M5.7 6.5H10.2V11H5.7Z M10.8 9.4H15.3V13.9H10.8Z"),
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform,
            Stroke = TrayTextBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

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
