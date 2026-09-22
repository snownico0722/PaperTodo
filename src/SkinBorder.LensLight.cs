using System;
using System.Windows;
using System.Windows.Interop;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Window? _lensWindow;
    private UIElement? _lensRoot;
    private string? _lightingSkin;
    internal bool HasLensLightSubscription => _lensRoot != null;

    // Event-driven Aero parallax only. No idle clock or layout invalidation on the editor subtree.
    private void SyncLensLight()
    {
        var root = !IsOutline && !UseLightweightMaterial && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin == PaperSkins.Aero ? (PresentationSource.FromVisual(this) as HwndSource)?.RootVisual as UIElement : null;
        if (ReferenceEquals(root, _lensRoot) && _lightingSkin == Skin) return;
        DetachLensLight();
        _lensRoot = root;
        _lensWindow = root as Window;
        _lightingSkin = Skin;
        if (root == null) return;
        if (_lensWindow != null)
        {
            _lensWindow.LocationChanged += OnAeroLocation;
            OnAeroLocation(_lensWindow, EventArgs.Empty);
            _lensWindow.Closed += OnLensClosed;
        }
    }

    private void DetachLensLight()
    {
        _lensRoot = null;
        if (_lensWindow != null)
        {
            _lensWindow.LocationChanged -= OnAeroLocation;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _reflectionShift.X = _reflectionShift.Y = 0;
    }

    private void OnAeroLocation(object? sender, EventArgs e)
    {
        if (!IsLoaded || !IsVisible || _lensWindow?.WindowState == WindowState.Minimized) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var origin = PointToScreen(new Point());
        _reflectionShift.X = -origin.X / dpi.DpiScaleX * .10;
        _reflectionShift.Y = -origin.Y / dpi.DpiScaleY * .06;
    }

    private void OnLensClosed(object? sender, EventArgs e) => DetachLensLight();
}
