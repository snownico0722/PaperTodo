using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Owns the backdrop only. PaperWindow owns form-transition geometry and resize policy.
/// Only ordinary non-layered HWNDs enter this adapter; the bounded layered Edge hosts retain
/// their own presentation. WindowChrome owns non-client/glass integration. Do not add a parallel
/// NCCALCSIZE handler or crop the expanded HWND to an inset WPF Border.
/// </summary>
internal sealed class NativeMicaBackdrop : IDisposable
{
    private readonly Window _window;
    private readonly Func<Border?> _getChrome;
    private readonly Func<bool> _canPresent;
    private readonly Action<Brush> _setSurface;
    private readonly INativeMicaApi _native;
    private readonly WindowChrome _windowChrome;
    private readonly DependencyPropertyDescriptor _opacity;
    private HwndSource? _source;
    private Border? _observedChrome;
    private (bool Requested, bool Dark, bool Eligible, bool Rounded, string Material, bool AlwaysActive)? _applied;
    private bool _requested;
    private bool _dark;
    private string _material = MicaBackdropTypes.Mica;
    private bool _alwaysActive;
    private bool _updating;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _clearAcrylicApplied;

    internal bool IsActive { get; private set; }
    internal int LastHResult { get; private set; }
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    internal NativeMicaBackdrop(Window window, Func<Border?> getChrome,
        Func<bool> canPresent, Action<Brush> setSurface,
        INativeMicaApi? native = null)
    {
        if (window.AllowsTransparency)
            throw new ArgumentException("DWM Mica requires a non-layered WPF window.", nameof(window));
        _window = window;
        _getChrome = getChrome;
        _canPresent = canPresent;
        _setSurface = setSurface;
        _native = native ?? DwmMicaApi.Instance;
        // One frame owner, installed before HWND creation. PaperWindow's existing native
        // hit-test still owns resizing; settings/capsules must not acquire resize borders.
        _windowChrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(window, _windowChrome);
        _opacity = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        _opacity.AddValueChanged(window, OnOpacityChanged);
        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    internal void Refresh(bool requested, bool dark, string? material = null, bool? alwaysActive = null, bool force = false)
    {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _requested = requested;
        _dark = dark;
        if (material != null) _material = MicaBackdropTypes.Normalize(material);
        var wasForcedActive = IsActive && _alwaysActive;
        if (alwaysActive.HasValue) _alwaysActive = alwaysActive.Value;
        if (_updating) return;
        var chrome = _getChrome();
        ObserveChrome(chrome);
        if (_source?.CompositionTarget == null || chrome == null) return;

        var eligible = _canPresent() && _window.Opacity >= 1 && chrome.Opacity >= 1 &&
            !_native.IsLayered(_source.Handle) && chrome.IsVisible &&
            _window.WindowState != WindowState.Minimized;
        var rounded = chrome.CornerRadius.TopLeft > 0 && _window.WindowState != WindowState.Maximized;
        var state = (requested, dark, eligible, rounded, _material, _alwaysActive);
        if (!force && _applied == state) return;

        _updating = true;
        try
        {
            _applied = state;
            var hwnd = _source.Handle;
            var enable = requested && eligible && _native.IsSupported &&
                !_native.HighContrast && _native.TransparencyEnabled && _native.CompositionEnabled;
            var clear = _material == MicaBackdropTypes.ClearAcrylic;
            IsActive = false;
            LastHResult = 0;
            if (_clearAcrylicApplied && (!enable || !clear))
            {
                LastHResult = _native.SetClearAcrylic(hwnd, false, dark);
                if (LastHResult >= 0) _clearAcrylicApplied = false;
            }
            if (enable && LastHResult >= 0)
            {
                // Legacy blur-behind alpha is ONLY a fallback. Leaving it enabled when
                // restoring Mica mixes two composition recipes after the startup fade.
                LastHResult = _native.DisableAlpha(hwnd);
                if (LastHResult >= 0)
                {
                    // Full glass interferes with the accent tint. Keep a 1-DIP top strip:
                    // exact zero makes WindowChrome install a window region on every resize,
                    // disabling native corners/shadow. Both frame writers use the same margins.
                    _windowChrome.GlassFrameThickness = clear ? new Thickness(0, 1, 0, 0) : new Thickness(-1);
                    LastHResult = _native.ExtendFrame(hwnd, clear
                        ? (int)Math.Ceiling(VisualTreeHelper.GetDpi(_window).DpiScaleY) : -1);
                    if (LastHResult >= 0)
                    {
                        LastHResult = _native.SetDarkMode(hwnd, dark);
                        if (LastHResult >= 0)
                        {
                            LastHResult = _native.SetBackdrop(hwnd, MicaBackdropTypes.ToDwmBackdrop(_material));
                            if (LastHResult >= 0 && clear)
                            {
                                LastHResult = _native.SetClearAcrylic(hwnd, true, dark);
                                // Retain ownership if a later refresh fails: fallback still
                                // has to remove the accent that was previously installed.
                                _clearAcrylicApplied |= LastHResult >= 0;
                            }
                            IsActive = LastHResult >= 0;
                        }
                    }
                }
            }

            // DWM owns the outer corners and border. No SetWindowRgn: it invalidates native
            // rounding/shadow and used to turn the paper's shadow margin into a second frame.
            _native.ConfigureFrame(hwnd, IsActive && rounded);
            // WM_NCACTIVATE controls appearance only. Never synthesize WM_ACTIVATE or
            // change keyboard focus. Restore real activation when the override ends.
            if (IsActive && _alwaysActive || wasForcedActive)
                _native.SetNonClientActive(hwnd, IsActive && _alwaysActive || _window.IsActive);
            var alphaReady = false;
            if (!IsActive)
            {
                if (_clearAcrylicApplied)
                {
                    if (_native.SetClearAcrylic(hwnd, false, dark) >= 0) _clearAcrylicApplied = false;
                }
                _windowChrome.GlassFrameThickness = new Thickness(-1);
                if (_native.IsSupported) _native.SetBackdrop(hwnd, DwmMicaApi.None);
                alphaReady = _native.CompositionEnabled && _native.EnableAlpha(hwnd) >= 0;
            }
            _source.CompositionTarget.BackgroundColor = IsActive || alphaReady
                ? Color.FromArgb(0, 0, 0, 0) : ((SolidColorBrush)Theme.PaperBrush).Color;
            _window.Background = IsActive || alphaReady ? Brushes.Transparent : Theme.PaperBrush;
            _setSurface(IsActive ? GetActiveSurfaceBrush(_material, dark) : Theme.PaperBrush);
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
        Refresh(_requested, _dark, force: true);
        QueueRefresh(); // WindowChrome and startup shell styles have now been installed.
    }

    private void ObserveChrome(Border? chrome)
    {
        if (ReferenceEquals(chrome, _observedChrome)) return;
        if (_observedChrome != null)
        {
            _opacity.RemoveValueChanged(_observedChrome, OnOpacityChanged);
            _observedChrome.IsVisibleChanged -= OnVisibilityChanged;
        }
        _observedChrome = chrome;
        if (chrome != null)
        {
            _opacity.AddValueChanged(chrome, OnOpacityChanged);
            chrome.IsVisibleChanged += OnVisibilityChanged;
        }
        // Settings rebuild their root; the replacement must receive the actual surface.
        _applied = null;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Refresh(_requested, _dark);

    private void OnStateChanged(object? sender, EventArgs e) => QueueRefresh();
    private void OnOpacityChanged(object? sender, EventArgs e)
    {
        Refresh(_requested, _dark);
        // WPF can remove its temporary WS_EX_LAYERED flag after the DP callback.
        // Refresh only at the boundary, never poll or write native attributes every frame.
        if (_window.Opacity >= 1 && (_observedChrome?.Opacity ?? 1) >= 1) QueueRefresh();
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DwmMicaApi.NonClientActivateMessage && IsActive && _alwaysActive &&
            _window.WindowState != WindowState.Minimized)
        {
            _native.SetNonClientActive(hwnd, true);
            handled = true;
            // TRUE lets Windows complete the actual activation change to another window.
            return new IntPtr(1);
        }
        if (message is 0x031E /* WM_DWMCOMPOSITIONCHANGED */ or 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */ or
            0x031A /* WM_THEMECHANGED */ or 0x001A /* WM_SETTINGCHANGE */ or 0x02E0 /* WM_DPICHANGED */)
            QueueRefresh();
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
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
        _opacity.RemoveValueChanged(_window, OnOpacityChanged);
        if (_observedChrome != null)
        {
            _opacity.RemoveValueChanged(_observedChrome, OnOpacityChanged);
            _observedChrome.IsVisibleChanged -= OnVisibilityChanged;
        }
        if (_source != null && !_source.IsDisposed)
        {
            _source.RemoveHook(WindowMessage);
            if (IsActive && _alwaysActive) _native.SetNonClientActive(_source.Handle, _window.IsActive);
            if (_clearAcrylicApplied) _native.SetClearAcrylic(_source.Handle, false, _dark);
            if (_native.IsSupported) _native.SetBackdrop(_source.Handle, DwmMicaApi.None);
        }
        IsActive = false;
        _source = null;
        _observedChrome = null;
    }

    internal static Brush GetActiveSurfaceBrush(string? material, bool dark)
    {
        if (material == MicaBackdropTypes.Acrylic)
        {
            // Windows 11 DWM native Acrylic includes a built-in heavy noise texture (grain)
            // and dark luminosity tint. A semi-transparent tint wash filters out the gritty
            // noise and lifts the darkness, producing a clean, luminous frosted glass.
            // Clear Acrylic instead sets its native tint directly, with no WPF wash.
            // Text and controls stay fully opaque in both modes.
            var color = dark ? Color.FromArgb(144, 32, 33, 40) : Color.FromArgb(152, 255, 255, 255);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Brushes.Transparent;
    }
}
