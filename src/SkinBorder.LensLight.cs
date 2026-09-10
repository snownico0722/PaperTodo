using System;
using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Window? _lensWindow;
    private string? _lightingSkin;
    internal bool HasLensLightSubscription => _lensWindow != null;

    // Event-driven optics only: lens pointer light and Aero world-space parallax.
    // No idle clock and no effect or layout invalidation on the editor subtree.
    private void SyncLensLight()
    {
        var window = !IsOutline && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin is PaperSkins.LiquidGlass or PaperSkins.Aero ? Window.GetWindow(this) : null;
        if (ReferenceEquals(window, _lensWindow) && _lightingSkin == Skin) return;
        DetachLensLight();
        _lensWindow = window;
        _lightingSkin = Skin;
        if (window == null) return;
        if (Skin == PaperSkins.LiquidGlass) window.PreviewMouseMove += OnLensPointerMoved;
        if (Skin == PaperSkins.Aero)
        { window.LocationChanged += OnAeroLocation; OnAeroLocation(window, EventArgs.Empty); }
        window.Closed += OnLensClosed;
    }
    private void DetachLensLight()
    {
        if (_lensWindow != null)
        {
            _lensWindow.PreviewMouseMove -= OnLensPointerMoved;
            _lensWindow.LocationChanged -= OnAeroLocation;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _reflectionShift.X = _reflectionShift.Y = 0;
        _lensLight.Center = _lensLight.GradientOrigin = new Point(.24, .05);
    }
    private void OnAeroLocation(object? sender, EventArgs e)
    {
        if (_lensWindow is not { IsVisible: true } window || window.WindowState == WindowState.Minimized ||
            !double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
        // Absolute DIPs make reflection width independent of paper size and DPI.
        // Continuous translation (no modulo wrap/jump) supplies subtle parallax.
        _reflectionShift.X = -window.Left * .10;
        _reflectionShift.Y = -window.Top * .06;
    }
    private void OnLensClosed(object? sender, EventArgs e) => DetachLensLight();
    private void OnLensPointerMoved(object sender, MouseEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _lensWindow?.WindowState == WindowState.Minimized) return;
        var pointer = e.GetPosition(this);
        var point = new Point(Math.Clamp(pointer.X / ActualWidth, 0, 1), Math.Clamp(pointer.Y / ActualHeight, 0, 1));
        if ((point - _lensLight.Center).LengthSquared < .0004) return;
        _lensLight.Center = _lensLight.GradientOrigin = point;
        foreach (var slice in _slices) slice.Effect.Light = point;
    }
}
