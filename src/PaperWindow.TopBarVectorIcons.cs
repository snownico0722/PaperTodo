using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Button = System.Windows.Controls.Button;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal const string RunScriptIconPath =
        "M5 4.5 H19 Q22 4.5 22 7.5 V16.5 Q22 19.5 19 19.5 H5 Q2 19.5 2 16.5 V7.5 Q2 4.5 5 4.5 Z " +
        "M6.5 9 L10 12 L6.5 15 M13 15 H17.5";

    private const string NewPaperPlusPath = "M2 12 H10 M6 8 V16 ";

    // Keep the drawing's margins when centering it. Stretching to its ink bounds
    // would give a horizontal stroke and a full terminal window the same height.
    private static Path TopBarPath(Button owner, string data, double width = 24,
        double stroke = 1.8, bool filled = false)
    {
        var scale = 16.0 / 24 * TopBarIconScale();
        var path = new Path
        {
            Width = width,
            Height = 24,
            Data = Geometry.Parse(data),
            Stretch = Stretch.None,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            LayoutTransform = new ScaleTransform(scale, scale),
            IsHitTestVisible = false
        };
        path.SetBinding(Shape.StrokeProperty, CreateForegroundBinding(owner));
        if (filled) path.SetBinding(Shape.FillProperty, CreateForegroundBinding(owner));
        return path;
    }

    private static FrameworkElement CreateTopmostPinIcon(Button owner, bool pinned)
    {
        if (Theme.IsPixelSkin) return pinned
            ? CreatePixelIcon(owner, "..####..", "...##...", "..####..", ".######.", "...##...", "...##...", "...#....")
            : CreatePixelIcon(owner, "..####..", "..#..#..", "..#..#..", ".######.", "...##...", "...##...", "...#....");
        return TopBarPath(owner,
            "M7.5 4.5 H16.5 L15.5 6 V12 L18 15 V16 H6 V15 L8.5 12 V6 Z M12 16 V21",
            filled: pinned);
    }

    private static FrameworkElement CreateTopBarRunScriptIcon(Button owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            ".#######.", "#.......#", "#.#.....#", "#..#....#", "#.#..##.#", "#.......#", ".#######.");
        return TopBarPath(owner, RunScriptIconPath);
    }

    private static FrameworkElement CreateTopBarMoreIcon(Button owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner, "#..#..#");
        return TopBarPath(owner, "M4.5 12 h0.25 M11.875 12 h0.25 M19.25 12 h0.25", stroke: 3);
    }

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
            Width = Math.Round(18 * scale, 1),
            Height = Math.Round(18 * scale, 1),
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
        return TopBarPath(owner, NewPaperPlusPath + "M15 12 L20 17 L30 7", width: 32);
    }

    private static FrameworkElement CreateTopBarNewNoteIcon(Button owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            "......##.", ".....####", "....####.", "...####..", "..####...", ".####....", ".###.....", ".#.......", ".........");
        return TopBarPath(owner, NewPaperPlusPath +
            "M15 19 L16 14.5 L25.5 5 Q26.5 4 27.5 5 L29 6.5 Q30 7.5 29 8.5 L19.5 18 Z M24 6.5 L27.5 10",
            width: 32);
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

        return TopBarPath(owner, "M5.5 5.5 L18.5 18.5 M18.5 5.5 L5.5 18.5");
    }

    private static double TopBarIconScale()
    {
        return Math.Clamp(
            AppTypography.ScaleFactor,
            0.85,
            1.15);
    }
}
