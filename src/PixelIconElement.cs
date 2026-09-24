using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

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
