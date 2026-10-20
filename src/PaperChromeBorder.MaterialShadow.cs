using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal partial class PaperChromeBorder
{
    private static readonly Pen[] MaterialShadowPens = CreateMaterialShadowPens();
    private readonly ScaleTransform _materialShadowScale = new();
    private double _materialShadowMargin;

    internal void SetMaterialShadowMargin(double margin)
    {
        margin = Math.Max(0, margin);
        if (_materialShadowMargin == margin) return;
        _materialShadowMargin = margin;
        InvalidateVisual();
    }

    protected void DrawMaterialShadow(DrawingContext dc)
    {
        if (_materialShadowMargin <= 0 || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Leave one physical pixel for antialiasing/layout rounding. In particular,
        // the zero-gutter native start frame must not grow a clipped capsule shadow.
        var guard = 1 / Math.Min(dpi.DpiScaleX, dpi.DpiScaleY);
        var extent = Math.Min(7, Math.Max(0, _materialShadowMargin - guard));
        if (extent <= 0) return;
        var scale = extent / 7;
        _materialShadowScale.ScaleX = _materialShadowScale.ScaleY = scale;
        dc.PushTransform(_materialShadowScale);
        var radius = CornerRadius.TopLeft / scale;
        for (var i = 0; i < MaterialShadowPens.Length; i++)
        {
            var expansion = (i + .5) * 1.4;
            var rect = new Rect(0, 0, RenderSize.Width / scale, RenderSize.Height / scale);
            rect.Inflate(expansion, expansion);
            dc.DrawRoundedRectangle(null, MaterialShadowPens[i], rect,
                radius + expansion, radius + expansion);
        }
        dc.Pop();
    }

    private static Pen[] CreateMaterialShadowPens()
    {
        var alpha = new byte[] { 27, 17, 9, 4, 1 };
        var pens = new Pen[alpha.Length];
        for (var i = 0; i < alpha.Length; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha[i], 0, 0, 0));
            brush.Freeze();
            pens[i] = new Pen(brush, 1.4);
            pens[i].Freeze();
        }
        return pens;
    }
}
