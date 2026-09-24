using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

// The outer Border still owns the shell's original layout. Its ordinary paper shadow is
// painted as a handful of cached soft rings instead of a full-surface DropShadowEffect.
// Only the empty background Border is arranged shorter; the editor never changes size.
internal class PaperChromeBorder : Border
{
    private readonly Border _surface = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private Geometry? _contentClip;
    private int _animationGeneration;
    private double _surfaceInset;
    private Pen[] _lightweightShadowPens = Array.Empty<Pen>();
    private double _lightweightShadowStep;
    private double _lightweightShadowDepthX;
    private double _lightweightShadowDepthY;

    internal static readonly DependencyProperty HeaderOpacityProperty = DependencyProperty.Register(
        nameof(HeaderOpacity), typeof(double), typeof(PaperChromeBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PaperChromeBorder)d).UpdateSurface(((PaperChromeBorder)d).RenderSize)));

    internal PaperChromeBorder() => AddVisualChild(_surface);

    internal double HeaderOpacity => (double)GetValue(HeaderOpacityProperty);
    internal double HeaderExtent { get; private set; }
    internal bool HasLightweightShadow => _lightweightShadowPens.Length > 0;

    protected override int VisualChildrenCount => base.VisualChildrenCount + 1;
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _surface : base.GetVisualChild(index - 1);

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var result = base.ArrangeOverride(arrangeSize);
        UpdateSurface(arrangeSize);
        return result;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawLightweightShadow(drawingContext);
        if (_surface.Visibility != Visibility.Visible)
            base.OnRender(drawingContext);
    }

    // The shadow lives in the existing transparent window gutter. Drawing several frozen,
    // non-overlapping rings is size-independent work: resize changes only their rectangles,
    // never a bitmap/effect surface. The paper itself paints afterwards and hides the inner
    // half of every ring, naturally producing the same outside-only silhouette.
    private void DrawLightweightShadow(DrawingContext drawingContext)
    {
        if (_lightweightShadowPens.Length == 0 ||
            RenderSize.Width <= 0 ||
            RenderSize.Height - _surfaceInset <= 0)
        {
            return;
        }

        var baseRect = new Rect(
            _lightweightShadowDepthX,
            _surfaceInset + _lightweightShadowDepthY,
            RenderSize.Width,
            RenderSize.Height - _surfaceInset);
        var radius = Math.Max(0, CornerRadius.TopLeft);

        for (var i = 0; i < _lightweightShadowPens.Length; i++)
        {
            var expand = (i + 0.5) * _lightweightShadowStep;
            var rect = baseRect;
            rect.Inflate(expand, expand);
            var ringRadius = radius + expand;
            drawingContext.DrawRoundedRectangle(
                null,
                _lightweightShadowPens[i],
                rect,
                ringRadius,
                ringRadius);
        }
    }

    internal void SetLightweightShadow(double blurRadius, double depth, double opacity)
    {
        blurRadius = Math.Clamp(blurRadius, 0, 32);
        depth = Math.Clamp(depth, 0, 16);
        opacity = Math.Clamp(opacity, 0, 1);
        if (blurRadius <= 0 || opacity <= 0)
        {
            ClearLightweightShadow();
            return;
        }

        // The existing paper HWND reserves an 8-DIP gutter. Keep every ring within it:
        // expanded paper uses almost the full gutter, while the tighter capsule shadow
        // intentionally occupies less.
        var extent = Math.Clamp(blurRadius * 0.55, 2.0, 7.75);
        var ringCount = Math.Clamp((int)Math.Ceiling(extent), 4, 8);
        var step = extent / ringCount;

        // WPF DropShadowEffect's default direction is visually down/right. A small diagonal
        // shift retains that weight without making the lightweight rings depend on window size.
        var diagonalDepth = depth / Math.Sqrt(2);
        var pens = new Pen[ringCount];
        for (var i = 0; i < ringCount; i++)
        {
            var t = ringCount == 1 ? 0 : (double)i / (ringCount - 1);
            // Dense at the contact edge, then quickly fade. Rings do not overlap, so their
            // individual alpha directly describes the local shadow strength.
            var falloff = Math.Pow(1 - t, 1.45);
            var alpha = (byte)Math.Clamp(
                Math.Round(255 * opacity * (0.12 + 0.36 * falloff)),
                0,
                255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            brush.Freeze();
            var pen = new Pen(brush, step);
            pen.Freeze();
            pens[i] = pen;
        }

        _lightweightShadowPens = pens;
        _lightweightShadowStep = step;
        _lightweightShadowDepthX = diagonalDepth;
        _lightweightShadowDepthY = diagonalDepth;
        InvalidateVisual();
    }

    internal void ClearLightweightShadow()
    {
        if (_lightweightShadowPens.Length == 0)
        {
            return;
        }

        _lightweightShadowPens = Array.Empty<Pen>();
        _lightweightShadowStep = 0;
        _lightweightShadowDepthX = 0;
        _lightweightShadowDepthY = 0;
        InvalidateVisual();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
    {
        if (_surface.Visibility != Visibility.Visible) return base.HitTestCore(parameters);
        // The helper is not an input element: keep border drag/menu gestures on this owner.
        var point = parameters.HitPoint - VisualTreeHelper.GetOffset(_surface);
        return VisualTreeHelper.HitTest(_surface, point) != null
            ? new PointHitTestResult(this, parameters.HitPoint) : null;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_surface == null) return;
        if (e.Property == BackgroundProperty || e.Property == BorderBrushProperty ||
            e.Property == BorderThicknessProperty || e.Property == CornerRadiusProperty ||
            e.Property == SnapsToDevicePixelsProperty || e.Property == UseLayoutRoundingProperty)
        {
            _surface.SetValue(e.Property, e.NewValue);
            if (e.Property == CornerRadiusProperty) UpdateSurface(RenderSize);
        }
    }

    internal void SetHeaderExtent(double extent)
    {
        if (!double.IsFinite(extent) || extent < 0 || HeaderExtent == extent) return;
        HeaderExtent = extent;
        UpdateSurface(RenderSize);
        InvalidateVisual();
    }

    private void UpdateSurface(Size size)
    {
        if (_surface == null) return;
        var inset = Math.Clamp(HeaderExtent * (1 - HeaderOpacity), 0, Math.Max(0, size.Height - 1));
        if (UseLayoutRounding)
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
            inset = Math.Round(inset * dpi) / dpi;
        }
        _surfaceInset = inset;
        if (inset <= 0 || size.Width <= 0 || size.Height <= 0)
        {
            _surface.Visibility = Visibility.Collapsed;
            if (Child != null && ReferenceEquals(Child.Clip, _contentClip)) Child.Clip = null;
            _contentClip = null;
            return;
        }

        _surface.Visibility = Visibility.Visible;
        var surfaceSize = new Size(size.Width, size.Height - inset);
        _surface.Measure(surfaceSize);
        _surface.Arrange(new Rect(new Point(0, inset), surfaceSize));

        // Clip content before the outer shadow is rendered. A mask outside the shadow
        // would cut off the new top edge again. This clip owns only the top corners.
        if (Child is not FrameworkElement child || child.RenderSize.Width <= 0) return;
        var width = child.RenderSize.Width;
        var bottom = child.RenderSize.Height;
        var top = Math.Min(inset, bottom);
        var maxRadius = Math.Min(width / 2, Math.Max(0, bottom - top));
        var left = Math.Clamp(CornerRadius.TopLeft - Math.Max(BorderThickness.Top, BorderThickness.Left) / 2, 0, maxRadius);
        var right = Math.Clamp(CornerRadius.TopRight - Math.Max(BorderThickness.Top, BorderThickness.Right) / 2, 0, maxRadius);
        var clip = new StreamGeometry();
        using (var context = clip.Open())
        {
            context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - right, top), isStroked: false, isSmoothJoin: false);
            context.ArcTo(new Point(width, top + right), new Size(right, right), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(width, bottom), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(0, bottom), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(0, top + left), isStroked: false, isSmoothJoin: false);
            context.ArcTo(new Point(left, top), new Size(left, left), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
        }
        clip.Freeze();
        child.Clip = _contentClip = clip;
    }

    internal void SetHeaderOpacity(double target, double milliseconds, Action? completed = null)
    {
        if (milliseconds > 0 && (double)GetAnimationBaseValue(HeaderOpacityProperty) == target) return;
        var from = HeaderOpacity;
        var generation = ++_animationGeneration;
        BeginAnimation(HeaderOpacityProperty, null);
        SetValue(HeaderOpacityProperty, target);
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
            animation.Completed += (_, _) =>
            {
                if (generation == _animationGeneration) completed();
            };
        BeginAnimation(HeaderOpacityProperty, animation);
    }
}
