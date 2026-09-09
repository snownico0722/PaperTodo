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
    private readonly RectangleGeometry _header = new();
    private readonly RectangleGeometry _body = new();
    private int _animationGeneration;

    internal InactiveTitleBarMask()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(_headerOpacity, null, _header));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, _body));
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
    internal double HeaderBottom => _header.Rect.IsEmpty ? 0 : _header.Rect.Bottom;

    internal void UpdateBounds(Size size, double headerBottom)
    {
        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(headerBottom))
        {
            return;
        }
        headerBottom = Math.Clamp(headerBottom, 0, size.Height);
        var header = new Rect(0, 0, size.Width, headerBottom);
        var body = new Rect(0, headerBottom, size.Width, size.Height - headerBottom);
        if (_header.Rect == header && _body.Rect == body)
        {
            return;
        }
        _header.Rect = header;
        _body.Rect = body;
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
