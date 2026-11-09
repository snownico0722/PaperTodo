using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    // Aero parallax observes only HWND position and display changes.
    // Auxiliary materials no longer sample the desktop or watch ancestor opacity.
    private MaterialSurfaceHost? _materialHost;
    internal bool HasMaterialHostSubscription => _materialHost?.IsObserving == true;

    internal void RefreshBackground()
    {
        var observe = IsLoaded && IsVisible && !IsOutline && !UseLightweightMaterial &&
            !_highContrast && _animateReflection && Skin == PaperSkins.Aero;
        if (observe)
        {
            _materialHost ??= new MaterialSurfaceHost(this, OnMaterialHostChanged);
            _materialHost.Observe(PresentationSource.FromVisual(this) as HwndSource);
        }
        else
        {
            _materialHost?.Dispose();
            _materialHost = null;
        }
        UpdateAeroReflection();
    }

    private void OnMaterialHostChanged(MaterialHostChange change)
    {
        if ((change & MaterialHostChange.Unavailable) != 0)
        {
            ReleaseMaterialResources();
            return;
        }
        if ((change & MaterialHostChange.Environment) != 0)
            RefreshSkin();
        else if (change == MaterialHostChange.Translation)
        {
            // The reflection brush's mutable transform already invalidates WPF paint.
            // Do not request a second full-surface redraw on a translation-only move.
            UpdateAeroReflection();
            return;
        }
        else
            RefreshBackground();
        InvalidateVisual();
    }

    private void ReleaseMaterialResources()
    {
        _materialHost?.Dispose();
        _materialHost = null;
        UpdateAeroReflection();
    }

    internal bool HasAeroReflectionSubscription => !IsOutline && !UseLightweightMaterial &&
        IsLoaded && IsVisible && !_highContrast && _animateReflection && Skin == PaperSkins.Aero &&
        HasMaterialHostSubscription;

    // One host observer drives both Window and Popup parallax. No mouse subscription,
    // idle clock, duplicate Window.LocationChanged handler, or texture-size dependency.
    private void UpdateAeroReflection()
    {
        if (!HasAeroReflectionSubscription || PresentationSource.FromVisual(this) is not HwndSource source ||
            !MaterialSurfaceHost.TryGetScreenOrigin(this, source, out var origin))
        {
            _reflectionShift.Matrix = Matrix.Identity;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        // Publish both coordinates together. Two separate Freezable mutations notify
        // every brush consumer twice, even though they describe one window movement.
        _reflectionShift.Matrix = new Matrix(1, 0, 0, 1,
            -origin.X / dpi.DpiScaleX * .10, -origin.Y / dpi.DpiScaleY * .06);
    }
}
