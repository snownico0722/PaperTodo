using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

// Eight reusable lighting tiles for the short paper form morph. Corners follow the
// current rim band; straight sides stretch along their length only. The full exact
// relief is restored at the endpoint. No capture, worker, timer or persistent pool.
internal sealed class MaterialTransitionRelief
{
    private readonly DrawingBrush[] _tiles;

    internal MaterialTransitionRelief(Drawing drawing, Size size, CornerRadius corners)
    {
        var band = Band(size, corners);
        var rects = Tiles(size, band);
        _tiles = new DrawingBrush[rects.Length];
        for (var i = 0; i < rects.Length; i++)
        {
            _tiles[i] = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = rects[i],
                Stretch = Stretch.Fill
            };
            _tiles[i].Freeze();
        }
    }

    internal void Draw(DrawingContext dc, Size size, CornerRadius corners)
    {
        var band = Band(size, corners);
        var w = size.Width; var h = size.Height;
        Draw(0, 0, 0, band, band);
        Draw(1, band, 0, w - 2 * band, band);
        Draw(2, w - band, 0, band, band);
        Draw(3, 0, band, band, h - 2 * band);
        Draw(4, w - band, band, band, h - 2 * band);
        Draw(5, 0, h - band, band, band);
        Draw(6, band, h - band, w - 2 * band, band);
        Draw(7, w - band, h - band, band, band);
        void Draw(int index, double x, double y, double width, double height)
        {
            if (width > 0 && height > 0)
                dc.DrawRectangle(_tiles[index], null, new Rect(x, y, width, height));
        }
    }

    private static double Band(Size size, CornerRadius corners) => Math.Min(
        Math.Max(4, Math.Max(Math.Max(corners.TopLeft, corners.TopRight),
            Math.Max(corners.BottomLeft, corners.BottomRight))) + 1,
        Math.Min(size.Width, size.Height) / 2);

    private static Rect[] Tiles(Size size, double b)
    {
        // A resting pill can have no straight vertical segment. Sample its rim
        // center, not an empty Viewbox, so expansion still has side illumination.
        var x = Math.Min(b, Math.Max(0, (size.Width - 1) / 2));
        var y = Math.Min(b, Math.Max(0, (size.Height - 1) / 2));
        var width = Math.Max(1, size.Width - 2 * b);
        var height = Math.Max(1, size.Height - 2 * b);
        return
        [
            new(0, 0, b, b), new(x, 0, width, b),
            new(size.Width - b, 0, b, b),
            new(0, y, b, height), new(size.Width - b, y, b, height),
            new(0, size.Height - b, b, b),
            new(x, size.Height - b, width, b),
            new(size.Width - b, size.Height - b, b, b)
        ];
    }
}
