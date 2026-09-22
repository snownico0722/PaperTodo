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
