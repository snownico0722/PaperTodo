using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Window? _lensWindow;
    private UIElement? _lensRoot;
    private string? _lightingSkin;
    internal bool HasLensLightSubscription => _lensRoot != null;

    // Event-driven optics only: lens pointer light and Aero world-space parallax.
    // No idle clock and no effect or layout invalidation on the editor subtree.
    private void SyncLensLight()
    {
        var root = !IsOutline && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin is PaperSkins.LiquidGlass or PaperSkins.Aero ? (PresentationSource.FromVisual(this) as HwndSource)?.RootVisual as UIElement : null;
        if (ReferenceEquals(root, _lensRoot) && _lightingSkin == Skin) return;
        DetachLensLight();
        _lensRoot = root;
        var window = root as Window;
        _lensWindow = window;
        _lightingSkin = Skin;
        if (root == null) return;
        if (Skin == PaperSkins.LiquidGlass) root.PreviewMouseMove += OnLensPointerMoved;
        if (Skin == PaperSkins.Aero)
        { if (window != null) window.LocationChanged += OnAeroLocation; OnAeroLocation(window, EventArgs.Empty); }
        if (window != null) window.Closed += OnLensClosed;
    }
    private void DetachLensLight()
    {
        if (_lensRoot != null) _lensRoot.PreviewMouseMove -= OnLensPointerMoved;
        _lensRoot = null;
        if (_lensWindow != null)
        {
            _lensWindow.LocationChanged -= OnAeroLocation;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _reflectionShift.X = _reflectionShift.Y = 0;
        _lensLight.Center = _lensLight.GradientOrigin = new Point(.24, .05);
    }
    private void OnAeroLocation(object? sender, EventArgs e)
    {
        if (!IsLoaded || !IsVisible || _lensWindow?.WindowState == WindowState.Minimized) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var origin = PointToScreen(new Point());
        // Absolute DIPs make the same reflection continuous on Window and Popup roots.
        _reflectionShift.X = -origin.X / dpi.DpiScaleX * .10;
        _reflectionShift.Y = -origin.Y / dpi.DpiScaleY * .06;
    }
    private void OnLensClosed(object? sender, EventArgs e) => DetachLensLight();
    private void OnLensPointerMoved(object sender, MouseEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _lensWindow?.WindowState == WindowState.Minimized) return;
        var pointer = e.GetPosition(this);
        var point = new Point(Math.Clamp(pointer.X / ActualWidth, 0, 1), Math.Clamp(pointer.Y / ActualHeight, 0, 1));
        if ((point - _lensLight.Center).LengthSquared < .0004) return;
        _lensLight.Center = _lensLight.GradientOrigin = point;
    }
}
