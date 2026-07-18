using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private EdgeCapsuleHost EnsureDeepCapsuleSlotHost()
    {
        if (_edgeCapsuleHost != null)
        {
            return _edgeCapsuleHost;
        }

        _edgeCapsuleHost = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin,
            CapsuleChromeCornerRadius,
            CapsuleInnerCornerRadius,
            DeepCapsuleSlotOutlineThickness,
            DeepCapsuleSlotOutlineOverlap,
            Math.Max(0, PaperLayoutDefaults.CapsuleHeight - WindowChromeInset),
            CapsuleLeftPadding,
            CapsuleIconGap,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper(),
            CapsuleLabelFontSize,
            CapsuleLabelFontWeight,
            Strings.Get("ToolTipHideThisPaper"),
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            CapsuleLabelFontFamily,
            AppTypography.SymbolFontFamily,
            AppTypography.Language,
            !_controller.SuppressTopmostForFullscreenForeground));
        var host = _edgeCapsuleHost;
        AttachDeepCapsuleSlotHostInput();
        host.AttachNativeHooks(
            OnDeepCapsuleSlotHostMessage,
            CloseDeepCapsuleSlotContextMenu);
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private IntPtr OnDeepCapsuleSlotHostMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // The slot host is not a paper window. In particular it must not inherit the paper's
        // snap/maximize WM_GETMINMAXINFO handling or minimum tracking size. It only participates
        // in display-metric refresh and then lets WPF process the native message normally.
        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            if (IsDeepCapsuleDockingReveal)
            {
                // Reveal is the only atomic cross-HWND boundary. Keep the already-confirmed pair
                // unchanged for these few frames; the controller replays fresh metrics on commit.
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
                return IntPtr.Zero;
            }
            // DPI hand-off, display removal and work-area changes can all rewrite a visible HWND
            // after its logical frame was committed. Invalidate both native and measured state so
            // an unchanged target is still replayed after WPF finishes processing this message.
            InvalidateEdgeCapsuleDisplayMetrics();
            if (IsDeepCapsuleReordering)
            {
                _controller.DeferDisplayMetricsRefreshUntilDeepCapsuleDragEnds();
            }
            else
            {
                _controller.ScheduleDisplayMetricsRefresh();
            }
        }

        return IntPtr.Zero;
    }

    private void AttachDeepCapsuleSlotHostInput()
    {
        if (_edgeCapsuleHost == null)
        {
            return;
        }
        _edgeCapsuleHost.AttachInput(new EdgeCapsuleHostCallbacks(
            InvalidateEdgeCapsulePointer,
            OnEdgeCapsulePointerPressed,
            OnEdgeCapsulePointerMoved,
            OnEdgeCapsulePointerReleased,
            OnEdgeCapsuleCaptureLost,
            OnEdgeCapsuleCloseInvoked));
        _edgeCapsuleHost.SetContextMenu(BuildDeepCapsuleSlotContextMenu());
        RefreshDeepCapsuleSlotLabel();
    }

    private void UpdateDeepCapsuleSlotHostTheme()
    {
        _edgeCapsuleHost?.UpdateTheme(
            PaperBrush,
            PaperBorderBrush,
            Theme.CapsuleFocusBorderBrush,
            HoverBrush,
            BrightWeakTextBrush,
            TextBrush,
            WeakTextBrush,
            CapsuleIconText(),
            CapsuleIconFontSizeForCurrentPaper());
    }

    public void UpdateEdgeCapsuleCloseButtonMode()
    {
        if (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        RequestEdgeCapsulePresentation(
            animate: false,
            EdgeCapsuleTransitionReason.State,
            refreshLayout: true);
    }

    private void RequestEdgeCapsulePresentation(
        bool animate,
        EdgeCapsuleTransitionReason reason,
        int durationMilliseconds = EdgeCapsuleLayout.SlotMoveMilliseconds,
        bool refreshLayout = false)
    {
        animate = animate && _controller.State.EnableAnimations;
        _edgeCapsule.RequestPresentation(animate
            ? EdgeCapsuleMotion.Animate(reason, durationMilliseconds)
            : EdgeCapsuleMotion.Snap(reason));
        var dirty = EdgeCapsuleDirty.Presentation;
        if (refreshLayout)
        {
            dirty |= EdgeCapsuleDirty.Measure;
        }
        InvalidateEdgeCapsule(dirty);
    }

    private void FlushEdgeCapsulePresentation(
        EdgeCapsuleTransitionReason reason,
        EdgeCapsuleDirty dirty = EdgeCapsuleDirty.Presentation)
    {
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Snap(reason));
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Flush(dirty, dispatcher, ReconcileEdgeCapsule);
    }

    internal void InvalidateEdgeCapsuleDisplayMetrics()
    {
        if (_edgeCapsuleHost == null && !HasDeepCapsuleSlotPlacement)
        {
            return;
        }

        _edgeCapsuleHost?.InvalidateNativeMetrics();
        _edgeCapsule.ForceApplyCurrentPresentation();
        _edgeCapsule.RequestPresentation(EdgeCapsuleMotion.Preserve(
            EdgeCapsuleTransitionReason.DisplayMetrics));
        InvalidateEdgeCapsule(
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure |
            EdgeCapsuleDirty.DisplayMetrics);
    }

    private EdgeCapsuleLayoutSnapshot CaptureEdgeCapsuleLayoutSnapshot()
    {
        var monitor = DeepCapsuleMonitorGeometry();
        var restingWidth = DeepCapsuleVisibleWidth(monitor.DpiScaleY);
        return EdgeCapsuleLayoutService.Calculate(new EdgeCapsuleLayoutFacts(
            monitor,
            MyDeepCapsuleEdge,
            _edgeCapsule.Placement,
            _controller.DeepCapsuleStartTopMarginFor(_paper),
            restingWidth,
            CapsuleCloseWidth,
            Math.Max(
                restingWidth + CapsuleCloseWidth,
                Math.Min(
                    EdgeCapsuleLayout.HostCapacityWidth,
                    monitor.LocalWorkAreaDip.Width)),
            PaperLayoutDefaults.CapsuleHeight,
            _controller.State.HideEdgeCapsuleCloseButtonOnHover));
    }

    private bool ApplyEdgeCapsulePresentationFrame(EdgeCapsulePresentationFrame frame)
    {
        if (!frame.Visible)
        {
            return _edgeCapsuleHost?.Apply(frame) ?? true;
        }

        EnsureDeepCapsuleSlotHost();
        return _edgeCapsuleHost?.Apply(frame) == true;
    }

    private DeviceScreenPoint? CaptureEdgeCapsulePointerPosition()
    {
        return WindowNative.TryGetCursorScreenPosition(out var pointer)
            ? pointer
            : null;
    }

    private void InvalidateEdgeCapsulePointer() =>
        InvalidateEdgeCapsule(EdgeCapsuleDirty.Pointer);

    private void ScheduleDeepCapsuleSlotMeasureRefresh()
    {
        if (_edgeCapsuleHost?.IsVisible == true && HasDeepCapsuleSlotPlacement)
        {
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Measure);
        }
    }

    private void InvalidateEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
        _edgeCapsule.Invalidate(dirty, dispatcher, ReconcileEdgeCapsule);
    }

    private EdgeCapsuleDirty ReconcileEdgeCapsule(EdgeCapsuleDirty dirty)
    {
        var wasRetracting = IsDeepCapsuleSlotRetracting;
        var remaining = _edgeCapsule.Reconcile(
            dirty,
            CaptureEdgeCapsuleLayoutSnapshot,
            CaptureEdgeCapsulePointerPosition,
            ApplyEdgeCapsulePresentationFrame);
        if (wasRetracting && !IsDeepCapsuleSlotRetracting)
        {
            UpdateDeepCapsuleSlotHostTheme();
            UpdateCapsuleClosePlacement();
        }
        return remaining;
    }

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        CancelDeepCapsuleReorderDrag();
        CloseDeepCapsuleSlotContextMenu();
        _edgeCapsule.Reset();
        _edgeCapsuleHost?.Dispose();
        _edgeCapsuleHost = null;
    }

    // ── This window's OWN queue identity. A queue is (monitor, edge); each docked capsule
    // resolves its geometry against its own queue, not a single global anchor. This is what
    // lets one capsule sit on the left edge of monitor A while another sits on the right of B.
    private EdgeCapsuleEdge MyDeepCapsuleEdge =>
        _paper.CapsuleSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;

    private bool MyDeepCapsuleIsLeftEdge => MyDeepCapsuleEdge == EdgeCapsuleEdge.Left;

    private Rect DeepCapsuleWorkArea()
    {
        return EdgeCapsuleLayout.WorkAreaForQueue(_paper.CapsuleMonitorDeviceName);
    }

    private MonitorGeometry DeepCapsuleMonitorGeometry()
    {
        if (_edgeCapsuleHost?.TryGetMonitorGeometry(
                _paper.CapsuleMonitorDeviceName,
                out var geometry) == true ||
            _edgeCapsuleHost == null && WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _paper.CapsuleMonitorDeviceName,
                out geometry))
        {
            return geometry;
        }

        var dpi = _edgeCapsuleHost?.Dpi ?? VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        return new MonitorGeometry(
            "",
            new DeviceScreenRect(
                (int)Math.Round(area.Left * dpi.DpiScaleX),
                (int)Math.Round(area.Top * dpi.DpiScaleY),
                (int)Math.Round(area.Right * dpi.DpiScaleX),
                (int)Math.Round(area.Bottom * dpi.DpiScaleY)),
            Math.Max(1, dpi.DpiScaleX),
            Math.Max(1, dpi.DpiScaleY));
    }

    private DpiScale DeepCapsuleSlotDpi()
    {
        var geometry = DeepCapsuleMonitorGeometry();
        return new DpiScale(geometry.DpiScaleX, geometry.DpiScaleY);
    }

    private double MyTopForIndex(int index, int slotCount)
    {
        return EdgeCapsuleLayoutService.TopForVisualIndex(
            DeepCapsuleMonitorGeometry(),
            index,
            slotCount,
            _controller.DeepCapsuleStartTopMarginFor(_paper));
    }

}
