using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

// Standalone "collapse-all" master capsule. It is permanently pinned at deep-capsule
// slot 0 (real capsules shift down to slot 1..N). Clicking it toggles whether the
// real capsules are retracted behind it. It owns only its own pill chrome and the
// vertical stack anchor; the controller drives the retract/release of the real
// capsule windows.
public sealed class MasterCapsuleWindow : Window
{
    private enum MasterGestureState
    {
        Idle,
        Pending,
        Dragging
    }

    private sealed record MasterDragSession(
        DeviceScreenPoint StartScreenPosition,
        double StartTopMargin);

    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    // Compact internal metrics controlling how tightly the glyph + label sit inside the pill.
    // The master owns exactly the width it renders; no full pill is hidden outside its HWND.
    private const double WindowChromeMargin = EdgeCapsuleLayout.WindowChromeMargin;
    private const double MasterLeftPadding = 5;
    private const double MasterGlyphGap = 4;
    private const double MasterRightPadding = 3;
    private const double MasterInteriorBorderThickness = 1;

    private readonly AppController _controller;
    private double MasterGlyphFontSize => AppTypography.Scale(12);
    private double MasterLabelFontSize => VisualTextSizes.FontSize(12, _controller.State.CapsuleTextSize);
    private FontWeight MasterLabelFontWeight => _controller.State.CapsuleTextBold
        ? FontWeights.SemiBold
        : FontWeights.Normal;

    // Which queue this master serves: (monitor device, edge). Each docked-capsule queue has its
    // own master pill at slot 0 of that queue. Geometry resolves against this queue's monitor+edge.
    private EdgeCapsuleEdge _queueEdge;
    private string _queueMonitorDeviceName = "";

    private Border _pill = null!;
    private Border _hoverOverlay = null!;
    private TextBlock _glyph = null!;
    private TextBlock _label = null!;
    private StackPanel _contentStack = null!;

    private bool _isHovering;
    private int _count;
    private bool _active;
    private MasterGestureState _gestureState;
    private double _currentTopDip = double.NaN;
    private MonitorGeometry? _animatedMonitorGeometry;
    private double _animatedWidthDip;
    private int _moveGeneration;
    private bool _isClosingForReal;
    // The master pill is dragged vertically only: it slides its queue's stack by driving the
    // shared start-top margin. It never detaches or changes edge/monitor — that is done by
    // dragging an individual side capsule to another edge / screen.
    private MasterDragSession? _dragSession;

    private static readonly DependencyProperty AnimatedTopProperty =
        DependencyProperty.Register(
            nameof(AnimatedTop),
            typeof(double),
            typeof(MasterCapsuleWindow),
            new PropertyMetadata(double.NaN, OnAnimatedTopChanged));

    private double AnimatedTop
    {
        get => (double)GetValue(AnimatedTopProperty);
        set => SetValue(AnimatedTopProperty, value);
    }

    public MasterCapsuleWindow(AppController controller, EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName)
    {
        _controller = controller;
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
        ConfigureWindow();
        BuildContent();
        UpdateToolTipSetting();
        // Clicking the pill must never pull foreground focus: activating this window would
        // deactivate whatever app was in front, forcing it to repaint — the click "flash".
        // WS_EX_NOACTIVATE makes the window unable to become the active/foreground window,
        // so the click toggles collapse-all without disturbing the current foreground app.
        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }

            // The HWND can acquire a different per-monitor DPI than the pre-show WPF visual.
            // Re-measure and re-anchor once that real source exists.
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!_isClosingForReal)
                    {
                        MoveToTarget(animate: false);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void ConfigureWindow()
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        // Don't steal foreground when first shown — activating would force every other
        // paper window to repaint, which reads as a whole-app flash.
        ShowActivated = false;
        // Start invisible; ShowPlaced() positions us first, then fades in, so we never
        // flash for one frame at the top-left (the default NaN → 0,0 position).
        Opacity = 0;
        RefreshEffectiveTopmost();
    }

    private void BuildContent()
    {
        var host = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = false
        };

        _pill = new Border
        {
            Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin),
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        // The pill background stays opaque (PaperBrush) at all times. Hover tint is a separate
        // overlay layered on top — the same shape as the pill — so the (semi-transparent)
        // HoverBrush never replaces the only opaque layer and let the desktop show through.
        var content = new Grid();

        _hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        content.Children.Add(_hoverOverlay);

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // Hug the left edge; the master pill is never truncated, so content sits flush left.
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(MasterLeftPadding, 0, MasterRightPadding, 0)
        };
        _contentStack = stack;

        _glyph = new TextBlock
        {
            Text = "▾",
            Foreground = Theme.TextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = MasterGlyphFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_glyph);

        _label = new TextBlock
        {
            Text = Strings.Get("CapsuleCollapseAllLabel"),
            Foreground = Theme.WeakTextBrush,
            FontSize = MasterLabelFontSize,
            FontWeight = MasterLabelFontWeight,
            Margin = new Thickness(MasterGlyphGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_label);
        content.Children.Add(stack);

        _pill.Child = content;
        host.Children.Add(_pill);
        Content = host;

        _pill.MouseEnter += (_, _) =>
        {
            _hoverOverlay.Background = Theme.HoverBrush;
            SetHover(true);
        };
        _pill.MouseLeave += (_, _) =>
        {
            _hoverOverlay.Background = Brushes.Transparent;
            SetHover(false);
        };
        _pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragSession = new MasterDragSession(
                DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this))),
                _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge));
            _gestureState = MasterGestureState.Pending;
            _pill.CaptureMouse();
            e.Handled = true;
        };
        _pill.PreviewMouseMove += (_, e) =>
        {
            var session = _dragSession;
            if (_gestureState == MasterGestureState.Idle ||
                session == null ||
                _pill.IsMouseCaptured != true)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishMasterGesture(commit: false);
                return;
            }

            var currentScreenPos = DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this)));
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(_queueMonitorDeviceName, this, out var geometry))
            {
                return;
            }

            var deltaX = (currentScreenPos.X - session.StartScreenPosition.X) / geometry.DpiScaleX;
            var deltaY = (currentScreenPos.Y - session.StartScreenPosition.Y) / geometry.DpiScaleY;
            if (_gestureState == MasterGestureState.Pending &&
                Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (_gestureState == MasterGestureState.Pending)
            {
                _gestureState = MasterGestureState.Dragging;
                ++_moveGeneration;
                _animatedMonitorGeometry = null;
                BeginAnimation(AnimatedTopProperty, null);
            }

            // The master stays pinned to its queue's edge; vertical drag slides that queue's stack
            // by driving the shared start-top margin. It never detaches or changes edge/monitor —
            // moving capsules between queues is done by dragging an individual side capsule.
            var targetMargin = session.StartTopMargin + deltaY;
            _controller.SetDeepCapsuleStartTopMargin(_queueMonitorDeviceName, _queueEdge, targetMargin);

            e.Handled = true;
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var wasDragging = FinishMasterGesture(commit: true, clearFocus: false);
            if (!wasDragging)
            {
                _controller.ToggleCapsuleCollapseAllActive(_queueMonitorDeviceName, _queueEdge);
            }

            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };
        _pill.LostMouseCapture += (_, _) => FinishMasterGesture(commit: false);
        _pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
        };
    }

    public void UpdateTheme()
    {
        // Pill background is always the opaque PaperBrush; the hover tint lives on the overlay.
        _pill.Background = Theme.PaperBrush;
        _pill.BorderBrush = Theme.PaperBorderBrush;
        _hoverOverlay.Background = _isHovering ? Theme.HoverBrush : Brushes.Transparent;
        _glyph.Foreground = Theme.TextBrush;
        _label.Foreground = Theme.WeakTextBrush;
    }

    public void UpdateTypography()
    {
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        _glyph.FontFamily = AppTypography.SymbolFontFamily;
        _glyph.FontSize = MasterGlyphFontSize;
        _label.FontFamily = AppTypography.UiFontFamily;
        _label.FontSize = MasterLabelFontSize;
        _label.FontWeight = MasterLabelFontWeight;
        MoveToTarget(animate: false);
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
    }

    public void RefreshEffectiveTopmost()
    {
        var topmost = !_controller.SuppressTopmostForFullscreenForeground &&
            !_controller.SuppressDeepCapsuleTopmostForContextMenu;
        Topmost = topmost;
        if (IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(this, topmost, _controller.FullscreenAvoidanceWindow);
        }
    }

    // count = number of real capsules behind the master; active = whether they are retracted.
    public void UpdateState(int count, bool active, bool animate)
    {
        _count = count;
        _active = active;
        ApplyStateVisuals();

        MoveToTarget(animate);
        RefreshEffectiveTopmost();
    }

    private void ApplyStateVisuals()
    {
        _glyph.Text = _active ? "▸" : "▾";
        _label.Text = _active
            ? string.Format(CultureInfo.CurrentUICulture, Strings.Get("CapsuleCollapseAllCountFormat"), _count)
            : Strings.Get("CapsuleCollapseAllLabel");
        _pill.ToolTip = _active
            ? Strings.Get("CapsuleCollapseAllCollapsedTip")
            : Strings.Get("CapsuleCollapseAllExpandedTip");
    }

    private void SetHover(bool hovering)
    {
        // Hover only changes the pill background (handled in the MouseEnter/Leave handlers);
        // the master pill does not move, so there is nothing to reposition here.
        _isHovering = hovering;
    }

    private bool FinishMasterGesture(bool commit, bool clearFocus = true)
    {
        var session = _dragSession;
        var wasDragging = _gestureState == MasterGestureState.Dragging && session != null;
        _gestureState = MasterGestureState.Idle;
        _dragSession = null;
        var hadCapture = _pill.IsMouseCaptured;
        if (_pill.IsMouseCaptured)
        {
            _pill.ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            // Live movement updates the queue immediately so the stack follows the pointer.
            // Only the explicit MouseUp path persists that value; every other exit restores the
            // session snapshot before the autosave timer can make the preview authoritative.
            _controller.SetDeepCapsuleStartTopMargin(
                _queueMonitorDeviceName,
                _queueEdge,
                commit
                    ? _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge)
                    : session!.StartTopMargin,
                commit);
        }

        if (hadCapture && clearFocus)
        {
            ClearCapsuleInteractionKeyboardFocus();
        }

        return wasDragging;
    }

    private void ClearCapsuleInteractionKeyboardFocus()
    {
        WindowNative.ClearCurrentThreadKeyboardFocus();
        Dispatcher.BeginInvoke(
            (Action)WindowNative.ClearCurrentThreadKeyboardFocus,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private double MasterDockedWidth(double pixelsPerDip)
    {
        var glyphWidth = Math.Max(
            MeasureText("▾", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip),
            MeasureText("▸", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip));
        var expandedLabelWidth = MeasureText(
            Strings.Get("CapsuleCollapseAllLabel"),
            MasterLabelFontSize,
            MasterLabelFontWeight,
            AppTypography.UiFontFamily,
            pixelsPerDip);
        var currentLabelWidth = MeasureText(
            _label.Text,
            MasterLabelFontSize,
            MasterLabelFontWeight,
            AppTypography.UiFontFamily,
            pixelsPerDip);
        var textWidth = Math.Max(expandedLabelWidth, currentLabelWidth);
        var bodyWidth = Math.Ceiling(
            MasterLeftPadding +
            glyphWidth +
            MasterGlyphGap +
            textWidth +
            MasterRightPadding +
            MasterInteriorBorderThickness);
        return Math.Max(1, bodyWidth + WindowChromeMargin);
    }

    internal double DesiredDockedWidth
    {
        get
        {
            var pixelsPerDip = WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry)
                    ? geometry.DpiScaleY
                    : VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return MasterDockedWidth(pixelsPerDip);
        }
    }

    // Mirror one real docked tag. The wall side is square and has no transparent margin; the
    // interior side owns its rounded cap and margin inside the actual top-level window bounds.
    private void ApplyMasterEdgeLayout()
    {
        var leftEdge = _queueEdge == EdgeCapsuleEdge.Left;
        var radius = EdgeCapsuleLayout.CornerRadius;
        var edgeCorner = leftEdge
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);

        _pill.Margin = leftEdge
            ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
            : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
        _pill.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pill.Width = double.NaN;
        _pill.CornerRadius = edgeCorner;
        _pill.BorderThickness = leftEdge
            ? new Thickness(0, MasterInteriorBorderThickness, MasterInteriorBorderThickness, MasterInteriorBorderThickness)
            : new Thickness(MasterInteriorBorderThickness, MasterInteriorBorderThickness, 0, MasterInteriorBorderThickness);
        _hoverOverlay.CornerRadius = edgeCorner;

        _contentStack.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _contentStack.Margin = leftEdge
            ? new Thickness(MasterRightPadding, 0, MasterLeftPadding, 0)
            : new Thickness(MasterLeftPadding, 0, MasterRightPadding, 0);

        _contentStack.Children.Clear();
        _label.Margin = leftEdge
            ? new Thickness(0, 0, MasterGlyphGap, 0)
            : new Thickness(MasterGlyphGap, 0, 0, 0);
        if (leftEdge)
        {
            _contentStack.Children.Add(_label);
            _contentStack.Children.Add(_glyph);
        }
        else
        {
            _contentStack.Children.Add(_glyph);
            _contentStack.Children.Add(_label);
        }
    }

    private static double MeasureText(
        string text,
        double fontSize,
        FontWeight weight,
        FontFamily fontFamily,
        double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                Theme.WeakTextBrush,
                pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    public void SetQueue(EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName)
    {
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
    }

    private double QueueStartTopMargin =>
        _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge);

    private int QueueSlotCount => Math.Max(1, _count + 1);

    private void MoveToTarget(bool animate)
    {
        if (_isClosingForReal)
        {
            return;
        }

        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry))
        {
            return;
        }

        var moveGeneration = ++_moveGeneration;
        animate = animate && _controller.State.EnableAnimations;
        var localArea = geometry.LocalWorkAreaDip;
        var requestedWidth = MasterDockedWidth(geometry.DpiScaleY);
        var targetTop = EdgeCapsuleLayout.TopForIndex(
            0,
            QueueStartTopMargin,
            localArea,
            QueueSlotCount);
        var currentTop = double.IsNaN(_currentTopDip) ? targetTop : _currentTopDip;

        ApplyMasterDeviceBounds(currentTop, requestedWidth, geometry);
        if (!animate || Math.Abs(currentTop - targetTop) < 0.5)
        {
            _animatedMonitorGeometry = null;
            BeginAnimation(AnimatedTopProperty, null);
            ApplyMasterDeviceBounds(targetTop, requestedWidth, geometry);
            return;
        }

        // One animation is one monitor-space transaction. A display/DPI refresh starts a new
        // generation; individual frames never re-enumerate screens or switch scale mid-flight.
        _animatedMonitorGeometry = geometry;
        _animatedWidthDip = requestedWidth;
        var topAnim = new DoubleAnimation
        {
            From = currentTop,
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(EdgeCapsuleLayout.SlotMoveMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        topAnim.Completed += (_, _) =>
        {
            if (moveGeneration != _moveGeneration)
            {
                return;
            }

            _animatedMonitorGeometry = null;
            BeginAnimation(AnimatedTopProperty, null);
            ApplyMasterDeviceBounds(targetTop, requestedWidth, geometry);
        };
        BeginAnimation(AnimatedTopProperty, topAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyMasterDeviceBounds(double topDip)
    {
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry))
        {
            return;
        }

        ApplyMasterDeviceBounds(topDip, MasterDockedWidth(geometry.DpiScaleY), geometry);
    }

    private void ApplyMasterDeviceBounds(
        double topDip,
        double widthDip,
        MonitorGeometry geometry)
    {
        if (_isClosingForReal)
        {
            return;
        }

        ApplyMasterEdgeLayout();
        var layout = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            geometry,
            _queueEdge,
            topDip,
            widthDip,
            0,
            PaperLayoutDefaults.CapsuleHeight));
        if (WindowNative.TrySetWindowDeviceBounds(this, layout.Bounds))
        {
            _currentTopDip = topDip;
        }
    }

    // Local target-monitor DIP. Real capsule hosts use the same coordinate space.
    public double AnchorTop => EdgeCapsuleLayout.TopForIndex(
        0,
        QueueStartTopMargin,
        EdgeCapsuleLayout.LocalWorkAreaForQueue(_queueMonitorDeviceName),
        QueueSlotCount);

    private static void OnAnimatedTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MasterCapsuleWindow w &&
            !w._isClosingForReal &&
            e.NewValue is double top &&
            !double.IsNaN(top) &&
            !double.IsInfinity(top))
        {
            if (w._animatedMonitorGeometry is MonitorGeometry geometry)
            {
                w.ApplyMasterDeviceBounds(top, w._animatedWidthDip, geometry);
            }
            else
            {
                w.ApplyMasterDeviceBounds(top);
            }
        }
    }
    // First-time show: position at the final edge-aligned spot BEFORE becoming visible,
    // then fade in. This avoids both the top-left flash and the slide-in from the wrong place.
    public void ShowPlaced(int count, bool active, bool animate)
    {
        _count = count;
        _active = active;
        ApplyStateVisuals();

        MoveToTarget(animate: false);
        Show();
        MoveToTarget(animate: false);
        RefreshEffectiveTopmost();

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public void CloseForReal()
    {
        if (_isClosingForReal)
        {
            return;
        }

        _isClosingForReal = true;
        FinishMasterGesture(commit: false, clearFocus: false);
        ++_moveGeneration;
        _animatedMonitorGeometry = null;
        BeginAnimation(AnimatedTopProperty, null);
        Close();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            _controller.ScheduleDisplayMetricsRefresh();
        }

        return IntPtr.Zero;
    }
}
