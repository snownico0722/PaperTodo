using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

// A tether capsule is a transient presentation only. It deliberately owns neither PaperData
// geometry nor the ordinary/deep capsule state machines.
internal sealed class ExperimentalTetherCapsuleWindow : Window
{
    private readonly Action _activateTarget;
    private readonly Border _pill;
    private readonly TextBlock _label;
    private double _restingOpacity;
    private bool _alwaysTransparent;
    private readonly bool _normalTopmost;
    private IntPtr _fullscreenAvoidanceWindow;
    private bool _experimentalPassive;
    private bool _closingForOwner;

    public ExperimentalTetherCapsuleWindow(
        string label,
        string targetTitle,
        Action activateTarget,
        bool normalTopmost,
        double restingOpacity)
    {
        _activateTarget = activateTarget;
        _normalTopmost = normalTopmost;
        _restingOpacity = Math.Clamp(restingOpacity, 0.2, 1.0);

        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = normalTopmost;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);

        _label = new TextBlock
        {
            Text = label,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = AppTypography.Scale(220)
        };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            MinWidth = AppTypography.Scale(92),
            MaxWidth = AppTypography.Scale(252),
            Cursor = Cursors.Hand,
            ToolTip = targetTitle,
            Child = _label
        };
        Content = _pill;
        UpdateTheme();

        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (_experimentalPassive)
            {
                ApplyExperimentalPassiveState();
            }
        };
        MouseEnter += (_, _) =>
        {
            if (!_experimentalPassive)
            {
                Opacity = _alwaysTransparent ? _restingOpacity : 1;
            }
        };
        MouseLeave += (_, _) =>
        {
            if (!_experimentalPassive)
            {
                Opacity = _restingOpacity;
            }
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_experimentalPassive)
            {
                return;
            }

            e.Handled = true;
            _activateTarget();
        };
        Closed += (_, _) =>
        {
            if (!_closingForOwner)
            {
                UnexpectedlyClosed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? UnexpectedlyClosed;

    public void ShowAt(DeviceScreenRect anchorBounds)
    {
        Opacity = _restingOpacity;
        Show();
        UpdateLayout();

        if (!WindowNative.TryGetWindowDeviceBounds(this, out var ownBounds))
        {
            return;
        }

        var anchorCenter = new DeviceScreenPoint(
            anchorBounds.Left + anchorBounds.Width / 2.0,
            anchorBounds.Top + anchorBounds.Height / 2.0);
        var left = anchorBounds.Left;
        var top = anchorBounds.Top;
        if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                anchorCenter,
                this,
                out var monitor))
        {
            left = Math.Clamp(
                left,
                monitor.WorkArea.Left,
                Math.Max(
                    monitor.WorkArea.Left,
                    monitor.WorkArea.Right - ownBounds.Width));
            top = Math.Clamp(
                top,
                monitor.WorkArea.Top,
                Math.Max(
                    monitor.WorkArea.Top,
                    monitor.WorkArea.Bottom - ownBounds.Height));
        }

        _ = WindowNative.TryMoveWindowDevicePosition(
            this,
            new DeviceScreenPoint(left, top));
        if (_experimentalPassive)
        {
            WindowNative.ApplyBottomZOrder(this);
        }
        else
        {
            SetFullscreenAvoidance(_fullscreenAvoidanceWindow);
        }
    }

    public void SetExperimentalPassive(bool enabled)
    {
        if (_experimentalPassive == enabled)
        {
            return;
        }

        _experimentalPassive = enabled;
        ApplyExperimentalPassiveState();
    }

    public void UpdateTheme()
    {
        _pill.Background = Theme.PaperBrush;
        _pill.BorderBrush = Theme.PaperBorderBrush;
        _label.Foreground = Theme.TextBrush;
    }

    public void SetFullscreenAvoidance(IntPtr avoidanceWindow)
    {
        _fullscreenAvoidanceWindow = avoidanceWindow;
        if (_experimentalPassive)
        {
            return;
        }

        var effectiveTopmost =
            _normalTopmost && avoidanceWindow == IntPtr.Zero;
        Topmost = effectiveTopmost;
        if (IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(
                this,
                effectiveTopmost,
                avoidanceWindow);
        }
    }

    public void UpdateRestingOpacity(double opacity, bool alwaysTransparent)
    {
        _restingOpacity = Math.Clamp(opacity, 0.2, 1.0);
        _alwaysTransparent = alwaysTransparent;
        if (!_experimentalPassive)
        {
            Opacity = _alwaysTransparent || !IsMouseOver
                ? _restingOpacity
                : 1.0;
        }
    }

    public void CloseForOwner()
    {
        if (_closingForOwner)
        {
            return;
        }

        _closingForOwner = true;
        Close();
    }

    private void ApplyExperimentalPassiveState()
    {
        WindowNative.SetInputPassthrough(this, _experimentalPassive);
        if (_experimentalPassive)
        {
            Topmost = false;
            WindowNative.ApplyBottomZOrder(this);
            return;
        }

        var effectiveTopmost =
            _normalTopmost &&
            _fullscreenAvoidanceWindow == IntPtr.Zero;
        Topmost = effectiveTopmost;
        Opacity = _alwaysTransparent || !IsMouseOver
            ? _restingOpacity
            : 1.0;
        if (IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(
                this,
                effectiveTopmost,
                _fullscreenAvoidanceWindow);
        }
    }
}
