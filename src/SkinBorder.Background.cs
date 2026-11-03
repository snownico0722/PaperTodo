using System;
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
}
