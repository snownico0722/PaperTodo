using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

// Disposable, input-transparent feedback for an ordinary capsule's pending snap.
// It owns no paper geometry and never participates in the edge-capsule presenter.
internal sealed class ExperimentalAttachmentPreviewWindow : Window
{
    private const int OutlineMarginDevice = 3;
    private bool _closed;

    public ExperimentalAttachmentPreviewWindow(bool hideInCapture)
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Opacity = 0;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var activeColor = Theme.ActiveBrush is SolidColorBrush activeBrush
            ? activeBrush.Color
            : Colors.Gray;
        var outline = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(2),
            BorderBrush = Theme.ActiveBrush,
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 34 : 22)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = activeColor,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = Theme.IsDark ? 0.58 : 0.34
            }
        };
        Content = outline;
        SourceInitialized += (_, _) =>
        {
            WindowCaptureExclusion.Apply(this, hideInCapture);
            WindowNative.ApplyNoActivateStyle(this);
            WindowNative.SetInputPassthrough(this, enabled: true);
        };
        Closed += (_, _) => _closed = true;
    }

    public void ShowAt(
        DeviceScreenRect snapBounds,
        IntPtr fullscreenAvoidanceWindow,
        bool emphasize,
        bool animate)
    {
        if (_closed || snapBounds.IsEmpty)
        {
            return;
        }

        var outlineBounds = new DeviceScreenRect(
            snapBounds.Left - OutlineMarginDevice,
            snapBounds.Top - OutlineMarginDevice,
            snapBounds.Right + OutlineMarginDevice,
            snapBounds.Bottom + OutlineMarginDevice);
        var firstShow = !IsVisible;
        if (firstShow)
        {
            Show();
        }

        if (!WindowNative.TrySetWindowDeviceBounds(this, outlineBounds))
        {
            return;
        }

        var topmost = fullscreenAvoidanceWindow == IntPtr.Zero;
        Topmost = topmost;
        WindowNative.ApplyTopmostZOrder(
            this,
            topmost,
            fullscreenAvoidanceWindow);

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0.86;
            return;
        }

        if (firstShow || emphasize)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = emphasize ? 0.42 : 0;
            AnimationHelper.FadeTo(
                this,
                0.86,
                duration: emphasize ? 90 : 120,
                easing: AnimationHelper.QuickEase);
        }
    }

    public void CloseForOwner()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        BeginAnimation(OpacityProperty, null);
        Close();
    }
}
