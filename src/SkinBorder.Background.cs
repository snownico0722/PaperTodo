using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    // Aero parallax still observes its own HWND position and WPF opacity.
    // No surface observes or captures the desktop behind the window.
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
            UpdateAeroReflection();
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
