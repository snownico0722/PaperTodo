using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

// The outer Border still owns the shell's original layout and shadow. Only the
// empty background Border is arranged shorter; the editor never changes size.
internal sealed class PaperChromeBorder : Border
{
    private readonly Border _surface = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private Geometry? _contentClip;
    private int _animationGeneration;

    internal static readonly DependencyProperty HeaderOpacityProperty = DependencyProperty.Register(
        nameof(HeaderOpacity), typeof(double), typeof(PaperChromeBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PaperChromeBorder)d).UpdateSurface(((PaperChromeBorder)d).RenderSize)));

    internal PaperChromeBorder() => AddVisualChild(_surface);

    internal double HeaderOpacity => (double)GetValue(HeaderOpacityProperty);
    internal double HeaderExtent { get; private set; }

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
        if (_surface.Visibility != Visibility.Visible)
            base.OnRender(drawingContext);
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
        var left = Math.Clamp(CornerRadius.TopLeft - Math.Max(BorderThickness.Top, BorderThickness.Left), 0, maxRadius);
        var right = Math.Clamp(CornerRadius.TopRight - Math.Max(BorderThickness.Top, BorderThickness.Right), 0, maxRadius);
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
