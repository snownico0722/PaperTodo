using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

// Standalone "collapse-all" master capsule. While docked it owns slot 0 (real capsules shift
// down to slot 1..N). Clicking toggles queue retraction; vertical drag moves the queue anchor.
// A horizontal pull hands only the floating visual to the shared EdgeCapsuleDragWindow while the
// controller owns the queue transfer. The master never becomes a Paper/presenter authority.
public sealed partial class MasterCapsuleWindow : Window
{
    private enum MasterGestureState
    {
        Idle,
        Pending,
        Dragging,
        QueueTransfer
    }

    private sealed record MasterDragSession(
        DeviceScreenPoint StartScreenPosition,
        string SourceQueueKey,
        bool SourceHadStartTopMargin,
        double StartTopMargin,
        FreeMasterDragLayout? FreeLayout = null);

    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    // Compact internal metrics controlling how tightly the glyph + stable count sit inside the pill.
    // The master owns exactly the width it renders; no full pill is hidden outside its HWND.
    private const double WindowChromeMargin = EdgeCapsuleLayout.WindowChromeMargin;
    private const double MasterLeftPadding = 4;
    private const double MasterGlyphGap = 3;
    private const double MasterRightPadding = 3;
    private const double MasterInteriorBorderThickness = 1;
    private const string MasterTwoDigitCountSample = "88";
    private const double MasterQueueTransferUnlockDistance = EdgeCapsuleLayout.CrossQueueDragUnlockDistance;

    private readonly AppController _controller;
    private readonly DeepCapsuleContextMenuSession _contextMenuSession;
    private double MasterGlyphFontSize => AppTypography.Scale(12);
    private double MasterLabelFontSize => VisualTextSizes.FontSize(12, _controller.State.CapsuleTextSize);
    private FontFamily MasterLabelFontFamily =>
        AppTypography.FontFamilyFor(content: false, bold: _controller.State.CapsuleTextBold);

    private FontWeight MasterLabelFontWeight =>
        AppTypography.FontWeightFor(_controller.State.CapsuleTextBold);

    // Which queue this master serves: (monitor device, edge). Each docked-capsule queue has its
    // own master pill at slot 0 of that queue. Geometry resolves against this queue's monitor+edge.
    private EdgeCapsuleEdge _queueEdge;
    private string _queueMonitorDeviceName = "";
    private string _queueKey = "";

    private Border _pill = null!;
    private Border _hoverOverlay = null!;
    private TextBlock _glyph = null!;
    private TextBlock _label = null!;
    private StackPanel _contentStack = null!;

    private bool _isHovering;
    private bool _experimentalPassive;
    private int _count;
    private bool _active;
    private MasterGestureState _gestureState;
    private double _currentTopDip = double.NaN;
    private int _moveGeneration;
    private bool _isClosingForReal;
    // Small vertical movement keeps adjusting the queue anchor. Pulling horizontally beyond the
    // ordinary cross-queue threshold switches to a queue transfer: members retract temporarily and
    // this master is represented by the shared FloatingFree drag HWND until the drop commits.
    private MasterDragSession? _dragSession;
    private EdgeCapsuleDragWindow? _floatingDragHost;
    private IntPtr _floatingFullscreenAvoidanceWindow;
    private MasterCapsuleQueueTransferSnapshot? _queueTransferSnapshot;
    private bool _queueTransferCanceled;

    public MasterCapsuleWindow(AppController controller, EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName,
        string? queueKey = null)
    {
        _controller = controller;
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
        _queueKey = queueKey ?? controller.MasterCapsuleQueueKey(_queueMonitorDeviceName, _queueEdge);
        _contextMenuSession = new DeepCapsuleContextMenuSession(
            controller,
            $"master:{Guid.NewGuid():N}");
        ConfigureWindow();
        BuildContent();
        UpdateExperimentalOpacity();
        // Ordinary pill interaction must never pull foreground focus: activating this window
        // would deactivate whatever app was in front, forcing it to repaint — the click "flash".
        // WS_EX_NOACTIVATE keeps the pill passive; an explicitly opened context-menu popup owns
        // its own short-lived foreground/focus session instead.
        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (_experimentalPassive)
            {
                WindowNative.SetInputPassthrough(this, enabled: true);
                WindowNative.ApplyBottomZOrder(this);
            }
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
        AppTypography.ApplyTextRendering(this);
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

        _pill = new SkinBorder
        {
            IsCapsule = true,
            Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin),
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = SkinBorder.CreateShadow(4, 0, 0.1)
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
            Text = "0",
            Foreground = Theme.WeakTextBrush,
            FontFamily = MasterLabelFontFamily,
            FontSize = MasterLabelFontSize,
            FontWeight = MasterLabelFontWeight,
            Margin = new Thickness(MasterGlyphGap, 0, 0, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AppTypography.ApplyTextRendering(_label);
        stack.Children.Add(_label);
        content.Children.Add(stack);

        _pill.Child = content;
        // Same chrome as the tray menu. The NOACTIVATE host delegates popup activation
        // and stale-focus cleanup to DeepCapsuleContextMenuSession.
        var contextMenu = _controller.CreateTrayMenu(registerForLiveRefresh: true);
        DeepCapsuleContextMenuSession.PrepareOwnerActivation((MaterialContextMenu)contextMenu);
        _pill.ContextMenu = contextMenu;
        _pill.ContextMenuOpening += (_, _) => _controller.RebuildTrayMenu(contextMenu);
        contextMenu.Opened += (_, _) => _contextMenuSession.HandleOpened(contextMenu);
        contextMenu.Closed += (_, _) => _contextMenuSession.HandleClosed(contextMenu);
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
            _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
            _dragSession = new MasterDragSession(
                DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this))),
                _queueKey,
                _controller.HasDeepCapsuleStartTopMarginForQueue(_queueKey),
                _controller.MasterCapsuleStartTopMargin(_queueKey),
                _controller.CaptureFreeMasterDragLayout(_queueMonitorDeviceName, _queueEdge, _queueKey));
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

            if (ContinueMasterDrag(
                    DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this))),
                    session))
            {
                e.Handled = true;
            }
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var hadGesture =
                _gestureState != MasterGestureState.Idle &&
                _dragSession != null;
            var wasDragging = FinishMasterGesture(commit: true, clearFocus: false,
                releasePointer: DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this))));
            if (hadGesture && !wasDragging)
            {
                _controller.SuppressEdgeCapsulePreviewForMasterQueueLayout(_queueKey);
                _controller.ToggleCapsuleCollapseAllActive(_queueKey);
            }

            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };
        _pill.LostMouseCapture += (_, _) =>
        {
            if (_gestureState != MasterGestureState.QueueTransfer)
            {
                FinishMasterGesture(commit: false);
            }
        };
        _pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
        };
    }

    private bool ContinueMasterDrag(DeviceScreenPoint currentScreenPos, MasterDragSession session)
    {
        if (EdgeCapsuleDragWindow.HasActiveLease)
        {
            FinishMasterGesture(commit: false);
            return true;
        }

        // A display change can invalidate the pressed queue before the delayed layout refresh
        // retires its master. Never apply that gesture's source margin to the fallback queue.
        if (!string.Equals(session.SourceQueueKey, _queueKey, StringComparison.Ordinal) ||
            // FreeLayout captured the LIVE edge at pointer-down, which may already
            // be the primary fallback. Keep the persistent identity separate so a
            // new post-disconnect gesture works, while an older gesture is revoked.
            !string.Equals(session.FreeLayout?.EdgeKey ?? EdgeCapsuleQueueIds.StripInstance(session.SourceQueueKey),
                _controller.MasterCapsuleQueueKey(_queueMonitorDeviceName, _queueEdge),
                StringComparison.Ordinal))
        {
            FinishMasterGesture(commit: false);
            return true;
        }

        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(_queueMonitorDeviceName, this, out var geometry))
        {
            return false;
        }

        var deltaX = (currentScreenPos.X - session.StartScreenPosition.X) / geometry.DpiScaleX;
        var deltaY = (currentScreenPos.Y - session.StartScreenPosition.Y) / geometry.DpiScaleY;
        if (_gestureState == MasterGestureState.Pending &&
            Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        if (Math.Abs(deltaX) >= MasterQueueTransferUnlockDistance)
        {
            if (TryRunQueueTransfer(currentScreenPos, session))
            {
                return true;
            }
        }

        if (_gestureState == MasterGestureState.Pending)
        {
            _gestureState = MasterGestureState.Dragging;
            ++_moveGeneration;
        }

        var targetMargin = (session.FreeLayout?.MasterTopDip ?? session.StartTopMargin) + deltaY;
        if (session.FreeLayout is { } freeLayout)
        {
            _controller.PreviewFreeMasterQueueTop(freeLayout, targetMargin, currentScreenPos);
            return true;
        }
        _controller.SetDeepCapsuleStartTopMargin(
            _queueMonitorDeviceName,
            _queueEdge,
            targetMargin);
        return true;
    }

    public void UpdateTheme()
    {
        // The pill background stays opaque; hover is rendered by the overlay.
        SkinBorder.Refresh(_pill);
        _pill.Effect = SkinBorder.CreateShadow(4, 0, 0.1);
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
        AppTypography.ApplyTextRendering(this);
        _glyph.FontFamily = AppTypography.SymbolFontFamily;
        _glyph.FontSize = MasterGlyphFontSize;
        _label.FontFamily = MasterLabelFontFamily;
        _label.FontSize = MasterLabelFontSize;
        _label.FontWeight = MasterLabelFontWeight;
        AppTypography.ApplyTextRendering(_label);
        MoveToTarget(animate: false);
    }

    public void UpdateExperimentalOpacity()
    {
        if (_controller.AreAdvancedMasterCapsulesTransparent)
        {
            _pill.Opacity = _controller.AdvancedShortcutOpacity;
            return;
        }

        var enabled =
            _controller.State.ExperimentalRestingCapsuleOpacity &&
            _controller.State.ExperimentalRestingCapsuleOpacityIncludesMaster;
        var restingOpacity = enabled
            ? ExperimentalOpacityLevels.Normalize(
                _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                ExperimentalOpacityLevels.DefaultRestingCapsule)
            : 1.0;
        var interactive = _isHovering || _gestureState != MasterGestureState.Idle;
        _pill.Opacity = enabled &&
            (_controller.State.ExperimentalRestingCapsuleOpacityAlways || !interactive)
                ? restingOpacity
                : 1.0;
    }

    public void RefreshEffectiveTopmost()
    {
        var avoidanceWindow = _controller.FullscreenAvoidanceWindowForQueue(
            _queueMonitorDeviceName);
        var topmost = !_experimentalPassive &&
            !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
            avoidanceWindow == IntPtr.Zero &&
            !_controller.SuppressDeepCapsuleTopmostForContextMenu;
        if (Topmost != topmost)
            _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        Topmost = topmost;
        if (IsVisible)
        {
            if (_experimentalPassive)
            {
                WindowNative.ApplyBottomZOrder(this);
            }
            else
            {
                WindowNative.ApplyTopmostZOrder(this, topmost, avoidanceWindow);
            }
        }

        RefreshFloatingDragTopmost();
    }

    private void RefreshFloatingDragTopmost()
    {
        if (_floatingDragHost is not { } floatingHost)
        {
            return;
        }

        var avoidanceWindow = _controller.FullscreenAvoidanceWindowFor(floatingHost);
        _floatingFullscreenAvoidanceWindow = avoidanceWindow;
        var topmost = !_controller.SuppressDeepCapsuleTopmostForContextMenu &&
            avoidanceWindow == IntPtr.Zero;
        floatingHost.Topmost = topmost;
        if (floatingHost.IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(floatingHost, topmost, avoidanceWindow);
        }
    }

    private void OnFloatingDragHostLocationChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _floatingDragHost)) return;
        if (_queueTransferSnapshot is { } snapshot &&
            WindowNative.TryGetCursorScreenPosition(out var pointer))
            _controller.PreviewFreeMasterQueueDropGhost(snapshot, pointer);
        if (_floatingFullscreenAvoidanceWindow !=
            _controller.FullscreenAvoidanceWindowFor(_floatingDragHost))
            RefreshFloatingDragTopmost();
    }

    private void OnFloatingDragHostUnexpectedlyClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _floatingDragHost))
        {
            ReleaseFloatingDragHostHandlers();
            CancelQueueTransfer();
        }
    }

    private void ReleaseFloatingDragHostHandlers()
    {
        if (_floatingDragHost is { } host)
        {
            host.LocationChanged -= OnFloatingDragHostLocationChanged;
            host.UnexpectedlyClosed -= OnFloatingDragHostUnexpectedlyClosed;
            _floatingDragHost = null;
        }
        _floatingFullscreenAvoidanceWindow = IntPtr.Zero;
    }

    internal void MarkQueueTransferCommitted(MasterCapsuleQueueTransferSnapshot snapshot)
    {
        if (_queueTransferSnapshot == snapshot)
        {
            // Business commit is complete. Closing an obsolete source master must not cancel
            // that commit or hide its floating cover; the caller still owns the lease until the
            // destination has been prepared and the shared handoff releases it.
            _queueTransferSnapshot = null;
        }
    }

    internal void CancelQueueTransfer()
    {
        if (_gestureState is MasterGestureState.Pending or MasterGestureState.Dragging)
            FinishMasterGesture(commit: false);
        if (_queueTransferSnapshot is not { } snapshot || _queueTransferCanceled)
        {
            return;
        }

        _queueTransferCanceled = true;
        // The native move loop can still be on the stack. Withdraw its visible surface now,
        // but keep the lease until that call returns so no later drag can rebind its HWND.
        _floatingDragHost?.Hide();
        _controller.CancelMasterCapsuleQueueTransfer(snapshot);
    }

    public void SetExperimentalPassive(bool enabled)
    {
        if (_isClosingForReal || _experimentalPassive == enabled)
        {
            return;
        }

        if (enabled)
        {
            CancelQueueTransfer();
            FinishMasterGesture(commit: false, clearFocus: true);
        }

        _experimentalPassive = enabled;
        WindowNative.SetInputPassthrough(this, enabled);
        RefreshEffectiveTopmost();
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
        _label.Visibility = _controller.State.CompactMasterCapsule
            ? Visibility.Collapsed
            : Visibility.Visible;
        _label.Text = _count.ToString(UiLanguages.EffectiveCulture);
        _pill.ToolTip = _active
            ? Strings.Get("CapsuleCollapseAllCollapsedTip")
            : Strings.Get("CapsuleCollapseAllExpandedTip");
    }

    private void SetHover(bool hovering)
    {
        // Hover only changes the pill background (handled in the MouseEnter/Leave handlers);
        // the master pill does not move, so there is nothing to reposition here.
        _isHovering = hovering;
        UpdateExperimentalOpacity();
    }

    private bool TryRunQueueTransfer(
        DeviceScreenPoint currentScreenPos,
        MasterDragSession session)
    {
        if (_isClosingForReal ||
            _experimentalPassive ||
            !_controller.TryCreateMasterQueueFloatingDragHostOptions(
                _queueMonitorDeviceName,
                _queueEdge,
                _glyph.Text,
                _label.Text,
                out var options,
                _queueKey) ||
            !_controller.TryBeginMasterCapsuleQueueTransfer(
                _queueMonitorDeviceName,
                _queueEdge,
                session.StartTopMargin,
                out var snapshot,
                session.SourceHadStartTopMargin,
                session.FreeLayout,
                _queueKey))
        {
            return false;
        }

        EdgeCapsuleDragWindow? floatingHost = null;
        MasterCapsuleWindow? handoffTarget = null;
        var committed = false;
        try
        {
            _queueTransferSnapshot = snapshot;
            _queueTransferCanceled = false;
            _gestureState = MasterGestureState.QueueTransfer;
            ++_moveGeneration;

            if (_pill.IsMouseCaptured)
            {
                _pill.ReleaseMouseCapture();
            }

            floatingHost = EdgeCapsuleDragWindow.Rent(options);
            _floatingDragHost = floatingHost;
            floatingHost.LocationChanged += OnFloatingDragHostLocationChanged;
            floatingHost.UnexpectedlyClosed += OnFloatingDragHostUnexpectedlyClosed;
            floatingHost.ShowWithEntrance(
                currentScreenPos,
                animate: false,
                scaleFrom: 1,
                durationMilliseconds: 0,
                alignToDragMessage: true);
            RefreshFloatingDragTopmost();
            if (_queueTransferCanceled || _isClosingForReal)
            {
                return true;
            }

            // The floating pill is now the only visible representative of this source queue.
            // Source members were retracted by the controller before this point.
            if (IsVisible)
            {
                Hide();
            }
            _controller.PreviewFreeMasterQueueDropGhost(snapshot, currentScreenPos);

            var outcome = floatingHost.PrepareAndRunNativeDrag(
                currentScreenPos,
                () => !_queueTransferCanceled &&
                    !_isClosingForReal &&
                    ReferenceEquals(floatingHost, _floatingDragHost));

            if (!_queueTransferCanceled &&
                !_isClosingForReal &&
                ReferenceEquals(floatingHost, _floatingDragHost) &&
                outcome.Result == EdgeCapsuleNativeDragResult.Completed)
            {
                _controller.PreviewFreeMasterQueueDropGhost(snapshot, outcome.DropPosition);
                committed = _controller.CommitMasterCapsuleQueueTransfer(
                    snapshot,
                    outcome.DropPosition,
                    out handoffTarget);
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Master capsule queue transfer failed: {0}",
                ex.Message);
            return true;
        }
        finally
        {
            _controller.EndFreeMasterQueueDropGhost();
            _queueTransferSnapshot = null;
            _gestureState = MasterGestureState.Idle;
            _dragSession = null;
            try
            {
                // Begin can synchronously retire this master before it receives the snapshot.
                // Every uncommitted exit must release the controller gate, even that early return.
                if (!committed)
                {
                    _controller.CancelMasterCapsuleQueueTransfer(snapshot);
                    if (!_isClosingForReal)
                    {
                        handoffTarget = this;
                    }
                }
            }
            finally
            {
                void ReleaseCover()
                {
                    try
                    {
                        ReleaseFloatingDragHostHandlers();
                    }
                    finally
                    {
                        floatingHost?.ReturnToPool();
                    }
                }

                // The detached master already uses the ordinary drag HWND. Keep using that same
                // HWND for the ordinary return flight instead of teleporting the target master
                // underneath it and immediately withdrawing the cover.
                if (floatingHost != null && handoffTarget != null)
                {
                    if (_queueTransferCanceled)
                    {
                        // Explicit cancellation already withdrew the floating cover. There is
                        // nothing visible to fly back, so restore the source authority immediately.
                        handoffTarget.PrepareQueueTransferHandoff();
                        floatingHost.CompleteHandoff(ReleaseCover);
                    }
                    else
                    {
                        BeginQueueTransferFloatingHandoff(
                            floatingHost,
                            handoffTarget,
                            ReleaseCover);
                    }
                }
                else
                {
                    ReleaseCover();
                }
            }

            ClearCapsuleInteractionKeyboardFocus();
            UpdateExperimentalOpacity();
        }
    }

    private bool FinishMasterGesture(bool commit, bool clearFocus = true,
        DeviceScreenPoint? releasePointer = null)
    {
        var session = _dragSession;
        var wasDragging = _gestureState == MasterGestureState.Dragging && session != null;
        if (wasDragging) _controller.DeferEdgeCapsulePreviewAfterDrag();
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
            // Only MouseUp commits that value. Every other exit restores the original margin
            // and its presence, so a canceled preview cannot pin the current product default.
            if (session!.FreeLayout is { } freeLayout)
            {
                // The same captured vertical gesture can MERGE by releasing over a
                // visible peer master. An independent drop commits its bounded anchor
                // and the previewed local spacing; neither needs a floating handoff.
                var merged = commit && releasePointer is { } point &&
                    _controller.TryCommitDockedFreeMasterQueueMerge(
                        freeLayout, point, session.StartTopMargin,
                        session.SourceHadStartTopMargin);
                if (!merged)
                    _controller.FinishFreeMasterQueueDrag(freeLayout, commit);
            }
            else if (commit)
            {
                _controller.SetDeepCapsuleStartTopMargin(
                    _queueMonitorDeviceName,
                    _queueEdge,
                    _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge),
                    commit: true);
            }
            else
            {
                _controller.RestoreMasterCapsuleQueueStartTopMargin(
                    session!.SourceQueueKey,
                    session.SourceHadStartTopMargin,
                    session.StartTopMargin);
            }
        }

        if (hadCapture && clearFocus)
        {
            ClearCapsuleInteractionKeyboardFocus();
        }

        UpdateExperimentalOpacity();
        return wasDragging;
    }

    private void ClearCapsuleInteractionKeyboardFocus() =>
        WindowNative.ClearCurrentThreadKeyboardFocus();

    private double MasterDockedWidth(double pixelsPerDip, bool updateLabel = true)
    {
        // The arrow slot stays stable in both modes. Normal mode also reserves a stable
        // two-digit count slot; compact mode removes that slot entirely.
        var glyphWidth = Math.Max(
            MeasureText("▾", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip),
            MeasureText("▸", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip));
        var countWidth = 0d;
        var countGap = 0d;
        if (_controller.State.CompactMasterCapsule)
        {
            if (updateLabel) _label.Width = double.NaN;
        }
        else
        {
            countWidth = MeasureText(
                MasterTwoDigitCountSample,
                MasterLabelFontSize,
                MasterLabelFontWeight,
                MasterLabelFontFamily,
                pixelsPerDip);
            countGap = MasterGlyphGap;
            if (updateLabel) _label.Width = countWidth;
        }

        var bodyWidth = Math.Ceiling(
            MasterLeftPadding +
            glyphWidth +
            countGap +
            countWidth +
            MasterRightPadding +
            MasterInteriorBorderThickness);
        return Math.Max(1, bodyWidth + WindowChromeMargin);
    }

    internal double DesiredDockedWidthFor(MonitorGeometry geometry) =>
        MasterDockedWidth(geometry.DpiScaleY, updateLabel: false);

    internal bool DragWindowIsTopmost => _floatingDragHost?.Topmost ?? Topmost;

    internal void BringDragWindowAboveGhost() =>
        WindowNative.BringToFrontNoActivate((Window?)_floatingDragHost ?? this);

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
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                Theme.WeakTextBrush,
                null,
                AppTypography.TextFormattingMode,
                pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    public void SetQueue(EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName, string? queueKey = null)
    {
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
        _queueKey = queueKey ?? _controller.MasterCapsuleQueueKey(_queueMonitorDeviceName, _queueEdge);
    }

    private double? _queueTopPreview;
    internal void SetQueueTopPreview(double? top) => _queueTopPreview = top;

    // Drag previews only change position. Keep count, theme and z-order updates
    // in the normal state/lifecycle path, and do not schedule an unchanged move.
    internal void PreviewQueueTop(double top, bool animate = false)
    {
        if (_queueTopPreview == top) return;
        _queueTopPreview = top;
        MoveToTarget(animate);
    }

    private double QueueStartTopMargin =>
        _queueTopPreview ?? _controller.MasterCapsuleStartTopMargin(_queueKey);

    private int QueueSlotCount => Math.Max(1, _count + 1);

    private double QueueTopForArea(Rect area) =>
        _controller.State.UseCapsuleFreeQueues
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(0, QueueStartTopMargin, area, _controller.DeepCapsuleGap)
            : EdgeCapsuleLayout.TopForIndex(0, QueueStartTopMargin, area, QueueSlotCount, _controller.DeepCapsuleGap);

    private void MoveToTarget(bool animate)
    {
        if (_isClosingForReal) return;
        var moveGeneration = ++_moveGeneration;
        if (animate && _controller.TryStageMasterQueueYield(this)) return;
        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        if (_controller.IsEdgeCapsuleQueueProxyRetainingSource(this)) return;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName, this, out var geometry))
        {
            ScheduleMasterSettle(moveGeneration);
            return;
        }

        ApplyMasterDeviceBounds(QueueTopForArea(geometry.LocalWorkAreaDip),
            MasterDockedWidth(geometry.DpiScaleY), geometry);
        ScheduleMasterSettle(moveGeneration);
    }

    private void ScheduleMasterSettle(int moveGeneration, int pass = 0)
    {
        // Windows can move an HWND again while completing WM_DPICHANGED/display removal. Resolve
        // the queue once more after WPF's layout work, guarded by the existing move generation.
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_isClosingForReal || moveGeneration != _moveGeneration ||
                    !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                        _queueMonitorDeviceName,
                        this,
                        out var geometry))
                {
                    return;
                }

                var targetTop = QueueTopForArea(geometry.LocalWorkAreaDip);
                ApplyMasterDeviceBounds(
                    targetTop,
                    MasterDockedWidth(geometry.DpiScaleY),
                    geometry);
                if (pass == 0)
                {
                    // Display removal can deliver a second WPF/native rewrite after the first idle
                    // turn. One guarded follow-up keeps slot 0 on the same settle depth as its queue.
                    ScheduleMasterSettle(moveGeneration, pass: 1);
                }
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
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
    public double AnchorTop => QueueTopForArea(EdgeCapsuleLayout.LocalWorkAreaForQueue(_queueMonitorDeviceName));

    // First-time show: position at the final edge-aligned spot BEFORE becoming visible,
    // then fade in. This avoids both the top-left flash and the slide-in from the wrong place.
    internal void PrepareQueueTransferTarget(int count, bool active)
    {
        // Queue-transfer targets stay entirely unpublished during the floating return flight.
        // Their final physical anchor is pure geometry and does not require an invisible HWND.
        _count = count;
        _active = active;
        ApplyStateVisuals();
    }

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
            Duration = TimeSpan.FromMilliseconds(AnimationTiming.ScaleMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    internal bool TryGetQueueTransferDockingTarget(
        out DeviceScreenRect targetBounds,
        out EdgeCapsuleEdge targetEdge)
    {
        targetBounds = default;
        targetEdge = _queueEdge;
        if (_isClosingForReal ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry))
        {
            return false;
        }

        var targetTop = QueueTopForArea(geometry.LocalWorkAreaDip);
        var docked = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            geometry,
            _queueEdge,
            targetTop,
            MasterDockedWidth(geometry.DpiScaleY),
            0,
            PaperLayoutDefaults.CapsuleHeight));

        // Ordinary capsule docking adds the missing wall-side chrome margin to the one-sided
        // docked HWND. Use the same physical anchor for the symmetric floating pill.
        targetBounds = EdgeCapsuleGeometry.FloatingHandoffBoundsForDockedBounds(
            docked.Bounds,
            _queueEdge,
            geometry.DpiScaleX,
            WindowChromeMargin);
        return !targetBounds.IsEmpty;
    }

    private void BeginQueueTransferFloatingHandoff(
        EdgeCapsuleDragWindow floatingHost,
        MasterCapsuleWindow target,
        Action releaseCover)
    {
        var targetKey = target._queueKey;
        var targetEdge = target._queueEdge;
        var hasTarget = target.TryGetQueueTransferDockingTarget(
            out var targetBounds,
            out _);

        void Reveal()
        {
            floatingHost.AnimateDockingReveal(
                _controller.State.EnableAnimations
                    ? AnimationTiming.ScaleMilliseconds(EdgeCapsuleLayout.DockingRevealMilliseconds)
                    : 1,
                _ => floatingHost.CompleteHandoff(releaseCover));
        }

        void CompleteFlight(bool reachedTarget)
        {
            var current = _controller.MasterCapsuleForQueue(targetKey);
            current?.PrepareQueueTransferHandoff();

            // Preparing the WPF surface pumps Render and can dispatch an arrange that replaces
            // the master or changes its geometry. Resolve once more after that boundary, then
            // publish synchronously so the cover uses the same current endpoint as the master.
            current = _controller.MasterCapsuleForQueue(targetKey);
            current?.PublishQueueTransferHandoff();
            if (current == null)
            {
                floatingHost.CompleteHandoff(releaseCover);
                return;
            }
            if (!reachedTarget ||
                !current.TryGetQueueTransferDockingTarget(out var currentBounds, out var currentEdge))
            {
                Reveal();
                return;
            }

            if (currentEdge == targetEdge &&
                EdgeCapsuleGeometry.DeviceBoundsMatch(currentBounds, targetBounds, tolerance: 0))
            {
                Reveal();
            }
            else
            {
                // An arrange during the flight is allowed. Align to its latest endpoint directly;
                // do not replay the flight or hold a second controller transaction open.
                floatingHost.AnimateDockingHandoff(currentBounds, currentEdge, 0, _ => Reveal());
            }
        }

        if (!hasTarget)
        {
            CompleteFlight(false);
            return;
        }
        floatingHost.AnimateDockingHandoff(
            targetBounds,
            targetEdge,
            _controller.State.EnableAnimations
                ? AnimationTiming.ScaleMilliseconds(EdgeCapsuleLayout.DockingHandoffMilliseconds)
                : 1,
            CompleteFlight);
    }

    internal void PrepareQueueTransferHandoff()
    {
        if (_isClosingForReal) return;

        PublishQueueTransferHandoff();
        // Call only after controller queue synchronization has finished: Render can reenter it.
        Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void PublishQueueTransferHandoff()
    {
        if (_isClosingForReal) return;

        // A drop/rollback swaps visible authority, so publish fully opaque under the floating cover.
        _controller.DeferEdgeCapsulePreviewAfterDrag();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        MoveToTarget(animate: false);
        if (!IsVisible) Show();
        RefreshEffectiveTopmost();
        UpdateLayout();
    }

    public void CloseForReal()
    {
        if (_isClosingForReal)
        {
            return;
        }

        _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        _isClosingForReal = true;
        CancelQueueTransfer();
        FinishMasterGesture(commit: false, clearFocus: false);
        _contextMenuSession.Dispose();
        ++_moveGeneration;
        Close();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest && _experimentalPassive)
        {
            handled = true;
            return HtTransparent;
        }

        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            _controller.ScheduleDisplayMetricsRefresh();
        }

        return IntPtr.Zero;
    }
}
