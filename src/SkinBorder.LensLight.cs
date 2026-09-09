using System;
using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private Window? _lensWindow;
    internal bool HasLensLightSubscription => _lensWindow != null;

    // Pointer-only gloss. No idle animation clocks, movement timers or location hooks.
    private void SyncLensLight()
    {
        var window = !IsOutline && IsLoaded && IsVisible && !_highContrast && _animateReflection &&
            Skin == PaperSkins.LiquidGlass ? Window.GetWindow(this) : null;
        if (ReferenceEquals(window, _lensWindow)) return;
        DetachLensLight();
        _lensWindow = window;
        if (window == null) return;
        window.PreviewMouseMove += OnLensPointerMoved;
        window.Closed += OnLensClosed;
    }
    private void DetachLensLight()
    {
        if (_lensWindow != null)
        {
            _lensWindow.PreviewMouseMove -= OnLensPointerMoved;
            _lensWindow.Closed -= OnLensClosed;
        }
        _lensWindow = null;
        _lensLight.Center = _lensLight.GradientOrigin = new Point(.24, .05);
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
