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
    private const string RunScriptIconPath =
        "M6 4.5 H18 Q21 4.5 21 7.5 V16.5 Q21 19.5 18 19.5 H6 Q3 19.5 3 16.5 V7.5 Q3 4.5 6 4.5 Z " +
        "M7 9 L10.25 12 L7 15 M13 15 H17";

    private const string NewPaperPlusPath = "M2 12 H8 M5 9 V15 ";

    // Keep the drawing's margins when centering it. Stretching to its ink bounds
    // would give a horizontal stroke and a full terminal window the same height.
    private static Path TopBarPath(Control owner, string data, double width = 24,
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

    internal static FrameworkElement CreateTopBarRunScriptIcon(Control owner)
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

    internal static FrameworkElement CreateTopBarAssociationIcon(Control owner, bool active)
    {
        if (Theme.IsPixelSkin) return active
            ? CreatePixelIcon(owner,
                "....###..", "...#...#.", "..#.....#", "..#..###.", ".###.#...",
                "#...#.###", "#...##..#", ".###..#.#", ".....###.")
            : CreatePixelIcon(owner,
                "....###..", "...#...#.", "..#.....#", "..#..###.", ".###.#...",
                "#...#....", "#...#....", ".###.....", ".........");
        if (!active)
            return TopBarPath(owner,
                "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07L11.75 5.18 " +
                "M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71", stroke: 2);

        var icon = new Grid { IsHitTestVisible = false };
        icon.Children.Add(TopBarPath(owner,
            "M10.6 5.15l1.76-1.76a4.55 4.55 0 0 1 7.63 4.35 " +
            "M8.6 18.95a4.32 4.32 0 0 1-5.5-6.55l3-3a4.3 4.3 0 0 1 5.95-.14", stroke: 2));
        // The check is cut out so the actual surface shows through on every skin.
        icon.Children.Add(TopBarPath(owner,
            "F0 M17 10a7 7 0 1 1 0 14 7 7 0 1 1 0-14Z " +
            "M12.5 17.35l1.44-1.48 2.28 2.13 4.08-4.36 1.51 1.41-5.49 5.86Z", stroke: 0, filled: true));
        return icon;
    }

    internal static FrameworkElement CreateTopBarNewTodoIcon(Control owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            ".........", "#######..", "#.....#.#", "#....#..#", "#.#.#...#", "#..#....#", "#.......#", "#########", ".........");
        return TopBarPath(owner, NewPaperPlusPath + "M12 12 L16.5 16.5 L26 7.5", width: 28);
    }

    internal static FrameworkElement CreateTopBarNewNoteIcon(Control owner)
    {
        if (Theme.IsPixelSkin) return CreatePixelIcon(owner,
            "......##.", ".....####", "....####.", "...####..", "..####...", ".####....", ".###.....", ".#.......", ".........");
        return TopBarPath(owner, NewPaperPlusPath +
            "M12 19 L13 14.5 L22.5 5 Q23.5 4 24.5 5 L26 6.5 Q27 7.5 26 8.5 L16.5 18 Z M21 6.5 L24.5 10",
            width: 28);
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
            var minus = new CollapseMinusIconElement
            {
                Width = Math.Round(16 * scale, 1),
                Height = Math.Round(10 * scale, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            minus.SetBinding(
                CollapseMinusIconElement.ForegroundProperty,
                CreateForegroundBinding(owner));
            return minus;
        }

        return TopBarPath(owner, "M5.5 5.5 L18.5 18.5 M18.5 5.5 L5.5 18.5");
    }

    private static FrameworkElement CreatePixelIcon(Control owner, params string[] rows)
    {
        var icon = new PixelIconElement(rows)
        {
            Width = 18 * TopBarIconScale(), Height = 18 * TopBarIconScale(),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(PixelIconElement.ForegroundProperty, CreateForegroundBinding(owner));
        return icon;
    }

    private static double TopBarIconScale()
    {
        return Math.Clamp(
            AppTypography.ScaleFactor,
            0.85,
            1.15);
    }
}

// Physical-pixel cells, not a stretched Path with 1.5-DIP squares. Only the icon is
// aliased; the title/body keep their ordinary Chinese font and text rendering.
internal sealed class PixelIconElement : FrameworkElement
{
    internal static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PixelIconElement),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private readonly string[] _rows;
    internal PixelIconElement(params string[] rows)
    {
        if (rows.Length == 0 || rows[0].Length == 0) throw new ArgumentException("Empty pixel icon.", nameof(rows));
        foreach (var row in rows)
            if (row.Length != rows[0].Length) throw new ArgumentException("Ragged pixel icon.", nameof(rows));
        _rows = (string[])rows.Clone();
        IsHitTestVisible = false;
        SnapsToDevicePixels = UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cell = Math.Floor(Math.Min(ActualWidth * dpi.DpiScaleX / _rows[0].Length,
            ActualHeight * dpi.DpiScaleY / _rows.Length));
        if (cell < 1) return;
        var origin = PresentationSource.FromVisual(this) != null ? PointToScreen(new Point()) : new Point();
        var x0 = (Math.Round(origin.X + (ActualWidth * dpi.DpiScaleX - _rows[0].Length * cell) / 2) - origin.X) / dpi.DpiScaleX;
        var y0 = (Math.Round(origin.Y + (ActualHeight * dpi.DpiScaleY - _rows.Length * cell) / 2) - origin.Y) / dpi.DpiScaleY;
        for (var y = 0; y < _rows.Length; y++)
        for (var x = 0; x < _rows[y].Length; x++)
            if (_rows[y][x] == '#') dc.DrawRectangle(Foreground, null,
                new Rect(x0 + x * cell / dpi.DpiScaleX, y0 + y * cell / dpi.DpiScaleY,
                    cell / dpi.DpiScaleX, cell / dpi.DpiScaleY));
    }
}
