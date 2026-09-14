using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// SourceBounds on planes is atlas-local PHYSICAL LTRB. HostOffset is real-host-local PHYSICAL XY.
// SizeDip is the canonical unscaled WPF size. SourceBounds may also reserve transparent capacity;
// neither padding nor pixel rounding authorizes stretching text.
internal sealed record EdgeCapsuleCompositionPlane(
    DeviceScreenRect SourceBounds, DeviceScreenPoint HostOffset, Size SizeDip);
internal readonly record struct EdgeCapsuleCompositionInsets(int Left, int Top, int Right, int Bottom);
internal sealed record EdgeCapsuleCompositionShellPlane(
    DeviceScreenRect SourceBounds, Size SizeDip, EdgeCapsuleCompositionInsets Insets);
internal sealed record EdgeCapsuleCompositionStyle(
    Brush PaperBrush, Brush PaperBorderBrush, Brush OutlineBrush,
    CornerRadius ChromeCorners, CornerRadius OutlineCorners,
    Thickness ChromeBorder, Thickness OutlineBorder, Thickness ChromeMargin, Thickness OutlineMargin,
    double ShadowBlurRadius, double ShadowDepth, double ShadowOpacity, Color ShadowColor);
internal sealed record EdgeCapsuleProxySourceDescription(
    IntPtr OwnerHandle, IntPtr SourceHandle, long Generation, long Revision,
    DpiScale Dpi, DeviceScreenRect NativeBounds, EdgeCapsuleEdge Edge,
    EdgeCapsuleCompositionPlane Compact, EdgeCapsuleCompositionPlane? Preview,
    EdgeCapsuleCompositionPlane? Close, EdgeCapsuleCompositionShellPlane ShellChrome,
    EdgeCapsuleCompositionShellPlane ShellOutline, EdgeCapsuleCompositionStyle Style,
    EdgeCapsulePresentationFrame LayoutFrame, int CompactBodyHeightDevice, int PreviewBodyHeightDevice,
    CornerRadius PreviewViewportCorners, EdgeCapsuleCompositionShellPlane ContentBackground,
    EdgeCapsuleCompositionShellPlane CloseBackground, DeviceScreenRect ContentBackgroundBounds,
    DeviceScreenRect CloseBackgroundBounds, CornerRadius ContentBackgroundCorners, CornerRadius CloseBackgroundCorners);

/// <summary>
/// Independent native visual reference. The atlas HWND is never an input target. Generation names
/// the HWND; Revision names its current plane description. Invalidated is advisory: the consumer
/// schedules a handoff, never synchronously completes it from a Host/layout callback. Old visuals
/// and their last arranged content remain referenced until every lease is explicitly relinquished.
/// </summary>
internal sealed class EdgeCapsuleProxySourceLease : IDisposable
{
    private EdgeCapsuleHost.ProxySource? _source;
    internal EdgeCapsuleProxySourceLease(EdgeCapsuleHost.ProxySource source)
    {
        _source = source;
        OwnerHandle = source.OwnerHandle; Generation = source.Generation;
        SourceBounds = source.SourceBounds; Dpi = source.Dpi;
        source.Updated += OnUpdated;
        source.Invalidated += OnInvalidated;
    }
    internal IntPtr SourceHandle => _source?.SourceHandle ?? IntPtr.Zero;
    internal IntPtr OwnerHandle { get; }
    internal long Generation { get; }
    // Compatibility name: this is the atlas HWND's screen-physical rectangle, NOT a plane crop.
    internal DeviceScreenRect SourceBounds { get; }
    internal DpiScale Dpi { get; }
    internal EdgeCapsuleProxySourceDescription Description => _source == null
        ? throw new ObjectDisposedException(nameof(EdgeCapsuleProxySourceLease))
        : _source.Description ?? throw new InvalidOperationException("The layered source was not initialized.");
    internal bool IsCurrent => _source?.IsCurrent == true;
    internal bool IsLayoutPending => _source?.IsLayoutPending == true;
    internal bool Synchronize(EdgeCapsulePresentationFrame frame) => _source?.Synchronize(frame) == true;
    // Every native consumer must explicitly prove its last submitted preview plane is invisible.
    // Absence/exception is a refusal. Queries must be read-only (no Commit/completion from Host).
    internal Func<bool>? CanReplacePreview { get; set; }
    internal event Action? Updated;
    internal event Action? Invalidated;
    private void OnUpdated() => Updated?.Invoke();
    private void OnInvalidated() => Invalidated?.Invoke();
    public void Dispose()
    {
        var source = _source;
        if (source == null) return;
        source.VerifyAccess();
        _source = null;
        source.Updated -= OnUpdated; source.Invalidated -= OnInvalidated;
        Updated = null; Invalidated = null;
        CanReplacePreview = null;
        source.Release(this);
    }
}

internal sealed partial class EdgeCapsuleHost
{
    private ProxySource? _proxySource;
    private readonly HashSet<ProxySource> _proxySourceResources = new();
    private long _proxySourceGeneration;
    private bool _preparingProxySource;
    internal double ActualContentOpacity => Root.Opacity;

    internal bool TryAcquireProxySource(out EdgeCapsuleProxySourceLease? lease)
    {
#if DEBUG
        using var observation = EdgeDiagnosticObservation.Begin("proxy.source-acquire", this);
#endif
        Dispatcher.VerifyAccess();
        lease = null;
        if (_disposed || _preparingProxySource || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            !Window.IsVisible || !_appliedFrame.Visible || !MatchesPresentation(_appliedFrame)) return false;
        var source = _proxySource;
        if (source != null)
        {
            if (source.Synchronize(_appliedFrame) && source.IsCurrent)
                return source.TryAddReference(out lease);
            if (source.AtlasDeferred) return false;
            source.Invalidate();
        }
        // At most a current source plus one leased retired source. Repeated invalidation falls back
        // to the real Host; it cannot accumulate a third live atlas or block normal content cleanup.
        if (_proxySourceResources.Count >= 2) return false;
        _preparingProxySource = true;
        source = new ProxySource(this, ++_proxySourceGeneration);
        _proxySource = source;
        _proxySourceResources.Add(source);
        try
        {
            if (source.Initialize() && source.TryAddReference(out lease)) return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Edge layered source creation failed. Paper={0}; Exception={1}",
                _options.DiagnosticId, ex);
        }
        finally { _preparingProxySource = false; }
        source.Close();
        return false;
    }

    private void ProxySourcePreviewChanging(FrameworkElement? next, double width, double height)
    {
        var source = _proxySource;
        // A final compact frame may detach the real tree while its invisible native plane remains
        // retained. Keep that Visual alive; only a replacement needs an explicit consumer admission.
        if (next != null && source?.PreviewVisual != null &&
            (!ReferenceEquals(source.PreviewVisual, next) ||
             Math.Abs(source.PreviewSize.Width - width) > .001 ||
             Math.Abs(source.PreviewSize.Height - height) > .001) && !source.CanReplacePreview()) source.Invalidate();
    }

    // Resource lifetime only. No per-paper state/transition, alternate content model, or clock.
    internal sealed partial class ProxySource
    {
        private readonly EdgeCapsuleHost _owner;
        private readonly int _metricsVersion;
        private readonly EdgeCapsuleEdge _edge;
        private readonly double _wallDeviceX;
        private readonly DeviceScreenRect _capacity;
        private Window? _window;
        private Canvas? _canvas;
        private HwndSource? _hwndSource;
        private DispatcherOperation? _notification;
        private int _references;
        private readonly HashSet<EdgeCapsuleProxySourceLease> _leases = new();
        private int _leaseVersion;
        private bool _checkingReplacement;
        private bool _waitingForLayout;
        private bool _ready, _closed, _invalid, _synchronizing, _updatedPending, _invalidPending;

        internal ProxySource(EdgeCapsuleHost owner, long generation)
        {
            _owner = owner; OwnerHandle = owner.Handle; Generation = generation; Dpi = owner.Dpi;
            _capacity = owner._appliedFrame.HostBounds;
            _metricsVersion = owner._nativeMetricsVersion;
            _edge = owner._appliedFrame.Edge;
            _wallDeviceX = owner._appliedFrame.WallDeviceX;
        }
        internal IntPtr SourceHandle => !_closed && _window != null
            ? new WindowInteropHelper(_window).Handle : IntPtr.Zero;
        internal IntPtr OwnerHandle { get; }
        internal long Generation { get; }
        internal DeviceScreenRect SourceBounds { get; private set; }
        internal DpiScale Dpi { get; }
        internal EdgeCapsuleProxySourceDescription? Description { get; private set; }
        internal event Action? Updated;
        internal event Action? Invalidated;
        internal void VerifyAccess() => _owner.Dispatcher.VerifyAccess();
        private bool IsReady => _ready && !_closed && !_invalid &&
            _window is { IsVisible: true, Opacity: 1 };
        internal bool IsLayoutPending => IsReady && AtlasDeferred;

        internal bool IsCurrent
        {
            get
            {
                VerifyAccess();
                return IsReady && !AtlasDeferred && OwnerIsCompatible(_owner._appliedFrame) && AtlasIsCompatible() &&
                    PreviewBindingIsCurrent &&
                    WindowNative.IsWindowHandleAlive(OwnerHandle) &&
                    WindowNative.TryGetWindowDeviceBounds(_owner.Window, out var bounds) &&
                    bounds == _owner._appliedFrame.HostBounds && SourceWindowIsCompatible();
            }
        }

        internal bool Initialize()
        {
#if DEBUG
            using var observation = EdgeDiagnosticObservation.Begin("proxy.source-initialize", this);
#endif
            VerifyAccess();
            if (!OwnerIsCompatible(_owner._appliedFrame) || !PrepareAtlas() ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForWindowHandle(OwnerHandle, out var monitor) ||
                Math.Abs(monitor.DpiScaleX - Dpi.DpiScaleX) > .001 ||
                Math.Abs(monitor.DpiScaleY - Dpi.DpiScaleY) > .001) return false;
            SourceBounds = new(monitor.WorkArea.Left, monitor.WorkArea.Top,
                checked(monitor.WorkArea.Left + _atlasWidth), checked(monitor.WorkArea.Top + _atlasHeight));
            var size = new Size(_atlasWidth / Dpi.DpiScaleX, _atlasHeight / Dpi.DpiScaleY);
            _window = new Window
            {
                ShowActivated = false, ShowInTaskbar = false, Focusable = false, IsHitTestVisible = false,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
                Width = size.Width, Height = size.Height,
                Left = SourceBounds.Left / Dpi.DpiScaleX, Top = SourceBounds.Top / Dpi.DpiScaleY,
                Topmost = false, Opacity = 0, Content = _canvas,
                FontFamily = _owner._options.UiFontFamily, Language = _owner._options.Language,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            _owner.Window.Closed += OnOwnerClosed;
            AppTypography.ApplyTextRendering(_window);
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            WindowNative.ApplyNoActivateStyle(_window);
            WindowNative.SetInputPassthrough(_window, true);
            // Unshown HWND -> app-cloak readback -> Show under opacity zero -> verify -> alpha one.
            // Keep it on-screen: an off-screen/zero-size HWND is not a dependable live DComp source.
            if (!WindowNative.TrySetWindowCloaked(handle, true) ||
                !WindowNative.TrySetWindowDeviceBounds(_window, SourceBounds)) return false;
            _window.Show();
            if (_closed || !SourceWindowIsCompatible()) return false;
            WindowNative.ApplyBottomZOrder(_window);
            _hwndSource = HwndSource.FromHwnd(handle);
            if (_hwndSource == null) return false;
            _hwndSource.AddHook(OnNativeMessage);
            _window.Opacity = 1;
            _ready = true;
            _window.UpdateLayout();
            if (!Synchronize(_owner._appliedFrame)) return false;
            _window.UpdateLayout();
            // Queue publication owns shared Render/Commit/fence; there is no per-member DwmFlush.
            return IsCurrent;
        }

        internal bool TryAddReference(out EdgeCapsuleProxySourceLease? lease)
        {
            VerifyAccess(); lease = null;
            if (!IsCurrent || _references == int.MaxValue) return false;
            lease = new EdgeCapsuleProxySourceLease(this); _references++;
            _leases.Add(lease); _leaseVersion++;
            return true;
        }

        internal bool Synchronize(EdgeCapsulePresentationFrame frame)
        {
            VerifyAccess();
            if (_synchronizing || !IsReady) return false;
            if (!OwnerIsCompatible(frame)) { Invalidate(); return false; }
            var wasDeferred = AtlasDeferred;
            _synchronizing = true;
            try
            {
                if (!SynchronizeAtlas())
                {
                    if (AtlasDeferred) WaitForLayout();
                    else Invalidate();
                    return false;
                }
                StopWaitingForLayout();
                // Layout can change only the brush viewbox, with no new slot/Revision. Consumers
                // which deferred a native update still need this readiness notification.
                if (wasDeferred)
                {
                    _updatedPending = true;
                    QueueNotification();
                }
                return IsReady && OwnerIsCompatible(frame);
            }
            catch (Exception ex)
            {
                Invalidate();
                Trace.TraceWarning("Edge layered source synchronization failed. Paper={0}; Exception={1}",
                    _owner._options.DiagnosticId, ex);
                return false;
            }
            finally { _synchronizing = false; }
        }

        private void WaitForLayout()
        {
            if (_waitingForLayout || !IsReady || _owner.Dispatcher.HasShutdownStarted) return;
            _waitingForLayout = true;
            _owner.Root.LayoutUpdated += OnOwnerLayoutUpdated;
        }

        private void StopWaitingForLayout()
        {
            if (!_waitingForLayout) return;
            _waitingForLayout = false;
            _owner.Root.LayoutUpdated -= OnOwnerLayoutUpdated;
        }

        private void OnOwnerLayoutUpdated(object? sender, EventArgs e)
        {
            // LayoutUpdated is Dispatcher-wide. An unrelated window's layout is not proof that
            // these sampled visuals/ancestors are arranged. Keep this one pending subscription
            // until our own layout settles; there is no timer, forced layout, or idle polling.
            if (!_waitingForLayout || _synchronizing) return;
            if (!IsReady || !OwnerIsCompatible(_owner._appliedFrame))
            {
                StopWaitingForLayout();
                Invalidate();
                return;
            }
            if (HasPendingAtlasLayout()) return;
            StopWaitingForLayout();
            Synchronize(_owner._appliedFrame);
        }

        private bool OwnerIsCompatible(EdgeCapsulePresentationFrame frame)
        {
            if (_closed || _invalid || _owner._disposed || !_owner.Window.IsVisible ||
                !frame.Visible || !frame.IsUsable || _owner._appliedFrame != frame || _owner.Handle != OwnerHandle ||
                _owner._nativeMetricsVersion != _metricsVersion ||
                _owner._appliedNativeMetricsVersion != _metricsVersion || frame.Edge != _edge ||
                frame.WallDeviceX != _wallDeviceX || frame.HostBounds.Width != _capacity.Width ||
                frame.HostBounds.Height != _capacity.Height ||
                Math.Abs(frame.DpiScaleX - Dpi.DpiScaleX) > .001 ||
                Math.Abs(frame.DpiScaleY - Dpi.DpiScaleY) > .001) return false;
            var dpi = _owner.Dpi;
            return Math.Abs(dpi.DpiScaleX - Dpi.DpiScaleX) < .001 &&
                Math.Abs(dpi.DpiScaleY - Dpi.DpiScaleY) < .001;
        }

        private bool SourceWindowIsCompatible()
        {
            if (_window is not { IsVisible: true } window) return false;
            var handle = new WindowInteropHelper(window).Handle;
            var dpi = VisualTreeHelper.GetDpi(window);
            return handle != IntPtr.Zero && WindowNative.IsWindowHandleAlive(handle) &&
                WindowNative.TryGetWindowDeviceBounds(window, out var bounds) && bounds == SourceBounds &&
                Math.Abs(dpi.DpiScaleX - Dpi.DpiScaleX) < .001 && Math.Abs(dpi.DpiScaleY - Dpi.DpiScaleY) < .001 &&
                (!_ready || window.Opacity == 1) &&
                DwmGetWindowAttribute(handle, 14, out var cloak, sizeof(int)) == 0 && (cloak & 1) != 0;
        }

        internal void Invalidate()
        {
            if (_closed || _invalid) return;
            _invalid = true;
            StopWaitingForLayout();
            if (ReferenceEquals(_owner._proxySource, this)) _owner._proxySource = null;
            _invalidPending = true;
            QueueNotification();
            if (_references == 0) Close();
        }

        private void QueueNotification()
        {
            if (_closed || _notification != null || _owner.Dispatcher.HasShutdownStarted) return;
            // Content mutation/Host.Apply must never synchronously reenter native completion.
            _notification = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _notification = null;
                var invalid = _invalidPending;
                var updated = _updatedPending;
                _invalidPending = _updatedPending = false;
                if (_closed) return;
                var handlers = invalid ? Invalidated : updated ? Updated : null;
                if (handlers == null) return;
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception ex) { Trace.TraceWarning("Edge layered source observer failed: {0}", ex); }
                }
            }));
        }

        internal bool CanReplacePreview()
        {
            if (_closed || _invalid || _checkingReplacement) return false;
            _checkingReplacement = true;
            var version = _leaseVersion;
            try
            {
                foreach (var lease in _leases.ToArray())
                    if (lease.CanReplacePreview?.Invoke() != true) return false;
                return !_closed && !_invalid && version == _leaseVersion;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Edge preview replacement admission failed: {0}", ex);
                return false;
            }
            finally { _checkingReplacement = false; }
        }

        internal void Release(EdgeCapsuleProxySourceLease lease)
        {
            VerifyAccess();
            if (!_leases.Remove(lease) || _references <= 0) return;
            _leaseVersion++;
            if (--_references != 0) return;
            if (_invalid || !ReferenceEquals(_owner._proxySource, this))
            {
                Close();
                return;
            }
            // A preview can temporarily use its real HWND while the new content is arranging.
            // That ends the proxy's lease, not the Host's compatible atlas ownership. Retain one
            // current bounded source so the next successor can reuse it. Hidden/disposed Hosts
            // and incompatible generations still retire through Invalidate/Close; retired sources
            // are never cached. Keep the existing compositor fence before releasing old leases.
            StopWaitingForLayout();
        }

        internal void Close()
        {
            VerifyAccess();
            if (_closed) return;
            _closed = true; _ready = false;
            StopWaitingForLayout();
            _notification?.Abort(); _notification = null;
            _owner.Window.Closed -= OnOwnerClosed;
            if (ReferenceEquals(_owner._proxySource, this)) _owner._proxySource = null;
            _owner._proxySourceResources.Remove(this);
            ClearAtlas();
            var window = _window; _window = null;
            try { _hwndSource?.RemoveHook(OnNativeMessage); }
            finally
            {
                _hwndSource = null;
                if (window != null) { window.Content = null; window.Close(); }
                Updated = null; Invalidated = null;
            }
        }

        private void OnOwnerClosed(object? sender, EventArgs e) => Close();
        private IntPtr OnNativeMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0084) { handled = true; return new IntPtr(-1); }
            if (msg == 0x0021) { handled = true; return new IntPtr(3); }
            if (msg is 0x02E0 or 0x007E or 0x0082) Invalidate();
            return IntPtr.Zero;
        }
        [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
