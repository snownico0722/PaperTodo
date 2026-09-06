using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;

namespace PaperTodo;

internal sealed record EdgeCapsuleHostOptions(
    double WindowChromeMargin,
    double ChromeCornerRadius,
    double InnerCornerRadius,
    double OutlineThickness,
    double OutlineOverlap,
    double BodyHeight,
    double LeftPadding,
    double IconGap,
    string IconText,
    double IconFontSize,
    double LabelFontSize,
    FontWeight LabelFontWeight,
    string CloseToolTip,
    Brush PaperBrush,
    Brush PaperBorderBrush,
    Brush OutlineBrush,
    Brush HoverBrush,
    Brush IconBrush,
    Brush StrongTextBrush,
    Brush TextBrush,
    FontFamily UiFontFamily,
    FontFamily SymbolFontFamily,
    XmlLanguage Language,
    bool Topmost,
    string DiagnosticId);

internal sealed record EdgeCapsulePreviewThemeResources(
    Brush LinkBrush,
    Brush CheckBoxBorderBrush,
    Brush CheckBoxActiveBrush,
    Brush CheckBoxUncheckedHoverBorderBrush,
    Brush CheckBoxUncheckedHoverBackgroundBrush,
    Brush CheckBoxActiveHoverBrush);

internal sealed record EdgeCapsuleHostCallbacks(
    Action<DeviceScreenPoint?> PointerInvalidated,
    Action<DeviceScreenPoint> PointerPressed,
    Func<DeviceScreenPoint, bool, bool> PointerMoved,
    Func<DeviceScreenPoint, bool> PointerReleased,
    Func<EdgeCapsuleCaptureLoss, EdgeCapsuleCaptureAction> CaptureLost,
    Action CloseInvoked);

/// <summary>
/// Owns the docked HWND and every visual belonging to it. PaperWindow supplies content and event
/// callbacks, but host lifetime can no longer be partially cleared across unrelated fields.
/// </summary>
internal sealed partial class EdgeCapsuleHost : IDisposable
{
    private const int WmMouseMove = 0x0200;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private readonly EdgeCapsuleHostOptions _options;
#if DEBUG
    private readonly long _diagnosticResourceId;
#endif
    private EdgeCapsuleHostCallbacks? _callbacks;
    private Brush _hoverBrush;
    private Brush _textBrush;
    private Brush _weakTextBrush;
    private double _maximumCloseWidth;
    private double _appliedCloseWidth;
    private EdgeCapsuleEdge? _appliedEdge;
    private EdgeCapsulePresentationFrame _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
    private EdgeCapsuleCaptureLossReason _contentCaptureLossReason;
    private int _nativeMetricsVersion;
    private int _appliedNativeMetricsVersion;
    private bool _zOrderStateInitialized;
    private bool _appliedTopmost;
    private IntPtr _appliedTopmostInsertAfter;
    private bool _experimentalPassive;
    private bool _interactionLocked;
    private bool _disposed;
    private Window Window { get; }
    private Grid Root { get; }
    private Grid VisualSurface { get; }
    private TranslateTransform VisualSurfaceOffset { get; }
    private Border Chrome { get; }
    private Border Outline { get; }
    private Grid Shell { get; }
    private Border ContentArea { get; }
    private Grid ContentHost { get; }
    private Grid ContentGrid { get; }
    private TextBlock Icon { get; }
    private Border CloseArea { get; }
    private TextBlock CloseGlyph { get; }
    private TextBlock Label { get; }

    private EdgeCapsuleHost(
        EdgeCapsuleHostOptions options,
        Window window,
        Grid root,
        Grid visualSurface,
        TranslateTransform visualSurfaceOffset,
        Border chrome,
        Border outline,
        Grid shell,
        Border contentArea,
        Grid contentHost,
        Grid contentGrid,
        TextBlock icon,
        Border closeArea,
        TextBlock closeGlyph,
        TextBlock label)
    {
        _options = options;
        _zOrderStateInitialized = true;
        _appliedTopmost = options.Topmost;
        _appliedTopmostInsertAfter = IntPtr.Zero;
#if DEBUG
        _diagnosticResourceId =
            EdgeCapsulePerformanceDiagnostics.RegisterTransparentHost(
                options.DiagnosticId);
#endif
        _hoverBrush = options.HoverBrush;
        _textBrush = options.StrongTextBrush;
        _weakTextBrush = options.TextBrush;
        Window = window;
        Root = root;
        VisualSurface = visualSurface;
        VisualSurfaceOffset = visualSurfaceOffset;
        Chrome = chrome;
        Outline = outline;
        Shell = shell;
        ContentArea = contentArea;
        ContentHost = contentHost;
        ContentGrid = contentGrid;
        Icon = icon;
        CloseArea = closeArea;
        CloseGlyph = closeGlyph;
        Label = label;
    }

    public bool IsVisible => !_disposed && Window.IsVisible;
    public Dispatcher Dispatcher => Window.Dispatcher;
    internal IntPtr Handle => _disposed
        ? IntPtr.Zero
        : new WindowInteropHelper(Window).Handle;

    public void AttachNativeHooks(HwndSourceHook hook, Action deactivated)
    {
        if (_disposed)
        {
            return;
        }

        var window = Window;
        var attached = false;
        void AttachToCurrentSource()
        {
            if (_disposed || attached)
            {
                return;
            }

            // SourceInitialized may run before PresentationSource.FromVisual(window) starts
            // returning the source. Resolve it from the already-created HWND as the authoritative
            // fallback; otherwise that one-shot event can pass without either native hook ever
            // being installed.
            var source = PresentationSource.FromVisual(window) as HwndSource;
            if (source == null)
            {
                var handle = new WindowInteropHelper(window).Handle;
                source = handle == IntPtr.Zero
                    ? null
                    : HwndSource.FromHwnd(handle);
            }
            if (source == null)
            {
                return;
            }

            WindowNative.ApplyNoActivateStyle(window);
            WindowNative.SetInputPassthrough(
                window,
                _interactionLocked || _experimentalPassive);
            if (_experimentalPassive)
            {
                WindowNative.ApplyBottomZOrder(window);
            }
            source.AddHook(OnNativeMessage);
            source.AddHook(hook);
            attached = true;
        }

        window.SourceInitialized += (_, _) => AttachToCurrentSource();
        window.Deactivated += (_, _) => deactivated();
        AttachToCurrentSource();
    }

    /// <summary>
    /// The only per-paper docked-surface effect entry. HostBounds is the real endpoint HWND and
    /// Bounds is its wall-aligned visual surface; HostBounds may be larger than Bounds and remains the stable capacity.
    /// </summary>
    public bool Apply(EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || !frame.IsUsable)
        {
            return false;
        }
        var window = Window;
        var root = Root;
        var nativeHostBounds = frame.HostBounds;
        var visualOffsetYDevice = frame.Bounds.Top - nativeHostBounds.Top;
#if DEBUG
        var applyStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        double nativeMilliseconds = 0;
        double propertyMilliseconds = 0;
        double previewMilliseconds = 0;
        double forcedLayoutMilliseconds = 0;
        double showMilliseconds = 0;
        double verifyMilliseconds = 0;
        var nativeSetRequested = false;
        void TraceApply(string outcome)
        {
            var totalMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    applyStartedAt);
            if (string.Equals(outcome, "success", StringComparison.Ordinal) &&
                totalMilliseconds < 0.75 &&
                forcedLayoutMilliseconds <= 0.001 &&
                !nativeSetRequested)
            {
                return;
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"host.apply paper={_options.DiagnosticId} outcome={outcome} " +
                $"totalMs={totalMilliseconds:F3} " +
                $"nativeMs={nativeMilliseconds:F3} propertiesMs={propertyMilliseconds:F3} " +
                $"previewMs={previewMilliseconds:F3} forcedLayoutMs={forcedLayoutMilliseconds:F3} " +
                $"showMs={showMilliseconds:F3} verifyMs={verifyMilliseconds:F3} " +
                $"surface={frame.Surface} visible={frame.Visible} " +
                $"bounds={frame.Bounds.Width}x{frame.Bounds.Height} " +
                $"nativeSet={nativeSetRequested} hostMode=bounded-live " +
                $"offsetYDevice={visualOffsetYDevice} " +
                $"nativeBounds={nativeHostBounds.Left},{nativeHostBounds.Top}," +
                $"{nativeHostBounds.Width}x{nativeHostBounds.Height}");
        }
#endif

        if (!frame.Visible)
        {
#if DEBUG
            var previousHostBounds = _appliedFrame.HostBounds;
#endif
            if (window.IsVisible)
            {
                window.Hide();
            }
            if (Math.Abs(window.Opacity - 1) > 0.001)
            {
                window.Opacity = 1;
            }
            if (Math.Abs(root.Opacity - 1) > 0.001)
            {
                root.Opacity = 1;
            }
            if (root.IsHitTestVisible)
            {
                root.IsHitTestVisible = false;
            }
            if (!DetachPreviewContent())
            {
                ResetForFreshApply();
#if DEBUG
                TraceApply("hide-detach-failed");
#endif
                return false;
            }
            _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.UpdateTransparentHost(
                _diagnosticResourceId,
                _options.DiagnosticId,
                previousHostBounds,
                shown: false,
                "hidden");
            TraceApply("hidden");
#endif
            return true;
        }

        Debug.Assert(
            frame.Surface != EdgeCapsuleSurfaceKind.FloatingFree,
            "FloatingFree is rendered by EdgeCapsuleDragWindow, never the docked host.");
        Debug.Assert(
            frame.Bounds.Width <= nativeHostBounds.Width &&
            frame.Bounds.Height <= nativeHostBounds.Height &&
            (frame.Edge == EdgeCapsuleEdge.Left
                ? frame.Bounds.Left == frame.WallDeviceX &&
                  nativeHostBounds.Left == frame.WallDeviceX
                : frame.Bounds.Right == frame.WallDeviceX &&
                  nativeHostBounds.Right == frame.WallDeviceX),
            "The visible capsule must fit inside its bounded, wall-pinned native host.");
        var previousFrame = _appliedFrame;
        var previousNativeHostBounds = previousFrame.HostBounds;
        var nativeMetricsVersion = _nativeMetricsVersion;
        var nativeMetricsChanged = _appliedNativeMetricsVersion != nativeMetricsVersion;
        var firstShow = !window.IsVisible;
        var nativeHostSizeChanged = firstShow ||
            !previousFrame.Visible ||
            previousNativeHostBounds.Width != nativeHostBounds.Width ||
            previousNativeHostBounds.Height != nativeHostBounds.Height;
        var refreshNativeLayout = firstShow ||
            nativeMetricsChanged ||
            !previousFrame.Visible ||
            nativeHostSizeChanged;
        var edgeChanged = firstShow ||
            !previousFrame.Visible ||
            previousFrame.Edge != frame.Edge;
        var visualSurfaceSizeChanged = nativeMetricsChanged ||
            edgeChanged ||
            previousFrame.Bounds.Width != frame.Bounds.Width ||
            previousFrame.Bounds.Height != frame.Bounds.Height ||
            Math.Abs(previousFrame.DpiScaleX - frame.DpiScaleX) > 0.001 ||
            Math.Abs(previousFrame.DpiScaleY - frame.DpiScaleY) > 0.001;
        var visualSurfaceOffsetChanged =
            previousNativeHostBounds.Top != nativeHostBounds.Top ||
            previousFrame.Bounds.Top - previousNativeHostBounds.Top != visualOffsetYDevice;
        var segmentLayoutChanged = visualSurfaceSizeChanged ||
            previousFrame.BodyWindowWidthDevice != frame.BodyWindowWidthDevice ||
            Math.Abs(previousFrame.MaximumCloseWidthDip - frame.MaximumCloseWidthDip) > 0.001;
        var closeSegmentModeChanged = firstShow ||
            !previousFrame.Visible ||
            previousFrame.CloseSegmentActsAsContent != frame.CloseSegmentActsAsContent;
        var nativeHandoff = previousFrame.Visible && (
            previousFrame.Edge != frame.Edge ||
            previousFrame.WallDeviceX != frame.WallDeviceX ||
            Math.Abs(previousFrame.DpiScaleX - frame.DpiScaleX) > 0.001 ||
            Math.Abs(previousFrame.DpiScaleY - frame.DpiScaleY) > 0.001);
#if DEBUG
        var nativeQueryStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var nativeBoundsChanged = firstShow ||
            !previousFrame.Visible ||
            previousNativeHostBounds != nativeHostBounds ||
            !WindowNative.TryGetWindowDeviceBounds(window, out var actualHostBounds) ||
            actualHostBounds != nativeHostBounds;
#if DEBUG
        nativeSetRequested = nativeBoundsChanged;
#endif
#if DEBUG
        nativeMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            nativeQueryStartedAt);
#endif

        if (firstShow || nativeHandoff)
        {
            // A monitor/edge/DPI transfer must never display the destination layout on the source
            // wall. Reveal it only after the native move and immediate metrics checks both succeed.
            window.Opacity = 0;
        }
#if DEBUG
        var nativeSetStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var nativeBoundsApplied = !nativeBoundsChanged ||
            WindowNative.TrySetWindowDeviceBounds(window, nativeHostBounds);
#if DEBUG
        nativeMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            nativeSetStartedAt);
#endif
        if (!nativeBoundsApplied)
        {
            ResetForFreshApply();
#if DEBUG
            TraceApply("native-set-failed");
#endif
            return false;
        }

        // Move the HWND before changing edge-specific columns and corners. If the native move is
        // rejected or superseded by a per-monitor DPI hand-off, the old monitor must never display
        // a visual tree that has already been flipped for the destination edge.
#if DEBUG
        var propertiesStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        if (edgeChanged)
        {
            ApplyFixedLayout(frame.Edge);
        }
        if (visualSurfaceSizeChanged || visualSurfaceOffsetChanged)
        {
            ApplyVisualSurface(frame);
        }
        if (closeSegmentModeChanged)
        {
            ApplyCloseSegmentMode(frame);
        }
        if (segmentLayoutChanged)
        {
            var closeWidth = EdgeCapsuleGeometry.CloseWidthForAppliedDeviceWidth(
                frame.Bounds.Width,
                frame.BodyWindowWidthDevice,
                frame.DpiScaleX,
                frame.MaximumCloseWidthDip);
            ApplySegmentWidths(
                frame,
                closeWidth,
                frame.MaximumCloseWidthDip,
                frame.IsHitTestVisible);
        }
        else if (previousFrame.IsHitTestVisible != frame.IsHitTestVisible ||
            closeSegmentModeChanged)
        {
            CloseArea.IsHitTestVisible = frame.IsHitTestVisible &&
                _maximumCloseWidth > 0 &&
                (frame.CloseSegmentActsAsContent
                    ? _appliedCloseWidth > 0
                    : _appliedCloseWidth >= _maximumCloseWidth - 0.5);
        }
#if DEBUG
        propertyMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            propertiesStartedAt);
#endif

#if DEBUG
        var previewStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var previewApplied = ApplyPreviewPresentation(frame);
#if DEBUG
        previewMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            previewStartedAt);
#endif
        if (!previewApplied)
        {
            ResetForFreshApply();
#if DEBUG
            TraceApply("preview-apply-failed");
#endif
            return false;
        }
        _appliedFrame = frame;
        if (firstShow)
        {
#if DEBUG
            var showStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            window.Show();
            if (!WindowNative.TrySetWindowDeviceBounds(window, nativeHostBounds))
            {
                // Show succeeded but the post-Show placement did not. Make the surface transparent
                // immediately so the half-committed edge layout never remains visible; the next
                // apply treats its frame as fresh and retries the full path.
                ResetForFreshApply();
#if DEBUG
                showMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    showStartedAt);
                TraceApply("post-show-native-set-failed");
#endif
                return false;
            }
#if DEBUG
            showMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                showStartedAt);
#endif
        }

        if (refreshNativeLayout)
        {
            // Width/Height can retain the same DIP values while the HWND changes DPI. Explicitly
            // invalidate the WPF tree so the real surface is arranged against the new client area.
#if DEBUG
            var layoutStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            RefreshNativeMetricsLayout();
#if DEBUG
            forcedLayoutMilliseconds +=
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    layoutStartedAt);
#endif
        }
#if DEBUG
        var verifyStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var nativePresentationMatches =
            WindowNative.TryGetWindowDeviceBounds(window, out var settledHostBounds) &&
            settledHostBounds == nativeHostBounds;
#if DEBUG
        verifyMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            verifyStartedAt);
#endif
        if (!nativePresentationMatches)
        {
            // Windows/WPF may supersede SetWindowPos during WM_DPICHANGED or display removal. Never
            // leave an edge-flipped visual tree visible on the old monitor; retry from a hidden host.
            ResetForFreshApply();
#if DEBUG
            TraceApply("native-verify-failed");
#endif
            return false;
        }
        if (_nativeMetricsVersion != nativeMetricsVersion)
        {
            // SetWindowPos can synchronously enter WM_DPICHANGED. Replay the frame with the new
            // generation instead of marking the old DPI layout as committed.
            ResetForFreshApply();
#if DEBUG
            TraceApply("dpi-generation-changed");
#endif
            return false;
        }

        var contentOpacity = Math.Clamp(frame.ContentOpacity, 0, 1);
        if (Math.Abs(root.Opacity - contentOpacity) > 0.001)
        {
            root.Opacity = contentOpacity;
        }
        if (root.IsHitTestVisible != frame.IsHitTestVisible)
        {
            root.IsHitTestVisible = frame.IsHitTestVisible;
        }
        var outlineVisibility = frame.OutlineVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (Outline.Visibility != outlineVisibility)
        {
            Outline.Visibility = outlineVisibility;
        }
        var opacity = Math.Clamp(frame.Opacity, 0, 1);
        if (Math.Abs(window.Opacity - opacity) > 0.001)
        {
            window.Opacity = opacity;
        }
        _appliedNativeMetricsVersion = nativeMetricsVersion;
        if (_experimentalPassive)
        {
            WindowNative.ApplyBottomZOrder(window);
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.UpdateTransparentHost(
            _diagnosticResourceId,
            _options.DiagnosticId,
            nativeHostBounds,
            window.IsVisible,
            firstShow
                ? "shown"
                : nativeBoundsChanged
                    ? "bounds-changed"
                    : "visibility-changed");
        TraceApply("success");
#endif
        return true;
    }

    public bool ConfirmPresentationSettled(EdgeCapsulePresentationFrame frame)
    {
        var settled = !_disposed &&
            frame.Visible &&
            Window.IsVisible &&
            _appliedFrame == frame &&
            _appliedNativeMetricsVersion == _nativeMetricsVersion &&
            MatchesNativePresentationLayout(frame);
        if (!settled && !_disposed)
        {
            // The floating cover is about to be released. Never expose an HWND whose outer bounds
            // or WPF client layout was rewritten after Apply; the debounced display pass restores it.
            ResetForFreshApply();
        }
        return settled;
    }

    internal bool MatchesPresentation(
        EdgeCapsulePresentationFrame frame)
    {
        if (_disposed || _appliedFrame != frame)
        {
            return false;
        }
        if (!frame.Visible)
        {
            return !Window.IsVisible;
        }
        return Window.IsVisible &&
            _appliedNativeMetricsVersion == _nativeMetricsVersion &&
            MatchesNativePresentationLayout(frame);
    }

    internal bool MatchesQueueTranslationSurface(
        EdgeCapsulePresentationFrame expected)
    {
        if (_disposed ||
            !Window.IsVisible ||
            _appliedNativeMetricsVersion != _nativeMetricsVersion ||
            !EdgeCapsuleQueueProxyPolicy.HasStableLiveSurfaceIdentity(
                _appliedFrame,
                expected) ||
            !WindowNative.TryGetWindowDeviceBounds(
                Window,
                out var actualBounds) ||
            actualBounds != expected.HostBounds)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(Window);
        return Math.Abs(dpi.DpiScaleX - expected.DpiScaleX) < 0.001 &&
            Math.Abs(dpi.DpiScaleY - expected.DpiScaleY) < 0.001;
    }

    internal bool PrepareCompositionSourceForHandoff()
    {
        if (_disposed || !Window.IsVisible)
        {
            return false;
        }

        try
        {
            // Host.Apply establishes the endpoint synchronously, but WPF may still have queued its
            // Measure/Arrange/Render work. Drain through Render before the live HWND wrapper fades
            // in or is uncloaked, then wait for that submitted surface at the desktop boundary.
            Window.UpdateLayout();
            VisualSurface.UpdateLayout();
            Window.Dispatcher.Invoke(
                DispatcherPriority.Render,
                static () => { });
            WindowNative.FlushDesktopComposition();
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule proxy endpoint render barrier failed. Paper={0}; Exception={1}",
                _options.DiagnosticId,
                ex);
            return false;
        }
    }

    public void InvalidateNativeMetrics()
    {
        if (_disposed)
        {
            return;
        }

        unchecked
        {
            _nativeMetricsVersion++;
        }
    }

    private void RefreshNativeMetricsLayout()
    {
        VisualSurface.InvalidateMeasure();
        VisualSurface.InvalidateArrange();
        Root.InvalidateMeasure();
        Root.InvalidateArrange();
        Root.UpdateLayout();
    }

    private bool MatchesNativePresentationLayout(EdgeCapsulePresentationFrame frame)
    {
        var nativeHostBounds = frame.HostBounds;
        if (_disposed ||
            !Window.IsVisible ||
            !WindowNative.TryGetWindowDeviceBounds(Window, out var actualBounds) ||
            actualBounds != nativeHostBounds)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(Window);
        if (Math.Abs(dpi.DpiScaleX - frame.DpiScaleX) > 0.001 ||
            Math.Abs(dpi.DpiScaleY - frame.DpiScaleY) > 0.001)
        {
            return false;
        }
        if (!double.IsFinite(VisualSurface.ActualWidth) ||
            !double.IsFinite(VisualSurface.ActualHeight) ||
            VisualSurface.ActualWidth <= 0 ||
            VisualSurface.ActualHeight <= 0)
        {
            return false;
        }

        var surfaceWidth = (int)Math.Round(
            VisualSurface.ActualWidth * dpi.DpiScaleX,
            MidpointRounding.AwayFromZero);
        var surfaceHeight = (int)Math.Round(
            VisualSurface.ActualHeight * dpi.DpiScaleY,
            MidpointRounding.AwayFromZero);
        return Math.Abs(surfaceWidth - frame.Bounds.Width) <= 1 &&
            Math.Abs(surfaceHeight - frame.Bounds.Height) <= 1 &&
            Math.Abs(VisualSurfaceOffset.Y) <= 0.5;
    }

    private void ResetForFreshApply()
    {
        // Keep an already-visible HWND alive so a floating drag does not lose mouse capture owned
        // by its content element. Opacity 0 plus a hidden applied frame makes the endpoint host
        // click-through while the Presenter retries it as a fresh visual transaction.
        Window.Opacity = 0;
        Root.Opacity = 1;
        Root.IsHitTestVisible = false;
        _appliedFrame = EdgeCapsulePresentationFrame.Hidden;
    }

    internal void RejectNativeBatchApply()
    {
        if (!_disposed)
        {
            ResetForFreshApply();
        }
    }

    internal bool TryGetAppliedPresentation(
        out EdgeCapsulePresentationFrame presentation)
    {
        presentation = _appliedFrame;
        return !_disposed;
    }

    private bool ContainsScreenPoint(DeviceScreenPoint point)
    {
        if (_disposed || !Window.IsVisible || !_appliedFrame.IsHitTestVisible)
        {
            return false;
        }

        // The frame carries the physical body/close rectangle and excludes the transparent shadow
        // margin. Pointer intent never uses the surrounding HWND chrome.
        return EdgeCapsuleGeometry.Contains(_appliedFrame.InteractiveBounds, point);
    }

    private IntPtr OnNativeMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
#if DEBUG
        WindowNative.ObserveNativeGeometryMessage(hwnd, msg);
#endif
        if (msg is WmMouseMove or WmNcMouseMove &&
            _callbacks is { } callbacks &&
            (_appliedFrame.Surface is
                EdgeCapsuleSurfaceKind.DockedResting or
                EdgeCapsuleSurfaceKind.DockedHovered or
                EdgeCapsuleSurfaceKind.DockedActive or
                EdgeCapsuleSurfaceKind.DockedPreview) &&
            WindowNative.TryGetCursorScreenPosition(out var pointer) &&
            ContainsScreenPoint(pointer))
        {
            // The owning HWND and committed InteractiveBounds are the physical authority. Cosmetic
            // WPF hover can be stale, so it must never suppress this wake-up.
            callbacks.PointerInvalidated(pointer);
        }

        if (msg == WmNcHitTest && _experimentalPassive)
        {
            handled = true;
            return HtTransparent;
        }

        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var point = new DeviceScreenPoint(
            unchecked((short)(packed & 0xffff)),
            unchecked((short)((packed >> 16) & 0xffff)));
        if (ContainsScreenPoint(point))
        {
            // Hit testing is the earliest native proof that the pointer reached the committed real
            // capsule. Wake here as well as on the later mouse-move message: a prior WPF enter on
            // an earlier cosmetic WPF enter can otherwise suppress a second enter, and Windows does not
            // guarantee another move before the pointer stops.
            _callbacks?.PointerInvalidated(point);
            return IntPtr.Zero;
        }

        // Chrome shadow margins outside InteractiveBounds are transparent and must behave as if no
        // HWND exists; HostBounds itself is never a substitute for the real input rectangle.
        handled = true;
        return HtTransparent;
    }

    public static EdgeCapsuleHost Create(EdgeCapsuleHostOptions options)
    {
        var root = new Grid
        {
            Background = null,
            ClipToBounds = false,
            Opacity = 1
        };
        var visualSurface = new Grid
        {
            Background = null,
            ClipToBounds = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        var visualSurfaceOffset = new TranslateTransform();
        visualSurface.RenderTransform = visualSurfaceOffset;
        root.Children.Add(visualSurface);
        var chrome = new Border
        {
            Margin = new Thickness(options.WindowChromeMargin),
            CornerRadius = new CornerRadius(options.ChromeCornerRadius),
            BorderThickness = new Thickness(1),
            Background = options.PaperBrush,
            BorderBrush = options.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.10
            }
        };
        Panel.SetZIndex(chrome, 0);
        visualSurface.Children.Add(chrome);

        var shell = new Grid
        {
            Width = double.NaN,
            Height = options.BodyHeight,
            Margin = new Thickness(options.WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        var contentArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(options.InnerCornerRadius, 0, 0, options.InnerCornerRadius),
            Cursor = Cursors.Hand
        };
        var contentHost = new Grid
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var contentGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(options.LeftPadding, 0, 0, 0)
        };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = options.IconText,
            Foreground = options.IconBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = options.IconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        contentGrid.Children.Add(icon);

        var label = new TextBlock
        {
            Foreground = options.TextBrush,
            FontFamily = options.UiFontFamily,
            FontSize = options.LabelFontSize,
            FontWeight = options.LabelFontWeight,
            Margin = new Thickness(options.IconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        contentGrid.Children.Add(label);
        contentHost.Children.Add(contentGrid);
        contentArea.Child = contentHost;
        Grid.SetColumn(contentArea, 0);
        shell.Children.Add(contentArea);

        var closeGlyph = new TextBlock
        {
            Text = "×",
            Foreground = options.TextBrush,
            FontFamily = options.SymbolFontFamily,
            FontSize = AppTypography.Scale(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeArea = new Border
        {
            Width = 0,
            Opacity = 0,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Cursor = Cursors.Hand,
            ToolTip = options.CloseToolTip,
            IsHitTestVisible = false,
            Child = closeGlyph
        };
        Grid.SetColumn(closeArea, 1);
        shell.Children.Add(closeArea);
        Panel.SetZIndex(shell, 10);
        visualSurface.Children.Add(shell);

        var outlineMargin = options.WindowChromeMargin - options.OutlineThickness + options.OutlineOverlap;
        var outline = new Border
        {
            Margin = new Thickness(outlineMargin),
            CornerRadius = new CornerRadius(
                options.ChromeCornerRadius + options.OutlineThickness - options.OutlineOverlap),
            BorderThickness = new Thickness(options.OutlineThickness),
            BorderBrush = options.OutlineBrush,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(outline, 20);
        visualSurface.Children.Add(outline);

        var window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            FontFamily = options.UiFontFamily,
            Language = options.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = options.Topmost,
            Content = root
        };
        AppTypography.ApplyTextRendering(window);
        AppTypography.ApplyTextRendering(label);

        return new EdgeCapsuleHost(
            options,
            window,
            root,
            visualSurface,
            visualSurfaceOffset,
            chrome,
            outline,
            shell,
            contentArea,
            contentHost,
            contentGrid,
            icon,
            closeArea,
            closeGlyph,
            label);
    }

    public void AttachInput(EdgeCapsuleHostCallbacks callbacks)
    {
        if (_disposed || _callbacks != null)
        {
            return;
        }

        _callbacks = callbacks;
        var shell = Shell;
        var content = ContentArea;
        var close = CloseArea;
        var closeGlyph = CloseGlyph;

        void BeginContentPointer(MouseButtonEventArgs e)
        {
            if (IsPreviewInteractiveSource(
                    e.OriginalSource as DependencyObject))
            {
                ArmPreviewInteractiveCaptureLease();
                return;
            }

            callbacks.PointerPressed(PointerScreenPosition(e));
            if (!content.CaptureMouse())
            {
                callbacks.CaptureLost(new EdgeCapsuleCaptureLoss(
                    LeftButtonPressed: true,
                    EdgeCapsuleCaptureLossReason.AcquisitionFailed));
            }
            e.Handled = true;
        }

        void CompleteContentPointer(MouseButtonEventArgs e)
        {
            if (!content.IsMouseCaptured ||
                !callbacks.PointerReleased(PointerScreenPosition(e)))
            {
                return;
            }
            content.ReleaseMouseCapture();
            e.Handled = true;
        }

        content.MouseEnter += (_, _) =>
        {
            if (!_previewVisible)
            {
                content.Background = _hoverBrush;
            }
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                close.Background = _hoverBrush;
            }
        };
        content.MouseLeave += (_, _) =>
        {
            content.Background = Brushes.Transparent;
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                close.Background = Brushes.Transparent;
            }
        };
        shell.MouseEnter += (_, _) => callbacks.PointerInvalidated(null);
        shell.MouseLeave += (_, _) => callbacks.PointerInvalidated(null);
        content.PreviewMouseLeftButtonDown += (_, e) => BeginContentPointer(e);
        content.PreviewMouseRightButtonDown += (_, _) =>
            ArmPreviewInteractiveCaptureLease();
        content.PreviewMouseMove += (_, e) =>
        {
            if (!content.IsMouseCaptured)
            {
                return;
            }
            e.Handled = callbacks.PointerMoved(
                PointerScreenPosition(e),
                e.LeftButton == MouseButtonState.Pressed);
        };
        content.PreviewMouseLeftButtonUp += (_, e) => CompleteContentPointer(e);
        content.LostMouseCapture += (_, _) =>
        {
            var reason = _contentCaptureLossReason;
            _contentCaptureLossReason = EdgeCapsuleCaptureLossReason.Unplanned;
            var action = callbacks.CaptureLost(new EdgeCapsuleCaptureLoss(
                Mouse.LeftButton == MouseButtonState.Pressed,
                reason));
            if (action == EdgeCapsuleCaptureAction.Recapture)
            {
                if (!content.IsVisible ||
                    !content.IsEnabled ||
                    !content.CaptureMouse())
                {
                    callbacks.CaptureLost(new EdgeCapsuleCaptureLoss(
                        Mouse.LeftButton == MouseButtonState.Pressed,
                        EdgeCapsuleCaptureLossReason.AcquisitionFailed));
                }
            }
        };

        close.MouseEnter += (_, _) =>
        {
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                content.Background = _hoverBrush;
                close.Background = _hoverBrush;
                return;
            }
            content.Background = Brushes.Transparent;
            close.Background = _hoverBrush;
            closeGlyph.Foreground = _textBrush;
        };
        close.MouseLeave += (_, _) =>
        {
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                content.Background = Brushes.Transparent;
                close.Background = Brushes.Transparent;
                return;
            }
            close.Background = Brushes.Transparent;
            closeGlyph.Foreground = _weakTextBrush;
            close.Opacity = Math.Clamp(_appliedCloseWidth / Math.Max(1, _maximumCloseWidth), 0, 1);
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                BeginContentPointer(e);
                return;
            }
            close.Opacity = 0.72;
            e.Handled = true;
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            if (_appliedFrame.CloseSegmentActsAsContent)
            {
                CompleteContentPointer(e);
                return;
            }
            callbacks.CloseInvoked();
            e.Handled = true;
        };
    }

    public void SetContextMenu(ContextMenu contextMenu)
    {
        if (!_disposed)
        {
            ContentArea.ContextMenu = contextMenu;
            CloseArea.ContextMenu = _appliedFrame.CloseSegmentActsAsContent
                ? contextMenu
                : null;
        }
    }

    public bool IsTopmost => !_disposed && Window.Topmost;

    public void ReleaseContentPointer()
    {
        if (!_disposed && ContentArea.IsMouseCaptured)
        {
            ContentArea.ReleaseMouseCapture();
        }
    }

    public EdgeCapsuleNativeDragOutcome TransferContentPointerToNativeDrag(
        Func<EdgeCapsuleNativeDragOutcome> runNativeDrag)
    {
        if (_disposed || !ContentArea.IsMouseCaptured)
        {
            return runNativeDrag();
        }

        _contentCaptureLossReason = EdgeCapsuleCaptureLossReason.NativeDragTransfer;
        try
        {
            // RunNativeDrag releases Win32 capture immediately before entering the caption move
            // loop. Keeping WPF capture until then leaves no gap where a fast MouseUp can be lost.
            return runNativeDrag();
        }
        finally
        {
            _contentCaptureLossReason = EdgeCapsuleCaptureLossReason.Unplanned;
        }
    }

    public void SetLabel(string label, string toolTip)
    {
        if (!_disposed)
        {
            Label.Text = label;
            Label.ToolTip = toolTip;
        }
    }

    public void ApplyToolTipSetting(bool enabled)
    {
        if (!_disposed)
        {
            ToolTipPreferences.Apply(Window, enabled);
        }
    }

    public void UpdateTypography(
        FontFamily uiFontFamily,
        FontFamily symbolFontFamily,
        System.Windows.Markup.XmlLanguage language,
        double iconFontSize,
        double labelFontSize,
        FontWeight labelFontWeight,
        double closeGlyphFontSize)
    {
        if (_disposed)
        {
            return;
        }
        Window.FontFamily = uiFontFamily;
        Window.Language = language;
        AppTypography.ApplyTextRendering(Window);
        AppTypography.ApplyTextRendering(Label);
        Icon.FontFamily = symbolFontFamily;
        Icon.FontSize = iconFontSize;
        Label.FontFamily = uiFontFamily;
        Label.FontSize = labelFontSize;
        Label.FontWeight = labelFontWeight;
        CloseGlyph.FontSize = closeGlyphFontSize;
    }

    public bool WouldChangeZOrder(bool topmost, IntPtr insertAfter)
    {
        if (_disposed)
        {
            return false;
        }

        var effectiveTopmost = topmost && !_experimentalPassive;
        return !_zOrderStateInitialized ||
            _appliedTopmost != effectiveTopmost ||
            _appliedTopmostInsertAfter != insertAfter;
    }

    public void SetTopmost(bool topmost, IntPtr insertAfter)
    {
        if (_disposed)
        {
            return;
        }
        var effectiveTopmost = topmost && !_experimentalPassive;
        Window.Topmost = effectiveTopmost;
        if (Window.IsVisible)
        {
            if (_experimentalPassive)
            {
                WindowNative.ApplyBottomZOrder(Window);
            }
            else
            {
                WindowNative.ApplyTopmostZOrder(
                    Window,
                    effectiveTopmost,
                    insertAfter);
            }
        }
        _zOrderStateInitialized = true;
        _appliedTopmost = effectiveTopmost;
        _appliedTopmostInsertAfter = insertAfter;
    }

    public void SetInteractionLocked(bool enabled)
    {
        if (_disposed || _interactionLocked == enabled)
        {
            return;
        }

        _interactionLocked = enabled;
        WindowNative.SetInputPassthrough(Window, enabled || _experimentalPassive);
    }

    public void SetExperimentalPassive(bool enabled)
    {
        if (_disposed || _experimentalPassive == enabled)
        {
            return;
        }

        _experimentalPassive = enabled;
        _zOrderStateInitialized = false;
        WindowNative.SetInputPassthrough(Window, enabled || _interactionLocked);
        if (enabled)
        {
            Window.Topmost = false;
            if (Window.IsVisible)
            {
                WindowNative.ApplyBottomZOrder(Window);
            }
        }
    }

    public DeviceScreenPoint ScreenOrigin()
    {
        if (_disposed)
        {
            return default;
        }
        if (_appliedFrame.Visible && !_appliedFrame.Bounds.IsEmpty)
        {
            return new DeviceScreenPoint(
                _appliedFrame.Bounds.Left,
                _appliedFrame.Bounds.Top);
        }
        return DeviceScreenPoint.FromPoint(Window.PointToScreen(new Point(0, 0)));
    }

    public bool ContainsWindowScreenPoint(Point screenPoint)
    {
        return ContainsScreenPoint(DeviceScreenPoint.FromPoint(screenPoint));
    }

    public bool TryGetMonitorGeometry(string? deviceName, out MonitorGeometry geometry)
    {
        if (_disposed)
        {
            geometry = default;
            return false;
        }
        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(deviceName, Window, out geometry);
    }

    public DpiScale Dpi => !_disposed
        ? VisualTreeHelper.GetDpi(Window)
        : new DpiScale(1, 1);

    public void BringToFrontNoActivate()
    {
        if (!_disposed)
        {
            WindowNative.BringToFrontNoActivate(Window);
        }
    }

    private DeviceScreenPoint PointerScreenPosition(MouseEventArgs e)
    {
        if (!_disposed && PresentationSource.FromVisual(Shell) != null)
        {
            return DeviceScreenPoint.FromPoint(Shell.PointToScreen(e.GetPosition(Shell)));
        }
        return default;
    }

    public void UpdateTheme(
        Brush paperBrush,
        Brush paperBorderBrush,
        Brush outlineBrush,
        Brush hoverBrush,
        Brush iconBrush,
        Brush strongTextBrush,
        Brush weakTextBrush,
        string iconText,
        double iconFontSize,
        EdgeCapsulePreviewThemeResources previewResources)
    {
        if (_disposed)
        {
            return;
        }
        _hoverBrush = hoverBrush;
        _textBrush = strongTextBrush;
        _weakTextBrush = weakTextBrush;
        Chrome.Background = paperBrush;
        Chrome.BorderBrush = paperBorderBrush;
        Outline.BorderBrush = outlineBrush;
        Label.Foreground = weakTextBrush;
        Icon.Text = iconText;
        Icon.FontSize = iconFontSize;
        Icon.Foreground = iconBrush;
        CloseGlyph.Foreground = weakTextBrush;
        Window.Foreground = strongTextBrush;

        // Preview content lives in this standalone HWND rather than in PaperWindow, so every
        // DynamicResource used by the shared Markdown/Todo views must be rooted here as well.
        Window.Resources["PaperBrushKey"] = paperBrush;
        Window.Resources["PaperBorderBrushKey"] = paperBorderBrush;
        Window.Resources["TextBrushKey"] = strongTextBrush;
        Window.Resources["WeakTextBrushKey"] = weakTextBrush;
        Window.Resources["HoverBrushKey"] = hoverBrush;
        Window.Resources["LinkBrushKey"] = previewResources.LinkBrush;
        Window.Resources["CheckBoxBorderBrushKey"] =
            previewResources.CheckBoxBorderBrush;
        Window.Resources["CheckBoxActiveBrushKey"] =
            previewResources.CheckBoxActiveBrush;
        Window.Resources["CheckBoxUncheckedHoverBorderBrushKey"] =
            previewResources.CheckBoxUncheckedHoverBorderBrush;
        Window.Resources["CheckBoxUncheckedHoverBgKey"] =
            previewResources.CheckBoxUncheckedHoverBackgroundBrush;
        Window.Resources["CheckBoxActiveHoverBrushKey"] =
            previewResources.CheckBoxActiveHoverBrush;
    }

    private void ApplyFixedLayout(EdgeCapsuleEdge edge)
    {
        if (_appliedEdge == edge)
        {
            return;
        }
        if (_disposed)
        {
            return;
        }

        var options = _options;
        var leftEdge = edge == EdgeCapsuleEdge.Left;
        ApplyContentOrder(edge, options);
        var outlineMargin = options.WindowChromeMargin - options.OutlineThickness + options.OutlineOverlap;
        var bodyCorner = EdgeCorner(edge, options.ChromeCornerRadius);
        var outlineCorner = EdgeCorner(
            edge,
            options.ChromeCornerRadius + options.OutlineThickness - options.OutlineOverlap);

        Chrome.Margin = leftEdge
            ? new Thickness(0, options.WindowChromeMargin, options.WindowChromeMargin, options.WindowChromeMargin)
            : new Thickness(options.WindowChromeMargin, options.WindowChromeMargin, 0, options.WindowChromeMargin);
        Chrome.HorizontalAlignment = HorizontalAlignment.Stretch;
        Chrome.VerticalAlignment = VerticalAlignment.Top;
        Chrome.Width = double.NaN;
        Chrome.Height = options.BodyHeight;
        Chrome.CornerRadius = bodyCorner;
        Chrome.BorderThickness = leftEdge
            ? new Thickness(0, 1, 1, 1)
            : new Thickness(1, 1, 0, 1);

        Shell.Margin = leftEdge
            ? new Thickness(0, options.WindowChromeMargin, options.WindowChromeMargin, options.WindowChromeMargin)
            : new Thickness(options.WindowChromeMargin, options.WindowChromeMargin, 0, options.WindowChromeMargin);
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.Width = double.NaN;
        Shell.Height = options.BodyHeight;

        Outline.Margin = leftEdge
            ? new Thickness(0, outlineMargin, outlineMargin, outlineMargin)
            : new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
        Outline.HorizontalAlignment = HorizontalAlignment.Stretch;
        Outline.VerticalAlignment = VerticalAlignment.Top;
        Outline.Width = double.NaN;
        Outline.Height = Math.Max(0, options.BodyHeight + options.WindowChromeMargin * 2 - outlineMargin * 2);
        Outline.CornerRadius = outlineCorner;
        Outline.BorderThickness = leftEdge
            ? new Thickness(0, options.OutlineThickness, options.OutlineThickness, options.OutlineThickness)
            : new Thickness(options.OutlineThickness, options.OutlineThickness, 0, options.OutlineThickness);

        ApplySegmentCorners(edge, options.InnerCornerRadius);
        _appliedEdge = edge;
    }

    private void ApplyVisualSurface(EdgeCapsulePresentationFrame frame)
    {
        var surface = VisualSurface;
        surface.HorizontalAlignment = frame.Edge == EdgeCapsuleEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        surface.Width = frame.Bounds.Width / Math.Max(1, frame.DpiScaleX);
        surface.Height = frame.Bounds.Height / Math.Max(1, frame.DpiScaleY);
        VisualSurfaceOffset.X = 0;
        // Global queue translation belongs exclusively to the DComp visual. The live WPF shape is
        // always rendered at the local origin of its endpoint host, so translation is never applied
        // twice while the HWND is cloaked under the compositor cover.
        VisualSurfaceOffset.Y = 0;
    }

    private void ApplyCloseSegmentMode(EdgeCapsulePresentationFrame frame)
    {
        var actsAsContent = frame.CloseSegmentActsAsContent;
        CloseGlyph.Visibility = actsAsContent ? Visibility.Collapsed : Visibility.Visible;
        CloseArea.ToolTip = actsAsContent ? null : _options.CloseToolTip;
        CloseArea.ContextMenu = actsAsContent ? ContentArea.ContextMenu : null;
        ContentArea.Background = Brushes.Transparent;
        CloseArea.Background = Brushes.Transparent;
    }

    private void ApplySegmentWidths(
        EdgeCapsulePresentationFrame frame,
        double width,
        double maximumWidth,
        bool enableHitTest)
    {
        if (_disposed)
        {
            return;
        }

        width = Math.Clamp(width, 0, maximumWidth);
        _maximumCloseWidth = maximumWidth;
        _appliedCloseWidth = width;
        var bodyWindowWidthDip = frame.BodyWindowWidthDevice / Math.Max(1, frame.DpiScaleX);
        var contentWidth = Math.Max(0, bodyWindowWidthDip - _options.WindowChromeMargin);
        if (frame.Edge == EdgeCapsuleEdge.Left)
        {
            Shell.ColumnDefinitions[0].Width = new GridLength(width);
            Shell.ColumnDefinitions[1].Width = new GridLength(contentWidth);
        }
        else
        {
            Shell.ColumnDefinitions[0].Width = new GridLength(contentWidth);
            Shell.ColumnDefinitions[1].Width = new GridLength(width);
        }
        CloseArea.Width = double.NaN;
        CloseArea.Opacity = frame.CloseSegmentActsAsContent
            ? 1
            : maximumWidth <= 0 ? 0 : width / maximumWidth;
        CloseArea.IsHitTestVisible =
            enableHitTest && maximumWidth > 0 &&
            (frame.CloseSegmentActsAsContent
                ? width > 0
                : width >= maximumWidth - 0.5);
    }

    private void ApplyContentOrder(EdgeCapsuleEdge edge, EdgeCapsuleHostOptions options)
    {
        var leftEdge = edge == EdgeCapsuleEdge.Left;
        ContentArea.Cursor = Cursors.Hand;
        Grid.SetColumn(ContentArea, leftEdge ? 1 : 0);
        Grid.SetColumnSpan(ContentArea, 1);
        Grid.SetColumn(CloseArea, leftEdge ? 0 : 1);

        ContentGrid.Margin = leftEdge
            ? new Thickness(0, 0, options.LeftPadding, 0)
            : new Thickness(options.LeftPadding, 0, 0, 0);
        if (ContentGrid.ColumnDefinitions.Count >= 2)
        {
            ContentGrid.ColumnDefinitions[0].Width = leftEdge
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            ContentGrid.ColumnDefinitions[1].Width = leftEdge
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
        }
        Grid.SetColumn(Icon, leftEdge ? 1 : 0);
        Icon.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Icon.TextAlignment = leftEdge ? TextAlignment.Right : TextAlignment.Left;
        Grid.SetColumn(Label, leftEdge ? 0 : 1);
        Label.Margin = leftEdge
            ? new Thickness(0, 0, options.IconGap, 0)
            : new Thickness(options.IconGap, 0, 0, 0);
        Label.TextAlignment = leftEdge ? TextAlignment.Right : TextAlignment.Left;
    }

    private void ApplySegmentCorners(EdgeCapsuleEdge edge, double radius)
    {
        if (_disposed)
        {
            return;
        }

        ContentArea.CornerRadius = edge == EdgeCapsuleEdge.Left
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);
        CloseArea.CornerRadius = new CornerRadius(0);
    }

    private static CornerRadius EdgeCorner(EdgeCapsuleEdge edge, double radius) =>
        edge == EdgeCapsuleEdge.Left
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DetachPreviewContent();
        _disposed = true;
        Window.Content = null;
        Window.Close();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.UnregisterTransparentHost(
            _diagnosticResourceId,
            _options.DiagnosticId);
#endif
        _callbacks = null;
        _appliedEdge = null;
    }
}
