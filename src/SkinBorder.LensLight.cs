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
    private Point _lensPointer = new(.24, .05);
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
        if (Skin == PaperSkins.LiquidGlass)
        {
            root.PreviewMouseMove += OnLensPointerMoved;
            root.MouseLeave += OnLensPointerLeft;
        }
        if (Skin == PaperSkins.Aero)
        { if (window != null) window.LocationChanged += OnAeroLocation; OnAeroLocation(window, EventArgs.Empty); }
        if (window != null) window.Closed += OnLensClosed;
    }
    private void DetachLensLight()
    {
        if (_lensRoot != null)
        {
            _lensRoot.PreviewMouseMove -= OnLensPointerMoved;
            _lensRoot.MouseLeave -= OnLensPointerLeft;
        }
        _lensRoot = null;
        if (_lensWindow != null)
        {
            _lensWindow.LocationChanged -= OnAeroLocation;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _reflectionShift.X = _reflectionShift.Y = 0;
        ResetLensLight();
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
        // Compare DIPs, not a percentage of the whole panel: on a wide paper the
        // old threshold could jump a dozen pixels at a time.
        var delta = new Vector((point.X - _lensPointer.X) * ActualWidth,
            (point.Y - _lensPointer.Y) * ActualHeight);
        if (delta.LengthSquared < 1) return;
        _lensPointer = point;
        UpdateLensLightGeometry();
    }
    private void OnLensPointerLeft(object sender, MouseEventArgs e) => ResetLensLight();
    private void ResetLensLight()
    {
        _lensPointer = new Point(.24, .05);
        UpdateLensLightGeometry();
    }
    private void UpdateLensLightGeometry()
    {
        // A circular light in DIPs stays circular on a long capsule, a tall menu and
        // a wide reading panel. Only the existing mutable brush changes on input.
        var radius = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * .8, 64, 160);
        _lensLight.RadiusX = _lensLight.RadiusY = radius;
        _lensLight.Center = _lensLight.GradientOrigin =
            new Point(_lensPointer.X * ActualWidth, _lensPointer.Y * ActualHeight);
    }
}
