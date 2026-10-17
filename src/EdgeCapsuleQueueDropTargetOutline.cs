using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

/// <summary>
/// Disposable, input-transparent outline marking which edge queue a free-queue drag would join.
///
/// Deliberately independent of <see cref="ExperimentalAttachmentPreviewWindow"/>. That window is
/// gated on "not a docked edge capsule" and describes snapping a folded capsule onto a window or
/// screen edge; sharing it would couple the experimental magnet feature to free queues for no gain.
///
/// Owns no paper geometry and never participates in the edge-capsule presenter.
/// </summary>
internal sealed class EdgeCapsuleQueueDropTargetOutline : Window
{
    private const int OutlineMarginDevice = 3;
    private const double RestingOpacity = 0.86;

    private bool _closed;
    private string _queueKey = "";

    public EdgeCapsuleQueueDropTargetOutline()
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
        Content = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(2),
            BorderBrush = Theme.ActiveBrush,
            Background = Theme.Tint((byte)(Theme.IsDark ? 34 : 22)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = activeColor,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = Theme.IsDark ? 0.58 : 0.34
            }
        };
        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            // The outline is feedback only: a click must reach whatever is underneath it.
            WindowNative.SetInputPassthrough(this, enabled: true);
        };
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Move the outline onto <paramref name="bounds"/>. Returns whether this call changed the
    /// target queue, which the caller uses to decide between a resting show and an emphasized
    /// attention pulse.
    /// </summary>
    public bool ShowAt(
        DeviceScreenRect bounds,
        string queueKey,
        IntPtr fullscreenAvoidanceWindow,
        bool animate)
    {
        if (_closed)
        {
            return false;
        }

        // A different queue is a genuinely different target and deserves the pulse; sliding within
        // the same queue must not re-pulse, or it would strobe on every pointer move.
        var targetChanged = !string.Equals(_queueKey, queueKey, StringComparison.Ordinal);
        _queueKey = queueKey;

        var outlineBounds = bounds.IsEmpty
            ? bounds
            : new DeviceScreenRect(
                bounds.Left - OutlineMarginDevice,
                bounds.Top - OutlineMarginDevice,
                bounds.Right + OutlineMarginDevice,
                bounds.Bottom + OutlineMarginDevice);
        var firstShow = !IsVisible;
        if (firstShow)
        {
            try
            {
                Show();
            }
            catch (Exception ex)
            {
                return targetChanged;
            }
        }

        if (outlineBounds.IsEmpty)
        {
            return targetChanged;
        }

        if (!WindowNative.TrySetWindowDeviceBounds(this, outlineBounds))
        {
            return targetChanged;
        }

        var topmost = fullscreenAvoidanceWindow == IntPtr.Zero;
        Topmost = topmost;
        WindowNative.ApplyTopmostZOrder(this, topmost, fullscreenAvoidanceWindow);

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = RestingOpacity;
            return targetChanged;
        }

        if (firstShow || targetChanged)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = targetChanged ? 0.42 : 0;
            AnimationHelper.FadeTo(
                this,
                RestingOpacity,
                duration: targetChanged ? 90 : 120,
                easing: AnimationHelper.QuickEase);
        }

        return targetChanged;
    }

    public void CloseForOwner()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _queueKey = "";
        BeginAnimation(OpacityProperty, null);
        Close();
    }
}
