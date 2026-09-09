using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private DrawingVisual? _refractionVisual;
    private LiquidRefractionEffect? _refractionEffect;
    private DesktopLensCapture? _capture;
    private Window? _captureWindow;
    private WriteableBitmap? _captureBitmap;
    private Int32Rect _captureBounds;
    private DesktopLensCapture.Region? _captureGeometry;
    private bool _refractionFailed, _evidenceFrozen;
    private (Size Size, CornerRadius Radius, double X, double Y)? _mapKey;
    internal bool IsRefractionActive => _capture != null && _captureBitmap != null;
    internal bool HasRefractionWorker => _capture != null;
    internal int RefractionFrameCount { get; private set; }
    internal string? RefractionFailure { get; private set; }

    // One paint-only visual before Border.Child. No content HWND, logical child, layout
    // owner, hit target or effect on text. Other skins keep this visual absent.
    protected override int VisualChildrenCount => base.VisualChildrenCount + (_refractionVisual == null ? 0 : 1);
    protected override Visual GetVisualChild(int index)
    {
        if (_refractionVisual == null) return base.GetVisualChild(index);
        return index == 0 ? _refractionVisual : base.GetVisualChild(index - 1);
    }
    private void InitializeRefraction()
    {
        SizeChanged += (_, _) => { _refractionFailed = false; RefreshRefraction(); };
        Loaded += (_, _) => RefreshRefraction();
        Unloaded += (_, _) => StopRefraction();
        IsVisibleChanged += (_, _) => RefreshRefraction();
    }
    internal void RefreshRefraction()
    {
        if (_evidenceFrozen) return;
        var window = Window.GetWindow(this) as PaperWindow;
        var enabled = AppController.Current?.State.LiquidGlassRefraction != false;
        var active = enabled && !IsOutline && !IsCapsule && IsLoaded && IsVisible && !_highContrast &&
            Skin == PaperSkins.LiquidGlass && window is { IsVisible: true, IsNativeMicaEffective: true, HasExpandedPaperSurface: true } &&
            window.WindowState != WindowState.Minimized && window.Opacity >= 1 && Opacity >= 1 && ActualWidth >= 8 && ActualHeight >= 8;
        if (!active)
        {
            StopRefraction();
            if (Skin != PaperSkins.LiquidGlass || !enabled) { _refractionFailed = false; RefractionFailure = null; }
            return;
        }
        if (_refractionFailed) return;
        try
        {
            var hwnd = new WindowInteropHelper(window!).Handle;
            if (!DesktopLensCapture.TryGetBounds(hwnd, out var bounds)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = PointToScreen(new Point());
            var geometry = new DesktopLensCapture.Region((int)Math.Round(origin.X) - bounds.X,
                (int)Math.Round(origin.Y) - bounds.Y, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), (int)Math.Ceiling(64 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
            _refractionEffect ??= new LiquidRefractionEffect();
            var key = (RenderSize, CornerRadius, dpi.DpiScaleX, dpi.DpiScaleY);
            if (_mapKey != key)
            {
                var map = new ImageBrush(LensDisplacement.Create(RenderSize, CornerRadius, dpi));
                map.Freeze(); _refractionEffect.Map = map; _mapKey = key;
            }
            _refractionEffect.Tint = _dark ? new Point4D(.065, .085, .115, .65) : new Point4D(.965, .98, 1, .58);
            if (_capture == null)
            {
                _capture = new DesktopLensCapture(hwnd, geometry, Dispatcher, PresentRefraction, FailRefraction);
                _captureWindow = window;
                window!.LocationChanged += OnLensLocation;
                window.StateChanged += OnLensWindowState;
                window.Closed += OnLensWindowClosed;
            }
            else _capture.SetRegion(geometry);
            _captureGeometry = geometry;
            UpdateRefractionCrop();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            FailRefraction(ex);
        }
    }
    private void FailRefraction(Exception error)
    {
        RefractionFailure = error.Message;
        Debug.WriteLine("Liquid refraction unavailable; keeping material fallback: " + error.Message);
        _refractionFailed = true; StopRefraction();
    }
    private void PresentRefraction(DesktopLensCapture.Frame frame)
    {
        if (_capture == null || _refractionEffect == null || _captureGeometry != frame.Geometry) return;
        var bounds = frame.Bounds;
        var changed = _captureBitmap == null || _captureBitmap.PixelWidth != bounds.Width || _captureBitmap.PixelHeight != bounds.Height;
        if (changed)
            _captureBitmap = new WriteableBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null);
        _captureBitmap!.WritePixels(new Int32Rect(0, 0, bounds.Width, bounds.Height), frame.Pixels, bounds.Width * 4, 0);
        _captureBounds = bounds;
        if (_refractionVisual == null)
        {
            _refractionVisual = new DrawingVisual { Effect = _refractionEffect };
            AddVisualChild(_refractionVisual);
            changed = true;
        }
        EnsureGeometry(); _refractionVisual.Clip = _shape;
        if (changed || _refractionVisual.ContentBounds.Size != RenderSize)
        {
            using var dc = _refractionVisual.RenderOpen();
            dc.DrawImage(_captureBitmap, new Rect(RenderSize));
        }
        UpdateRefractionCrop(); RefractionFrameCount++; RefractionFailure = null;
    }
    private void UpdateRefractionCrop()
    {
        if (_captureWindow == null || _captureBitmap == null || _refractionEffect == null || _captureGeometry == null) return;
        var hwnd = new WindowInteropHelper(_captureWindow).Handle;
        if (!DesktopLensCapture.TryGetBounds(hwnd, out var window)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _refractionEffect.Crop = new Point4D(_captureGeometry.Width / (double)_captureBounds.Width,
            _captureGeometry.Height / (double)_captureBounds.Height,
            (window.X + _captureGeometry.OffsetX - _captureBounds.X) / (double)_captureBounds.Width,
            (window.Y + _captureGeometry.OffsetY - _captureBounds.Y) / (double)_captureBounds.Height);
        _refractionEffect.Shift = new Point(LensDisplacement.MaxShiftDip * dpi.DpiScaleX / _captureBounds.Width,
            LensDisplacement.MaxShiftDip * dpi.DpiScaleY / _captureBounds.Height);
    }
    private void OnLensLocation(object? sender, EventArgs e) => UpdateRefractionCrop();
    private void OnLensWindowState(object? sender, EventArgs e) => RefreshRefraction();
    private void OnLensWindowClosed(object? sender, EventArgs e) => StopRefraction();

    // Freeze the last genuine background frame while restoring screenshot visibility.
    // The opted-in evidence harness uses this; the capture loop never does.
    internal IDisposable FreezeRefractionForEvidence()
    {
        var active = _capture != null;
        _evidenceFrozen = true;
        _capture?.Dispose(); _capture = null;
        UnhookCaptureWindow();
        return new EvidenceFreeze(this, active);
    }
    private sealed class EvidenceFreeze(SkinBorder surface, bool resume) : IDisposable
    {
        private SkinBorder? _surface = surface;
        public void Dispose()
        {
            if (_surface is not { } owner) return;
            _surface = null; owner._evidenceFrozen = false;
            if (resume) owner.RefreshRefraction();
        }
    }
    private void UnhookCaptureWindow()
    {
        if (_captureWindow == null) return;
        _captureWindow.LocationChanged -= OnLensLocation;
        _captureWindow.StateChanged -= OnLensWindowState;
        _captureWindow.Closed -= OnLensWindowClosed;
        _captureWindow = null;
    }
    private void StopRefraction()
    {
        _capture?.Dispose(); _capture = null;
        UnhookCaptureWindow();
        if (_refractionVisual != null)
        {
            using (var dc = _refractionVisual.RenderOpen()) { }
            RemoveVisualChild(_refractionVisual); _refractionVisual = null;
        }
        _captureBitmap = null; _captureGeometry = null; _mapKey = null;
    }
}
