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
    private sealed class LensSlice
    {
        internal readonly DrawingVisual Visual = new();
        internal readonly LiquidRefractionEffect Effect = new();
        internal WriteableBitmap? Bitmap;
        internal LensCaptureLayout.Tile? Layout;
        internal LensSlice() => Visual.Effect = Effect;
    }
    private ContainerVisual? _refractionVisual;
    private readonly System.Collections.Generic.List<LensSlice> _slices = new(4);
    private DesktopLensCapture? _capture;
    private DesktopLensCapture.Frame? _pendingFrame;
    private Window? _captureWindow;
    private DesktopLensCapture.Region? _captureGeometry, _presentedGeometry;
    private bool _refractionFailed, _evidenceFrozen, _renderingSubscribed, _cropDirty;
    private double _refractionStrength = 1;
    internal bool IsRefractionActive => _capture != null && _presentedGeometry != null;
    internal bool HasRefractionWorker => _capture != null;
    internal bool HasRefractionRenderSubscription => _renderingSubscribed;
    internal int RefractionFrameCount { get; private set; }
    internal long RefractionUploadedPixels { get; private set; }
    internal int RefractionBusyFrames { get; private set; }
    internal string? RefractionFailure { get; private set; }

    // Paint-only container before Border.Child. Effects belong to four small strips,
    // not the parent or the text/editor. Center pixels never pass through our sampler.
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
            var scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            var geometry = new DesktopLensCapture.Region((int)Math.Round(origin.X) - bounds.X,
                (int)Math.Round(origin.Y) - bounds.Y, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), (int)Math.Ceiling(24 * scale),
                (int)Math.Ceiling((LensDisplacement.BezelDip + 1) * scale));
            if (_capture == null)
            {
                _capture = new DesktopLensCapture(hwnd, geometry, Dispatcher, FailRefraction, RequestRefractionRender);
                _captureWindow = window;
                window!.LocationChanged += OnLensLocation;
                window.StateChanged += OnLensWindowState;
                window.Closed += OnLensWindowClosed;
                RequestRefractionRender();
            }
            else _capture.SetRegion(geometry);
            _captureGeometry = geometry;
            _cropDirty = true;
            RequestRefractionRender();
            if (_refractionVisual != null && _presentedGeometry != geometry) _refractionVisual.Opacity = 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException)
        { FailRefraction(ex); }
    }
    private void FailRefraction(Exception error)
    {
        RefractionFailure = error.Message;
        Debug.WriteLine("Liquid refraction unavailable; keeping material fallback: " + error.Message);
        _refractionFailed = true; StopRefraction();
    }
    private void RequestRefractionRender()
    {
        if (_capture == null || _renderingSubscribed) return;
        CompositionTarget.Rendering += OnRefractionRendering;
        _renderingSubscribed = true;
    }
    private void OnRefractionRendering(object? sender, EventArgs e)
    {
        if (_capture == null) return;
        try
        {
            var latest = _capture.TakeLatest();
            if (latest != null) { _pendingFrame?.Dispose(); _pendingFrame = latest; }
            if (_pendingFrame != null)
            {
                if (_captureGeometry != _pendingFrame.Geometry || PresentRefraction(_pendingFrame))
                { _pendingFrame.Dispose(); _pendingFrame = null; }
            }
            if (_cropDirty) UpdateRefractionCrop();
            // New frames wake us via one coalesced Background-priority notification.
            // An idle Rendering handler otherwise keeps WPF composition awake forever.
            // Missing/offscreen geometry also waits for a new frame or move notification,
            // not an unproductive render loop while there is nothing to present.
            if (_pendingFrame == null && _renderingSubscribed)
            {
                CompositionTarget.Rendering -= OnRefractionRendering;
                _renderingSubscribed = false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        { FailRefraction(ex); }
    }
    private unsafe bool PresentRefraction(DesktopLensCapture.Frame frame)
    {
        var locked = 0;
        try
        {
            while (_slices.Count < frame.Tiles.Length) _slices.Add(new LensSlice());
            for (var i = 0; i < frame.Tiles.Length; i++)
            {
                var slice = _slices[i]; var tile = frame.Tiles[i].Layout;
                if (slice.Bitmap == null || slice.Bitmap.PixelWidth != tile.PixelWidth || slice.Bitmap.PixelHeight != tile.PixelHeight)
                {
                    slice.Bitmap = new WriteableBitmap(tile.PixelWidth, tile.PixelHeight, 96, 96, PixelFormats.Bgr32, null);
                    slice.Effect.Scene = new ImageBrush(slice.Bitmap) { Stretch = Stretch.Fill };
                }
                // WritePixels internally waits for render-thread access. Never wait here:
                // retain only the newest frame and retry on the next composition callback.
                if (!slice.Bitmap.TryLock(new Duration(TimeSpan.Zero))) { RefractionBusyFrames++; return false; }
                locked++;
            }
            for (var i = 0; i < frame.Tiles.Length; i++)
            {
                var input = frame.Tiles[i]; var slice = _slices[i]; var bitmap = slice.Bitmap!;
                var rowBytes = input.Layout.PixelWidth * 4;
                fixed (byte* source = input.Pixels)
                {
                    for (var y = 0; y < input.Layout.PixelHeight; y++)
                        Buffer.MemoryCopy(source + y * rowBytes, (byte*)bitmap.BackBuffer + y * bitmap.BackBufferStride, rowBytes, rowBytes);
                }
                bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                RefractionUploadedPixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                slice.Layout = input.Layout;
            }
        }
        finally { for (var i = 0; i < locked; i++) _slices[i].Bitmap!.Unlock(); }
        if (_refractionVisual == null) { _refractionVisual = new ContainerVisual(); AddVisualChild(_refractionVisual); }
        if (_refractionVisual.Children.Count != frame.Tiles.Length)
        {
            _refractionVisual.Children.Clear();
            for (var i = 0; i < frame.Tiles.Length; i++) _refractionVisual.Children.Add(_slices[i].Visual);
        }
        EnsureGeometry(); _refractionVisual.Clip = _shape;
        _refractionVisual.Opacity = 1;
        _presentedGeometry = frame.Geometry; _cropDirty = true;
        UpdateRefractionCrop(); RefractionFrameCount++; RefractionFailure = null;
        return true;
    }
    private void UpdateRefractionCrop()
    {
        if (_captureWindow == null || _presentedGeometry == null || _refractionVisual == null) return;
        if (!DesktopLensCapture.TryGetBounds(new WindowInteropHelper(_captureWindow).Handle, out var window)) return;
        var geometry = _presentedGeometry;
        var dpi = VisualTreeHelper.GetDpi(this);
        for (var i = 0; i < _refractionVisual.Children.Count; i++)
        {
            var slice = _slices[i]; var tile = slice.Layout!; var target = tile.Target; var bounds = tile.Bounds;
            var size = new Size(target.Width / dpi.DpiScaleX, target.Height / dpi.DpiScaleY);
            slice.Visual.Offset = new Vector(target.X / dpi.DpiScaleX, target.Y / dpi.DpiScaleY);
            if (slice.Visual.ContentBounds.Size != size)
            { using var dc = slice.Visual.RenderOpen(); dc.DrawRectangle(Brushes.Transparent, null, new Rect(size)); }
            slice.Effect.Viewport = new Point4D(target.Width / (ActualWidth * dpi.DpiScaleX), target.Height / (ActualHeight * dpi.DpiScaleY),
                target.X / (ActualWidth * dpi.DpiScaleX), target.Y / (ActualHeight * dpi.DpiScaleY));
            var bezel = Math.Min(LensDisplacement.BezelDip, Math.Min(ActualWidth, ActualHeight) * .5);
            slice.Effect.Extent = new Point4D(ActualWidth, ActualHeight, 1 / Math.Max(.001, bezel), bezel / LensDisplacement.BezelDip);
            var limit = Math.Min(ActualWidth, ActualHeight) * .5;
            slice.Effect.Radii = new Point4D(Math.Min(limit, CornerRadius.TopLeft), Math.Min(limit, CornerRadius.TopRight),
                Math.Min(limit, CornerRadius.BottomRight), Math.Min(limit, CornerRadius.BottomLeft));
            slice.Effect.Crop = new Point4D(target.Width / (double)bounds.Width, target.Height / (double)bounds.Height,
                (window.X + geometry.OffsetX + target.X - bounds.X) / (double)bounds.Width,
                (window.Y + geometry.OffsetY + target.Y - bounds.Y) / (double)bounds.Height);
            slice.Effect.Shift = new Point(LensDisplacement.MaxShiftDip * dpi.DpiScaleX * _refractionStrength / bounds.Width,
                LensDisplacement.MaxShiftDip * dpi.DpiScaleY * _refractionStrength / bounds.Height);
            slice.Effect.Tint = LiquidTint;
            slice.Effect.Light = _lensLight.Center;
        }
        _cropDirty = false;
    }
    private Point4D LiquidTint => _dark ? new Point4D(.085, .10, .13, .32) : new Point4D(.965, .98, 1, .18);
    private void OnLensLocation(object? sender, EventArgs e)
    { _cropDirty = true; _capture?.MarkMoving(); RequestRefractionRender(); }
    private void OnLensWindowState(object? sender, EventArgs e) => RefreshRefraction();
    private void OnLensWindowClosed(object? sender, EventArgs e) => StopRefraction();

    internal void SetRefractionStrengthForEvidence(double value)
    {
        _refractionStrength = value;
        foreach (var slice in _slices)
        {
            var bounds = slice.Layout?.Bounds;
            if (bounds == null) continue;
            var dpi = VisualTreeHelper.GetDpi(this);
            slice.Effect.Shift = new Point(LensDisplacement.MaxShiftDip * dpi.DpiScaleX * value / bounds.Value.Width,
                LensDisplacement.MaxShiftDip * dpi.DpiScaleY * value / bounds.Value.Height);
        }
    }
    internal IDisposable FreezeRefractionForEvidence()
    {
        var active = _capture != null; _evidenceFrozen = true;
        _capture?.Dispose(); _capture = null;
        _pendingFrame?.Dispose(); _pendingFrame = null;
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
        if (_renderingSubscribed) { CompositionTarget.Rendering -= OnRefractionRendering; _renderingSubscribed = false; }
        if (_captureWindow == null) return;
        _captureWindow.LocationChanged -= OnLensLocation;
        _captureWindow.StateChanged -= OnLensWindowState;
        _captureWindow.Closed -= OnLensWindowClosed;
        _captureWindow = null;
    }
    private void StopRefraction()
    {
        _capture?.Dispose(); _capture = null;
        _pendingFrame?.Dispose(); _pendingFrame = null;
        UnhookCaptureWindow();
        if (_refractionVisual != null)
        { _refractionVisual.Children.Clear(); RemoveVisualChild(_refractionVisual); _refractionVisual = null; }
        _slices.Clear(); _captureGeometry = _presentedGeometry = null;
    }
}
