using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Native backdrop for ordinary, non-layered paper/settings HWNDs only. The bounded
/// layered Edge hosts must not use this adapter: DWM paints the entire HWND capacity.
/// A WPF window's AllowsTransparency is immutable after HWND creation, so the controller
/// chooses a native-window session at startup rather than replacing live editors.
/// </summary>
internal sealed class NativeMicaBackdrop : IDisposable
{
    private readonly Window _window;
    private readonly Func<Border?> _getChrome;
    private readonly Func<bool> _canPresent;
    private readonly Action<Brush> _setSurface;
    private readonly Action _changed;
    private readonly INativeMicaApi _native;
    private readonly DependencyPropertyDescriptor _opacity;
    private HwndSource? _source;
    private Border? _observedChrome;
    private NativeMicaRegion? _region;
    private (bool Requested, bool Dark, bool Eligible)? _applied;
    private bool _requested;
    private bool _dark;
    private bool _updating;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _alphaReady;
    private bool _regionOwned;

    internal bool IsActive { get; private set; }
    internal int LastHResult { get; private set; }
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    internal NativeMicaBackdrop(Window window, Func<Border?> getChrome,
        Func<bool> canPresent, Action<Brush> setSurface, Action changed,
        INativeMicaApi? native = null)
    {
        if (window.AllowsTransparency)
            throw new ArgumentException("DWM Mica requires a non-layered WPF window.", nameof(window));
        _window = window;
        _getChrome = getChrome;
        _canPresent = canPresent;
        _setSurface = setSurface;
        _changed = changed;
        _native = native ?? DwmMicaApi.Instance;
        _opacity = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        _opacity.AddValueChanged(window, OnOpacityChanged);
        window.SourceInitialized += OnSourceInitialized;
        window.LayoutUpdated += OnLayoutUpdated;
        window.Closed += OnClosed;
    }

    internal void Refresh(bool requested, bool dark, bool force = false)
    {
        _requested = requested;
        _dark = dark;
        if (_disposed || _updating) return;
        _window.Dispatcher.VerifyAccess();
        var chrome = _getChrome();
        ObserveChrome(chrome);
        if (_source?.CompositionTarget == null || chrome == null) return;

        var eligible = _canPresent() && _window.Opacity >= 1 && chrome.Opacity >= 1 &&
            !_native.IsLayered(_source.Handle) &&
            chrome.IsVisible && _window.WindowState != WindowState.Minimized;
        var state = (requested, dark, eligible);
        var regionFailed = false;
        if (!force && _applied == state)
        {
            if (!IsActive || UpdateRegion(chrome)) return;
            // A failed resize clip must not retain a native slab over stale geometry.
            // Stay on fallback until an explicit preference/source refresh retries it.
            regionFailed = true;
        }

        _updating = true;
        try
        {
            _applied = state;
            var wasActive = IsActive;
            var hwnd = _source.Handle;
            var enable = !regionFailed && requested && eligible && _native.IsSupported &&
                !_native.HighContrast && _native.TransparencyEnabled && _native.CompositionEnabled;
            IsActive = false;
            LastHResult = regionFailed ? unchecked((int)0x80004005) : 0;
            if (enable)
            {
                // Prepare glass + alpha before making any application brush transparent.
                LastHResult = _native.ExtendFrame(hwnd, true);
                if (LastHResult >= 0)
                {
                    LastHResult = _native.SetDarkMode(hwnd, dark);
                    if (LastHResult >= 0)
                    {
                        LastHResult = _native.SetBackdrop(hwnd, DwmMicaApi.MainWindow);
                        if (LastHResult >= 0)
                        {
                            IsActive = UpdateRegion(chrome);
                        }
                    }
                }
            }

            if (!IsActive)
            {
                // Remove the full-HWND material BEFORE releasing its clip. Collapse/opacity
                // animations then use the existing WPF pixels, never an expanded Mica slab.
                var disabled = !_native.IsSupported || _native.SetBackdrop(hwnd, DwmMicaApi.None) >= 0;
                // If DWM rejected disable, keep its last owned clip rather than expose a
                // full-capacity native background during the WPF fallback animation.
                if (disabled) ClearRegion();
                _alphaReady = _native.CompositionEnabled && _native.EnableAlpha(hwnd) >= 0;
            }
            _source.CompositionTarget.BackgroundColor = IsActive || _alphaReady
                ? Colors.Transparent : ((SolidColorBrush)Theme.PaperBrush).Color;
            _window.Background = IsActive || _alphaReady ? Brushes.Transparent : Theme.PaperBrush;
            _setSurface(IsActive ? Brushes.Transparent : Theme.PaperBrush);
            if (wasActive != IsActive) _changed();
            if (enable && !IsActive)
                Debug.WriteLine($"Native Mica fallback: HWND={hwnd}, HRESULT=0x{LastHResult:X8}");
        }
        finally { _updating = false; }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        if (_source == null) return;
        _source.AddHook(WindowMessage);
        // WindowStyle.None may still have a resize non-client frame in a non-layered HWND.
        // Recalculate it once; subsequent sizing remains in PaperWindow's existing owner.
        _native.RefreshFrame(_source.Handle);
        Refresh(_requested, _dark, force: true);
    }

    private void ObserveChrome(Border? chrome)
    {
        if (ReferenceEquals(chrome, _observedChrome)) return;
        if (_observedChrome != null) _opacity.RemoveValueChanged(_observedChrome, OnOpacityChanged);
        _observedChrome = chrome;
        if (chrome != null) _opacity.AddValueChanged(chrome, OnOpacityChanged);
        // Settings rebuild their root; do not leave the replacement chrome opaque.
        _applied = null;
        _region = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh(_requested, _dark);
    private void OnOpacityChanged(object? sender, EventArgs e)
    {
        Refresh(_requested, _dark);
        // WPF can remove its uniform-opacity WS_EX_LAYERED flag after the DP callback.
        // A single boundary refresh sees the settled style; no per-frame polling/timer.
        if (_window.Opacity >= 1 && (_observedChrome?.Opacity ?? 1) >= 1)
            QueueRefresh();
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private bool UpdateRegion(Border chrome)
    {
        if (_source == null || chrome.ActualWidth <= 0 || chrome.ActualHeight <= 0) return false;
        var origin = chrome.TranslatePoint(new Point(), _window);
        var dpi = VisualTreeHelper.GetDpi(_window);
        var region = NativeMicaRegion.FromLayout(origin, chrome.RenderSize, chrome.CornerRadius.TopLeft, dpi);
        if (_region == region) return true;
        // Cache before SetWindowRgn: it synchronously raises WM_WINDOWPOSCHANGED.
        var previous = _region;
        _region = region;
        if (_native.SetRegion(_source.Handle, region))
        {
            _regionOwned = true;
            return true;
        }
        _region = previous;
        LastHResult = unchecked((int)0x80004005);
        return false;
    }

    private void ClearRegion()
    {
        if (!_regionOwned || _source == null) return;
        if (_native.ClearRegion(_source.Handle))
        {
            _regionOwned = false;
            _region = null;
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0083 /* WM_NCCALCSIZE */ && _window.WindowStyle == WindowStyle.None)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message is 0x031E /* WM_DWMCOMPOSITIONCHANGED */ or 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */ or
            0x031A /* WM_THEMECHANGED */ or 0x001A /* WM_SETTINGCHANGE */ or 0x02E0 /* WM_DPICHANGED */)
        {
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || _disposed || _window.Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            if (!_disposed) Refresh(_requested, _dark, force: true);
        }), DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _opacity.RemoveValueChanged(_window, OnOpacityChanged);
        if (_observedChrome != null) _opacity.RemoveValueChanged(_observedChrome, OnOpacityChanged);
        if (_source != null && !_source.IsDisposed) _source.RemoveHook(WindowMessage);
        _source = null;
        _observedChrome = null;
        // HWND destruction releases its system backdrop and any system-owned HRGN.
    }
}

internal readonly record struct NativeMicaRegion(int Left, int Top, int Right, int Bottom, int EllipseWidth, int EllipseHeight)
{
    internal static NativeMicaRegion FromLayout(Point origin, Size size, double radius, DpiScale dpi)
    {
        var left = (int)Math.Round(origin.X * dpi.DpiScaleX);
        var top = (int)Math.Round(origin.Y * dpi.DpiScaleY);
        var right = (int)Math.Round((origin.X + size.Width) * dpi.DpiScaleX);
        var bottom = (int)Math.Round((origin.Y + size.Height) * dpi.DpiScaleY);
        radius = Math.Clamp(radius, 0, Math.Min(size.Width, size.Height) / 2);
        return new(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom),
            (int)Math.Round(radius * 2 * dpi.DpiScaleX), (int)Math.Round(radius * 2 * dpi.DpiScaleY));
    }
}
