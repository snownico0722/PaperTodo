using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

internal sealed record EdgeCapsuleDragWindowOptions
{
    public required EdgeCapsuleFloatingShape Shape { get; init; }
    public required double WindowChromeMargin { get; init; }
    public required double OutlineMargin { get; init; }
    public required double OutlineThickness { get; init; }
    public required double OutlineOverlap { get; init; }
    public required double LeftPadding { get; init; }
    public required double IconGap { get; init; }
    public required double RightPadding { get; init; }
    public required string Icon { get; init; }
    public required string Label { get; init; }
    public required double IconFontSize { get; init; }
    public required double LabelFontSize { get; init; }
    public required FontWeight LabelFontWeight { get; init; }
    public required FontFamily UiFontFamily { get; init; }
    public required FontFamily SymbolFontFamily { get; init; }
    public required XmlLanguage Language { get; init; }
    public required Brush PaperBrush { get; init; }
    public required Brush PaperBorderBrush { get; init; }
    public required Brush IconBrush { get; init; }
    public required Brush LabelBrush { get; init; }
    public required Brush OutlineBrush { get; init; }
    public required bool Topmost { get; init; }
}

// A detached capsule is a complete, real-size pill in its own HWND. It never reuses the docked
// one-sided tag or any of its edge-specific columns, margins, corners, or width animation state.
internal sealed class EdgeCapsuleDragWindow : Window
{
    private enum DockingHandoffAnimationPhase
    {
        Flight,
        Reveal
    }

    private sealed record DockingHandoffAnimation(
        DockingHandoffAnimationPhase Phase,
        EdgeCapsuleFloatingHandoffGeometry Geometry,
        EdgeCapsuleEdge Edge,
        double StartOpacity,
        long StartedAtTimestamp,
        long DurationTimestampTicks,
        Action<bool> Completed);

    private const int WmDpiChanged = 0x02E0;
    private readonly ScaleTransform _entranceScale = new(1, 1);
    private readonly double _widthDip;
    private readonly double _heightDip;
    private readonly Grid _root;
    private readonly Grid _surface;
    private readonly Border _outline;
    private DeviceScreenPoint _lastPointer;
    private DeviceScreenRect _surfaceDeviceBounds;
    private DockingHandoffAnimation? _dockingHandoffAnimation;
    private int _dpiSettleGeneration;
    private bool _dockingPresentationActive;
    private bool _closingByOwner;
    private bool _isClosed;

    public EdgeCapsuleDragWindow(EdgeCapsuleDragWindowOptions options)
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        FontFamily = options.UiFontFamily;
        Language = options.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);
        Topmost = options.Topmost;
        Opacity = 0;
        Debug.Assert(
            options.Shape.Visible && options.Shape.Kind == EdgeCapsuleSurfaceKind.FloatingFree,
            "EdgeCapsuleDragWindow only renders the FloatingFree shape.");
        _widthDip = options.Shape.WindowWidthDip;
        _heightDip = options.Shape.WindowHeightDip;
        (_root, _surface, _outline) = BuildContent(options);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }
        };
    }

    public event EventHandler? UnexpectedlyClosed;

    public void ShowWithEntrance(
        DeviceScreenPoint pointer,
        bool animate,
        double scaleFrom,
        int durationMilliseconds)
    {
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate)
        {
            _entranceScale.ScaleX = 1;
            _entranceScale.ScaleY = 1;
            MoveCenteredAt(pointer);
            Show();
            RefreshNativeMetricsLayout();
            MoveCenteredAt(pointer);
            Opacity = 1;
            return;
        }

        _entranceScale.ScaleX = scaleFrom;
        _entranceScale.ScaleY = scaleFrom;
        MoveCenteredAt(pointer);
        Show();
        RefreshNativeMetricsLayout();
        MoveCenteredAt(pointer);
        Opacity = 1;
        var animation = new DoubleAnimation
        {
            From = scaleFrom,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void MoveCenteredAt(DeviceScreenPoint pointer)
    {
        if (_dockingPresentationActive)
        {
            return;
        }

        _lastPointer = pointer;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, this, out var geometry))
        {
            return;
        }

        var width = Math.Max(1, (int)Math.Round(_widthDip * geometry.DpiScaleX, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(_heightDip * geometry.DpiScaleY, MidpointRounding.AwayFromZero));
        var left = (int)Math.Round(pointer.X - width / 2.0, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(pointer.Y - height / 2.0, MidpointRounding.AwayFromZero);
        var bounds = new DeviceScreenRect(left, top, left + width, top + height);
        if (!WindowNative.TrySetWindowDeviceBounds(this, bounds))
        {
            return;
        }
        if (!WindowNative.TryGetWindowDeviceBounds(this, out var actualBounds) ||
            actualBounds != bounds)
        {
            WindowNative.TrySetWindowDeviceBounds(this, bounds);
            WindowNative.TryGetWindowDeviceBounds(this, out actualBounds);
        }
        _surfaceDeviceBounds = actualBounds.IsEmpty ? bounds : actualBounds;
    }

    public void AnimateDockingHandoff(
        DeviceScreenRect dockingAnchorBounds,
        EdgeCapsuleEdge targetEdge,
        int durationMilliseconds,
        Action<bool> completed)
    {
        CancelDockingHandoffAnimation();
        if (_isClosed || dockingAnchorBounds.IsEmpty)
        {
            completed(false);
            return;
        }

        // A very quick release can overlap the entrance scale. The docking surface starts from the
        // real full-size pill, so remove that transform before sampling its physical rectangle.
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _entranceScale.ScaleX = 1;
        _entranceScale.ScaleY = 1;
        _surface.Opacity = 1;
        _outline.Opacity = 1;
        if (!TryGetCurrentSurfaceDeviceBounds(out var startSurfaceBounds))
        {
            completed(false);
            return;
        }

        var geometry = EdgeCapsuleGeometry.FloatingHandoffGeometry(
            startSurfaceBounds,
            dockingAnchorBounds,
            targetEdge);
        if (!geometry.IsUsable)
        {
            completed(false);
            return;
        }

        _dockingPresentationActive = true;

        // Expand only transparent native capacity at this phase boundary. The visible pill keeps
        // its exact current bounds inside that host and can subsequently reflow every frame without
        // ever being clipped by SetWindowPos.
        if (!ApplyDockingFrame(
                geometry.HostStartBounds,
                geometry.SurfaceStartBounds,
                targetEdge,
                refreshLayout: true))
        {
            completed(false);
            return;
        }

        _dpiSettleGeneration++;
        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Flight,
            geometry,
            targetEdge,
            1,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void AnimateDockingReveal(int durationMilliseconds, Action<bool> completed)
    {
        CancelDockingHandoffAnimation();
        if (_isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            completed(false);
            return;
        }

        // The confirmed docked host owns the final outline. Keep the floating body as the
        // anti-flash cover, but do not cross-fade two outlines with different edge geometry.
        _outline.Opacity = 0;
        var startOpacity = Math.Clamp(_surface.Opacity, 0, 1);
        if (startOpacity <= 0.001)
        {
            _surface.Opacity = 0;
            completed(true);
            return;
        }

        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Reveal,
            default,
            default,
            startOpacity,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void RestoreDockingCover()
    {
        if (_isClosed)
        {
            return;
        }

        _outline.Opacity = 1;
        _surface.Opacity = 1;
        WindowNative.BringToFrontNoActivate(this);
    }

    private void OnDockingHandoffFrame(object? sender, EventArgs e) =>
        AdvanceDockingHandoffFrame();

    private void AdvanceDockingHandoffFrame()
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null || _isClosed)
        {
            return;
        }

        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - animation.StartedAtTimestamp);
        var rawProgress = Math.Clamp(
            elapsed / (double)animation.DurationTimestampTicks,
            0,
            1);
        var progress = 1.0 - Math.Pow(1.0 - rawProgress, 3.0);
        if (animation.Phase == DockingHandoffAnimationPhase.Flight)
        {
            var hostBounds = EdgeCapsuleGeometry.InterpolateDeviceBounds(
                animation.Geometry.HostStartBounds,
                animation.Geometry.HostTargetBounds,
                progress);
            var surfaceBounds = EdgeCapsuleGeometry.InterpolateDeviceBounds(
                animation.Geometry.SurfaceStartBounds,
                animation.Geometry.SurfaceTargetBounds,
                progress);
            if (!ApplyDockingFrame(
                    hostBounds,
                    surfaceBounds,
                    animation.Edge,
                    refreshLayout: false))
            {
                CompleteDockingHandoffAnimation(reachedTarget: false);
                return;
            }
        }
        else
        {
            _surface.Opacity = Math.Clamp(
                animation.StartOpacity * (1.0 - progress),
                0,
                1);
        }

        if (rawProgress >= 1)
        {
            CompleteDockingHandoffAnimation(reachedTarget: true);
        }
    }

    private void CompleteDockingHandoffAnimation(bool reachedTarget)
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null)
        {
            return;
        }

        CompositionTarget.Rendering -= OnDockingHandoffFrame;
        if (!reachedTarget ||
            _isClosed ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            _dockingHandoffAnimation = null;
            animation.Completed(false);
            return;
        }

        if (animation.Phase == DockingHandoffAnimationPhase.Reveal)
        {
            _surface.Opacity = 0;
            _dockingHandoffAnimation = null;
            animation.Completed(true);
            return;
        }

        if (!ApplyDockingFrame(
                animation.Geometry.HostTargetBounds,
                animation.Geometry.SurfaceTargetBounds,
                animation.Edge,
                refreshLayout: false))
        {
            _dockingHandoffAnimation = null;
            animation.Completed(false);
            return;
        }

        // WM_DPICHANGED can be followed by a later WPF layout rewrite. Keep the hand-off active
        // until that work has drained, then verify both transparent host and visible surface.
        Dispatcher.BeginInvoke(
            (Action)(() => CompleteDockingHandoffEndpointSettle(animation)),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void CompleteDockingHandoffEndpointSettle(DockingHandoffAnimation animation)
    {
        if (!ReferenceEquals(animation, _dockingHandoffAnimation) || _isClosed)
        {
            return;
        }

        DeviceScreenRect actualHostBounds = default;
        DeviceScreenRect actualSurfaceBounds = default;
        var settled = ApplyDockingFrame(
                animation.Geometry.HostTargetBounds,
                animation.Geometry.SurfaceTargetBounds,
                animation.Edge,
                refreshLayout: true) &&
            WindowNative.TryGetWindowDeviceBounds(this, out actualHostBounds) &&
            actualHostBounds == animation.Geometry.HostTargetBounds &&
            TryGetVisualSurfaceDeviceBounds(out actualSurfaceBounds) &&
            EdgeCapsuleGeometry.DeviceBoundsMatch(
                actualSurfaceBounds,
                animation.Geometry.SurfaceTargetBounds,
                tolerance: 1);
        if (settled)
        {
            _surfaceDeviceBounds = actualSurfaceBounds;
        }
        _dockingHandoffAnimation = null;
        animation.Completed(settled);
    }

    private void CancelDockingHandoffAnimation()
    {
        if (_dockingHandoffAnimation == null)
        {
            return;
        }

        var animation = _dockingHandoffAnimation;
        _dockingHandoffAnimation = null;
        CompositionTarget.Rendering -= OnDockingHandoffFrame;
        animation.Completed(false);
    }

    private bool ApplyDockingFrame(
        DeviceScreenRect hostBounds,
        DeviceScreenRect surfaceBounds,
        EdgeCapsuleEdge edge,
        bool refreshLayout)
    {
        if (_isClosed || hostBounds.IsEmpty || surfaceBounds.IsEmpty)
        {
            return false;
        }

        _surface.HorizontalAlignment = edge == EdgeCapsuleEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        _surface.VerticalAlignment = VerticalAlignment.Center;
        if (!WindowNative.TrySetWindowDeviceBounds(this, hostBounds))
        {
            return false;
        }

        // SetWindowPos can synchronously change this HWND's per-monitor DPI. Size the child after
        // that hand-off, in the destination scale, so the requested physical surface does not grow
        // or shrink for one render tick merely because WPF changed coordinate spaces.
        var dpi = VisualTreeHelper.GetDpi(this);
        _surface.Width = surfaceBounds.Width / Math.Max(1, dpi.DpiScaleX);
        _surface.Height = surfaceBounds.Height / Math.Max(1, dpi.DpiScaleY);
        if (refreshLayout)
        {
            RefreshNativeMetricsLayout();
            if (!WindowNative.TrySetWindowDeviceBounds(this, hostBounds))
            {
                return false;
            }
        }

        _surfaceDeviceBounds = surfaceBounds;
        return true;
    }

    private bool TryGetCurrentSurfaceDeviceBounds(out DeviceScreenRect bounds)
    {
        if (TryGetVisualSurfaceDeviceBounds(out bounds))
        {
            return true;
        }
        if (!_surfaceDeviceBounds.IsEmpty)
        {
            bounds = _surfaceDeviceBounds;
            return true;
        }
        return WindowNative.TryGetWindowDeviceBounds(this, out bounds) && !bounds.IsEmpty;
    }

    private bool TryGetVisualSurfaceDeviceBounds(out DeviceScreenRect bounds)
    {
        bounds = default;
        if (_isClosed || PresentationSource.FromVisual(_surface) == null ||
            !double.IsFinite(_surface.ActualWidth) ||
            !double.IsFinite(_surface.ActualHeight) ||
            _surface.ActualWidth <= 0 ||
            _surface.ActualHeight <= 0)
        {
            return false;
        }

        var origin = _surface.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(_surface);
        var left = (int)Math.Round(origin.X, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(origin.Y, MidpointRounding.AwayFromZero);
        var width = Math.Max(1, (int)Math.Round(
            _surface.ActualWidth * Math.Max(1, dpi.DpiScaleX),
            MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(
            _surface.ActualHeight * Math.Max(1, dpi.DpiScaleY),
            MidpointRounding.AwayFromZero));
        bounds = new DeviceScreenRect(left, top, left + width, top + height);
        return true;
    }

    private static long AnimationDurationTicks(int durationMilliseconds) =>
        Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency * Math.Max(1, durationMilliseconds) / 1000.0));

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            var generation = ++_dpiSettleGeneration;
            if (_dockingPresentationActive)
            {
                // Once pointer ownership ends, every hand-off/reveal target is expressed in physical
                // pixels. Never run the old drag recenter path between the two animation phases.
                return IntPtr.Zero;
            }
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!_isClosed && generation == _dpiSettleGeneration)
                    {
                        SettleDpiPresentation(generation, scheduleVerification: true);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    private void SettleDpiPresentation(int generation, bool scheduleVerification)
    {
        if (_isClosed || generation != _dpiSettleGeneration)
        {
            return;
        }

        MoveCenteredAt(_lastPointer);
        RefreshNativeMetricsLayout();
        MoveCenteredAt(_lastPointer);
        if (!scheduleVerification || generation != _dpiSettleGeneration)
        {
            return;
        }

        // One later pass observes the client area after WPF's first render at the destination DPI.
        // It is topology-only work, never part of ordinary pointer-move frames.
        Dispatcher.BeginInvoke(
            (Action)(() => SettleDpiPresentation(generation, scheduleVerification: false)),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RefreshNativeMetricsLayout()
    {
        _surface.InvalidateMeasure();
        _surface.InvalidateArrange();
        _root.InvalidateMeasure();
        _root.InvalidateArrange();
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    public void CloseFromOwner()
    {
        if (_closingByOwner)
        {
            return;
        }

        _closingByOwner = true;
        CancelDockingHandoffAnimation();
        Content = null;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _dpiSettleGeneration++;
        base.OnClosed(e);
        if (!_closingByOwner)
        {
            UnexpectedlyClosed?.Invoke(this, EventArgs.Empty);
        }
        // UnexpectedlyClosed lets the owner clear its host reference first. Cancellation can then
        // complete the in-flight operation exactly once without re-entering owner recovery through
        // a window that has already closed.
        CancelDockingHandoffAnimation();
    }

    private (Grid Root, Grid Surface, Border Outline) BuildContent(
        EdgeCapsuleDragWindowOptions options)
    {
        var root = new Grid
        {
            Background = null,
            IsHitTestVisible = false
        };
        var surface = new Grid
        {
            Background = null,
            IsHitTestVisible = false,
            RenderTransform = _entranceScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        root.Children.Add(surface);

        surface.Children.Add(new Border
        {
            Margin = new Thickness(options.WindowChromeMargin),
            Background = options.PaperBrush,
            BorderBrush = options.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(options.Shape.CornerRadiusDip),
            SnapsToDevicePixels = true
        });

        var shell = new Grid
        {
            Margin = new Thickness(options.WindowChromeMargin),
            Height = options.Shape.BodyHeightDip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        var content = new Grid
        {
            Margin = new Thickness(options.LeftPadding, 0, options.RightPadding, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = options.Icon,
            Foreground = options.IconBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = options.IconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var label = new TextBlock
        {
            Text = options.Label,
            Foreground = options.LabelBrush,
            FontFamily = options.UiFontFamily,
            FontSize = options.LabelFontSize,
            FontWeight = options.LabelFontWeight,
            Margin = new Thickness(options.IconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AppTypography.ApplyTextRendering(label);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var contentArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(options.Shape.CornerRadiusDip),
            Child = content
        };
        shell.Children.Add(contentArea);
        Panel.SetZIndex(shell, 10);
        surface.Children.Add(shell);

        var outline = new Border
        {
            Margin = new Thickness(options.OutlineMargin),
            BorderBrush = options.OutlineBrush,
            BorderThickness = new Thickness(options.OutlineThickness),
            CornerRadius = new CornerRadius(options.Shape.CornerRadiusDip + options.OutlineThickness - options.OutlineOverlap),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = options.Shape.OutlineVisible ? Visibility.Visible : Visibility.Collapsed,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(outline, 20);
        surface.Children.Add(outline);
        return (root, surface, outline);
    }
}
