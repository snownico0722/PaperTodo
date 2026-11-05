using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static FrameworkElement CreateTopBarAssociationIcon(
        Button owner,
        bool active)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            "....#....", "..#####..", ".##.#.##.", ".#..#..#.", "#########", ".#..#..#.", ".##.#.##.", "..#####..", "....#....");
        var scale = TopBarIconScale();
        var icon = new VectorPrimitiveIconElement(
            active
                ? VectorPrimitiveIconKind.AssociationActive
                : VectorPrimitiveIconKind.AssociationIdle)
        {
            Width = Math.Round(14 * scale, 1),
            Height = Math.Round(14 * scale, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        return icon;
    }

    private static FrameworkElement CreateTopBarNewTodoIcon(Button owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            ".........", "#######..", "#.....#.#", "#....#..#", "#.#.#...#", "#..#....#", "#.......#", "#########", ".........");
        return CreateTopBarNewPaperIcon(
            owner,
            "✓",
            width: 21);
    }

    private static FrameworkElement CreateTopBarNewNoteIcon(Button owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            "......##.", ".....####", "....####.", "...####..", "..####...", ".####....", ".###.....", ".#.......", ".........");
        return CreateTopBarNewPaperIcon(
            owner,
            "✎",
            width: 22);
    }

    private static FrameworkElement CreateTopBarNewPaperIcon(
        Button owner,
        string glyph,
        double width)
    {
        var scale = TopBarIconScale();
        var icon = new Grid
        {
            Width = Math.Round(width * scale, 1),
            Height = Math.Round(16 * scale, 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };

        var plus = new VectorPrimitiveIconElement(
            VectorPrimitiveIconKind.Plus)
        {
            Width = Math.Round(9 * scale, 1),
            Height = Math.Round(9 * scale, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        plus.SetBinding(
            VectorPrimitiveIconElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        icon.Children.Add(plus);

        var mark = new VectorGlyphElement(
            glyph,
            AppTypography.SymbolFontFamily,
            AppTypography.Scale(13),
            FontWeights.Normal)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        mark.SetBinding(
            VectorGlyphElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        icon.Children.Add(mark);
        return icon;
    }

    private static FrameworkElement CreateTopBarCloseIcon(
        Button owner,
        bool collapse)
    {
        if (Theme.IsPixelSkin) return collapse
            ? CreatePixelIcon(owner, "########")
            : CreatePixelIcon(owner, "##....##", ".##..##.", "..####..", "...##...", "..####..", ".##..##.", "##....##");
        if (collapse)
        {
            var scale = TopBarIconScale();
            var minus = new VectorPrimitiveIconElement(
                VectorPrimitiveIconKind.Minus)
            {
                Width = Math.Round(16 * scale, 1),
                Height = Math.Round(10 * scale, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            minus.SetBinding(
                VectorPrimitiveIconElement.ForegroundProperty,
                CreateForegroundBinding(owner));
            return minus;
        }

        var close = new VectorGlyphElement(
            "×",
            AppTypography.UiFontFamily,
            AppTypography.Scale(16),
            FontWeights.Normal)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.SetBinding(
            VectorGlyphElement.ForegroundProperty,
            CreateForegroundBinding(owner));
        return close;
    }

    private static double TopBarIconScale()
    {
        return Math.Clamp(
            AppTypography.ScaleFactor,
            0.85,
            1.15);
    }
}
