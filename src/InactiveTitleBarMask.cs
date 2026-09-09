using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

// A render-only mask: the title row, body layout and native window bounds never change.
// Alpha zero in the final layered-window bitmap is what enables cross-process click-through.
internal sealed class InactiveTitleBarMask
{
    private readonly SolidColorBrush _headerOpacity = new(Colors.Black);
    private readonly RectangleGeometry _fadeRegion = new();
    private readonly RectangleGeometry _bodyTop = new();
    private readonly RectangleGeometry _bodyLower = new();
    private int _animationGeneration;
    private double _headerBottom;

    internal InactiveTitleBarMask()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(_headerOpacity, null, _fadeRegion));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, _bodyTop));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, _bodyLower));
        MaskBrush = new DrawingBrush(drawing)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            TileMode = TileMode.None
        };
    }

    internal DrawingBrush MaskBrush { get; }
    internal double HeaderOpacity => _headerOpacity.Opacity;
    internal double HeaderBottom => _headerBottom;

    internal void UpdateBounds(
        Size size,
        double headerBottom,
        Rect chromeBounds,
        double topCornerRadius)
    {
        if (size.Width <= 0 || size.Height <= 0 ||
            !double.IsFinite(headerBottom) ||
            !double.IsFinite(chromeBounds.Left) ||
            !double.IsFinite(chromeBounds.Right) ||
            !double.IsFinite(topCornerRadius))
        {
            return;
        }

        headerBottom = Math.Clamp(headerBottom, 0, size.Height);
        var chromeLeft = Math.Clamp(chromeBounds.Left, 0, size.Width);
        var chromeRight = Math.Clamp(chromeBounds.Right, chromeLeft, size.Width);
        var bodyHeight = size.Height - headerBottom;
        var maxRadius = Math.Max(0, Math.Min((chromeRight - chromeLeft) / 2, bodyHeight / 2));
        var radius = Math.Clamp(topCornerRadius, 0, maxRadius);
        var roundedBandBottom = Math.Min(size.Height, headerBottom + radius);

        // The fading region extends one radius below the title boundary. The opaque rounded
        // body overlaps its center, so title restore still reconstructs a full rectangular
        // mask while title hide smoothly exposes a new rounded paper top instead of a hard cut.
        var fadeRegion = new Rect(0, 0, size.Width, roundedBandBottom);
        var bodyTop = radius > 0
            ? new Rect(chromeLeft, headerBottom, chromeRight - chromeLeft, radius * 2)
            : Rect.Empty;
        var bodyLower = new Rect(
            0,
            roundedBandBottom,
            size.Width,
            size.Height - roundedBandBottom);

        if (_fadeRegion.Rect == fadeRegion &&
            _bodyTop.Rect == bodyTop &&
            _bodyLower.Rect == bodyLower &&
            _bodyTop.RadiusX == radius &&
            _bodyTop.RadiusY == radius &&
            _headerBottom == headerBottom)
        {
            return;
        }

        _headerBottom = headerBottom;
        _fadeRegion.Rect = fadeRegion;
        _bodyTop.Rect = bodyTop;
        _bodyTop.RadiusX = radius;
        _bodyTop.RadiusY = radius;
        _bodyLower.Rect = bodyLower;
        MaskBrush.Viewbox = MaskBrush.Viewport = new Rect(size);
    }

    internal void SetOpacity(double target, double milliseconds, Action? completed = null)
    {
        // Repeated hover/layout refreshes must not restart an in-flight focus transition.
        if (milliseconds > 0 && (double)_headerOpacity.GetAnimationBaseValue(Brush.OpacityProperty) == target)
        {
            return;
        }
        var from = _headerOpacity.Opacity;
        var generation = ++_animationGeneration;
        _headerOpacity.BeginAnimation(Brush.OpacityProperty, null);
        _headerOpacity.Opacity = target;
        if (milliseconds <= 0 || from == target)
        {
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = AnimationHelper.QuickEase,
            FillBehavior = FillBehavior.Stop
        };
        if (completed != null)
        {
            animation.Completed += (_, _) =>
            {
                if (generation == _animationGeneration) completed();
            };
        }
        _headerOpacity.BeginAnimation(Brush.OpacityProperty, animation);
    }
}
