using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
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
            _reflectionShift.X = _reflectionShift.Y = 0;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        _reflectionShift.X = -origin.X / dpi.DpiScaleX * .10;
        _reflectionShift.Y = -origin.Y / dpi.DpiScaleY * .06;
    }
}
