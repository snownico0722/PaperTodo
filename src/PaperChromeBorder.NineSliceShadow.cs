using System;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

// Ordinary Paper uses a cached silhouette separate from the native material shadow.
internal partial class PaperChromeBorder
{
    private bool _hasNineSliceShadow;
    private double _shadowBlur;
    private double _shadowDepth;
    private double _shadowOpacity;
    private bool _shadowCapsuleShape;

    internal bool HasNineSliceShadow => _hasNineSliceShadow;

    protected void DrawNineSliceShadow(DrawingContext dc)
    {
        if (!_hasNineSliceShadow || RenderSize.Width <= 0 ||
            RenderSize.Height - _surfaceInset <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var radius = Math.Max(0, CornerRadius.TopLeft);
        NineSlicePaperShadow.Get(
            _shadowBlur, _shadowDepth, _shadowOpacity, radius, dpi, _shadowCapsuleShape)
            .Draw(dc, new Rect(0, _surfaceInset, RenderSize.Width, RenderSize.Height - _surfaceInset));
    }

    internal void SetNineSliceShadow(double blurRadius, double depth, double opacity, bool capsuleShape)
    {
        blurRadius = Math.Clamp(blurRadius, 0, 32);
        depth = Math.Clamp(depth, 0, 16);
        opacity = Math.Clamp(opacity, 0, 1);
        if (blurRadius <= 0 || opacity <= 0)
        {
            ClearNineSliceShadow();
            return;
        }
        if (_hasNineSliceShadow && _shadowBlur == blurRadius &&
            _shadowDepth == depth && _shadowOpacity == opacity &&
            _shadowCapsuleShape == capsuleShape)
            return;

        _hasNineSliceShadow = true;
        _shadowBlur = blurRadius;
        _shadowDepth = depth;
        _shadowOpacity = opacity;
        _shadowCapsuleShape = capsuleShape;
        InvalidateVisual();
    }

    internal void ClearNineSliceShadow()
    {
        if (!_hasNineSliceShadow) return;
        _hasNineSliceShadow = false;
        InvalidateVisual();
    }
}
