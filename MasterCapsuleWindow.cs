using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
// peek/slide animation that mirrors the real capsules; the controller drives the
// retract/release of the real capsule windows.
public sealed class MasterCapsuleWindow : Window
{
    private const double ShellHeight = 30;

    // Compact internal metrics controlling how tightly the glyph + label sit inside the pill.
    // The label is always shown in full; only the right padding is tucked past the screen edge.
    private const double WindowChromeMargin = DeepCapsuleLayout.WindowChromeMargin;
    private const double WindowChromeInset = WindowChromeMargin * 2;
    private const double MasterLeftPadding = 5;
    private const double MasterGlyphGap = 4;
    private const double MasterRightPadding = 10;
    private const double MasterGlyphFontSize = 12;
    private const double MasterLabelFontSize = 11;
    // Reserve a couple of device-independent pixels so text anti-aliasing is not clipped
    // when the visible width is rounded to the screen edge.
    private const double MasterTextPixelReserve = 2;

    private readonly AppController _controller;

    // Which queue this master serves: (monitor device, edge). Each docked-capsule queue has its
    // own master pill at slot 0 of that queue. Geometry resolves against this queue's monitor+edge.
    private DeepCapsuleEdge _queueEdge;
    private string _queueMonitorDeviceName = "";

    private Border _pill = null!;
    private Border _hoverOverlay = null!;
    private TextBlock _glyph = null!;
    private TextBlock _label = null!;
    private StackPanel _contentStack = null!;
    private TranslateTransform _pillOffset = null!;

    private bool _isHovering;
    private bool _suppressGeometrySave = true; // master capsule position is always derived, never persisted
    private int _count;
    private bool _active;
    private bool _isPointerDown;
    private bool _isDraggingMaster;
    // The master pill is dragged vertically only: it slides its queue's stack by driving the
    // shared start-top margin. It never detaches or changes edge/monitor — that is done by
    // dragging an individual side capsule to another edge / screen.
    private double _dragStartTopMargin;
    private Point _dragStartScreenPos;
    private DeepCapsuleEdge? _appliedEdge;

    private static readonly DependencyProperty AnimatedLeftProperty =
        DependencyProperty.Register(
            nameof(AnimatedLeft),
            typeof(double),
            typeof(MasterCapsuleWindow),
            new PropertyMetadata(double.NaN, OnAnimatedLeftChanged));

    private double AnimatedLeft
    {
        get => (double)GetValue(AnimatedLeftProperty);
        set => SetValue(AnimatedLeftProperty, value);
    }

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

    public MasterCapsuleWindow(AppController controller, DeepCapsuleEdge queueEdge, string queueMonitorDeviceName)
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
        SourceInitialized += (_, _) => WindowNative.ApplyNoActivateStyle(this);
    }

    private void ConfigureWindow()
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Width = PaperLayoutDefaults.CapsuleWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
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
        var host = new Grid { Background = Brushes.Transparent, ClipToBounds = true };

        _pillOffset = new TranslateTransform();
        _pill = new Border
        {
            Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin),
            CornerRadius = new CornerRadius(DeepCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            RenderTransform = _pillOffset
        };

        // The pill background stays opaque (PaperBrush) at all times. Hover tint is a separate
        // overlay layered on top — the same shape as the pill — so the (semi-transparent)
        // HoverBrush never replaces the only opaque layer and let the desktop show through.
        var content = new Grid();

        _hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(DeepCapsuleLayout.CornerRadius),
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
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_glyph);

        _label = new TextBlock
        {
            Text = Strings.Get("CapsuleCollapseAllLabel"),
            Foreground = Theme.WeakTextBrush,
            FontSize = 11,
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
            _isPointerDown = true;
            _isDraggingMaster = false;
            _dragStartScreenPos = PointToScreen(e.GetPosition(this));
            _dragStartTopMargin = _controller.State.DeepCapsuleStartTopMargin;
            _pill.CaptureMouse();
            e.Handled = true;
        };
        _pill.PreviewMouseMove += (_, e) =>
        {
            if (!_isPointerDown || _pill.IsMouseCaptured != true || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentScreenPos = PointToScreen(e.GetPosition(this));
            var deltaX = currentScreenPos.X - _dragStartScreenPos.X;
            var deltaY = currentScreenPos.Y - _dragStartScreenPos.Y;
            if (!_isDraggingMaster &&
                Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (!_isDraggingMaster)
            {
                _isDraggingMaster = true;
                BeginAnimation(AnimatedLeftProperty, null);
                BeginAnimation(AnimatedTopProperty, null);
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var dpiScaleY = Math.Max(0.1, dpi.DpiScaleY);

            // The master stays pinned to its queue's edge; vertical drag slides that queue's stack
            // by driving the shared start-top margin. It never detaches or changes edge/monitor —
            // moving capsules between queues is done by dragging an individual side capsule.
            var targetMargin = _dragStartTopMargin + deltaY / dpiScaleY;
            _controller.SetDeepCapsuleStartTopMargin(targetMargin);

            e.Handled = true;
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var wasDragging = _isDraggingMaster;
            EndMasterDrag();
            if (wasDragging)
            {
                // Vertical slide only: the live margin is already applied; persist it.
                _controller.SetDeepCapsuleStartTopMargin(_controller.State.DeepCapsuleStartTopMargin, commit: true);
            }
            else
            {
                _controller.ToggleCapsuleCollapseAllActive();
            }

            e.Handled = true;
        };
        _pill.LostMouseCapture += (_, _) =>
        {
            // A capture lost mid-drag (e.g. Alt-Tab) snaps the stack back to its current anchor.
            if (_isDraggingMaster)
            {
                _controller.ArrangeDeepCapsules(animate: true);
            }

            EndMasterDrag();
        };
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

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
    }

    public void RefreshEffectiveTopmost()
    {
        var topmost = !_controller.SuppressTopmostForFullscreenForeground;
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

        ApplyDockedWidth(MasterVisibleWidth());

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

    private void EndMasterDrag()
    {
        _isPointerDown = false;
        _isDraggingMaster = false;
        if (_pill.IsMouseCaptured)
        {
            _pill.ReleaseMouseCapture();
        }
    }

    private double CapsuleWindowWidth()
    {
        // glyph + gap + label + left/right paddings + chrome margins. Both pieces are
        // measured the same way so the pill hugs the actual rendered content.
        var glyphWidth = MeasureText(_glyph.Text, MasterGlyphFontSize, FontWeights.SemiBold);
        var textWidth = MeasureText(_label.Text, MasterLabelFontSize, FontWeights.Normal);
        var shellWidth = Math.Ceiling(MasterLeftPadding + glyphWidth + MasterGlyphGap + textWidth + MasterRightPadding);
        return shellWidth + WindowChromeInset;
    }

    private double MasterVisibleWidth()
    {
        var peekLabel = FirstTextElement(Strings.Get("CapsuleCollapseAllLabel"));
        var visibleWidth = WindowChromeMargin + MasterLeftPadding
            + Math.Max(
                MeasureText("▾", MasterGlyphFontSize, FontWeights.SemiBold),
                MeasureText("▸", MasterGlyphFontSize, FontWeights.SemiBold))
            + MasterGlyphGap
            + MeasureText(peekLabel, MasterLabelFontSize, FontWeights.Normal)
            + MasterRightPadding
            + MasterTextPixelReserve;
        return Math.Clamp(visibleWidth, 1, CapsuleWindowWidth());
    }

    private static string FirstTextElement(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        return enumerator.MoveNext() ? (string)enumerator.Current : string.Empty;
    }

    private void ApplyDockedWidth(double visibleWidth)
    {
        ApplyMasterEdgeLayout();
        var fullWidth = CapsuleWindowWidth();
        visibleWidth = Math.Clamp(visibleWidth, 1, fullWidth);
        Width = visibleWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
        _pill.Width = Math.Max(0, fullWidth - WindowChromeInset);
        _pillOffset.X = 0;
    }

    // Mirror the pill chrome + content to the anchored edge. Right edge (default): chrome margin
    // on the left, pill+content hug the left, chevron leads. Left edge: chrome margin on the
    // right, pill+content hug the right (tail tucked past the left wall), chevron on the interior.
    private void ApplyMasterEdgeLayout()
    {
        var edge = _queueEdge;
        if (_appliedEdge == edge)
        {
            return;
        }

        _appliedEdge = edge;
        var leftEdge = edge == DeepCapsuleEdge.Left;

        _pill.Margin = leftEdge
            ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
            : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
        _pill.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        _contentStack.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _contentStack.Margin = leftEdge
            ? new Thickness(MasterRightPadding, 0, MasterLeftPadding, 0)
            : new Thickness(MasterLeftPadding, 0, MasterRightPadding, 0);

        // Keep the chevron on the interior side of the pill: leftmost on the right dock,
        // rightmost on the left dock.
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

    private double MeasureText(string text, double fontSize, FontWeight weight)
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
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                Theme.WeakTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    // Update which queue this master serves (e.g. its monitor was unplugged and it re-homes).
    public void SetQueue(DeepCapsuleEdge queueEdge, string queueMonitorDeviceName)
    {
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
    }

    private Rect QueueWorkArea => DeepCapsuleLayout.WorkAreaForQueue(_queueMonitorDeviceName);
    private bool QueueIsLeftEdge => _queueEdge == DeepCapsuleEdge.Left;
    private double QueueStartTopMargin => _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge);

    private void MoveToTarget(bool animate)
    {
        var area = QueueWorkArea;
        var visibleWidth = MasterVisibleWidth();
        var targetLeft = RoundX(DeepCapsuleLayout.DockedLeft(area, visibleWidth, _queueEdge));
        var targetTop = RoundY(DeepCapsuleLayout.TopForIndex(0, QueueStartTopMargin, area));
        var currentLeft = double.IsNaN(Left) || double.IsInfinity(Left) ? targetLeft : RoundX(Left);
        var currentTop = double.IsNaN(Top) || double.IsInfinity(Top) ? targetTop : RoundY(Top);

        MoveWithoutSave(() =>
        {
            ApplyDockedWidth(visibleWidth);
            if (!animate)
            {
                Left = targetLeft;
                Top = targetTop;
            }
        });

        if (!animate)
        {
            BeginAnimation(AnimatedLeftProperty, null);
            BeginAnimation(AnimatedTopProperty, null);
            return;
        }

        if (Math.Abs(currentLeft - targetLeft) < 0.5)
        {
            BeginAnimation(AnimatedLeftProperty, null);
            MoveWithoutSave(() => Left = targetLeft);
        }
        else
        {
            var leftAnim = new DoubleAnimation
            {
                From = currentLeft,
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(_isHovering ? DeepCapsuleLayout.SlideOutMilliseconds : DeepCapsuleLayout.SlideInMilliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            leftAnim.Completed += (_, _) =>
            {
                BeginAnimation(AnimatedLeftProperty, null);
                MoveWithoutSave(() => Left = targetLeft);
            };
            BeginAnimation(AnimatedLeftProperty, leftAnim, HandoffBehavior.SnapshotAndReplace);
        }

        if (Math.Abs(currentTop - targetTop) < 0.5)
        {
            BeginAnimation(AnimatedTopProperty, null);
            MoveWithoutSave(() => Top = targetTop);
        }
        else
        {
            var topAnim = new DoubleAnimation
            {
                From = currentTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(DeepCapsuleLayout.SlotMoveMilliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            topAnim.Completed += (_, _) =>
            {
                BeginAnimation(AnimatedTopProperty, null);
                MoveWithoutSave(() => Top = targetTop);
            };
            BeginAnimation(AnimatedTopProperty, topAnim, HandoffBehavior.SnapshotAndReplace);
        }
    }

    // The resting Top of the master, used as the retract/release anchor for real capsules.
    public double AnchorTop => RoundY(DeepCapsuleLayout.TopForIndex(0, QueueStartTopMargin, QueueWorkArea));

    private static void OnAnimatedLeftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MasterCapsuleWindow w || e.NewValue is not double left || double.IsNaN(left) || double.IsInfinity(left))
        {
            return;
        }

        w.MoveWithoutSave(() => w.Left = w.RoundX(left));
    }

    private static void OnAnimatedTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MasterCapsuleWindow w || e.NewValue is not double top || double.IsNaN(top) || double.IsInfinity(top))
        {
            return;
        }

        w.MoveWithoutSave(() => w.Top = w.RoundY(top));
    }

    private void MoveWithoutSave(Action move)
    {
        var was = _suppressGeometrySave;
        _suppressGeometrySave = true;
        try
        {
            move();
        }
        finally
        {
            _suppressGeometrySave = was;
        }
    }

    private double RoundX(double value)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Round(value, scale);
    }

    private double RoundY(double value)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        return Round(value, scale);
    }

    private static double Round(double value, double scale)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || scale <= 0)
        {
            return value;
        }

        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    // First-time show: position at the final edge-aligned spot BEFORE becoming visible,
    // then fade in. This avoids both the top-left flash and the slide-in from the wrong place.
    public void ShowPlaced(int count, bool active)
    {
        _count = count;
        _active = active;
        ApplyStateVisuals();

        ApplyDockedWidth(MasterVisibleWidth());
        MoveToTarget(animate: false);

        Show();
        RefreshEffectiveTopmost();

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
        BeginAnimation(AnimatedLeftProperty, null);
        BeginAnimation(AnimatedTopProperty, null);
        Close();
    }
}
