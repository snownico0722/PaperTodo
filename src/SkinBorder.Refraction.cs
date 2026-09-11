using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private sealed class LensSlice
    {
        internal readonly DrawingVisual Visual = new();
        private LiquidRefractionEffect? _effect;
        internal LiquidRefractionEffect Effect => _effect ??= new();
        internal readonly BlurEffect Diffusion = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        internal Rect? ImageBounds;
        internal WriteableBitmap? Bitmap;
        internal LensCaptureLayout.Tile? Layout;

    }
    private ContainerVisual? _refractionVisual;
    private DrawingVisual? _opticalFinish;
    private int _finishVersion = -1;
    private Brush? _finishBorderBrush;
    private readonly System.Collections.Generic.List<LensSlice> _slices = new(1);
    private DesktopLensCapture? _capture;
    private DesktopLensCapture.Frame? _pendingFrame, _preparedFrame;
    internal bool SuppressLiveBackgroundForOpening { get; set; }
    internal bool FirstMenuRenderUsedBackground { get; private set; }
    private bool _menuRendered;

    internal void PrepareMenuBackground(DesktopLensCapture.Frame? frame, bool failed)
    {
        _preparedFrame?.Dispose(); _preparedFrame = frame;
        SuppressLiveBackgroundForOpening = failed; _menuRendered = false;
    }

    // The popup's initial scene was captured while no popup HWND existed. Upload it
    // in its first render, at the FINAL WPF placement, without a UI-thread capture/wait.
    private void PresentPreparedMenuBackground()
    {
        if (_preparedFrame == null || ActualWidth < 8 || ActualHeight < 8 ||
            PresentationSource.FromVisual(this) is not HwndSource source || source.IsDisposed) return;
        try
        {
            _captureHwnd = source.Handle;
            _captureGeometry = CaptureRegion(source.Handle);
            if (_captureGeometry != null && PresentRefraction(_preparedFrame))
            { _preparedFrame.Dispose(); _preparedFrame = null; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        { _preparedFrame?.Dispose(); _preparedFrame = null; FailRefraction(ex); }
    }

    private DesktopLensCapture.Region? CaptureRegion(IntPtr hwnd)
    {
        if (!DesktopLensCapture.TryGetBounds(hwnd, out var bounds)) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = PointToScreen(new Point());
        return new((int)Math.Floor(origin.X) - bounds.X, (int)Math.Floor(origin.Y) - bounds.Y,
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            (int)Math.Ceiling(64 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
    }

    private IntPtr _captureHwnd;
    private string? _capturedSkin, _requestedSkin;
    private DesktopLensCapture.Region? _captureGeometry, _presentedGeometry;
    private bool _refractionFailed, _evidenceFrozen, _renderingSubscribed, _cropDirty;
    private double _refractionStrength = 1;
    private double _dispersionStrength = 1;
    internal bool IsRefractionActive => _capture != null && _presentedGeometry != null;
    internal bool HasRefractionWorker => _capture != null;
    internal bool HasRefractionRenderSubscription => _renderingSubscribed;
    internal int RefractionFrameCount { get; private set; }
    internal long RefractionUploadedPixels { get; private set; }
    internal int RefractionBusyFrames { get; private set; }
    internal string? RefractionFailure { get; private set; }

    // Paint-only container before Border.Child. One background source supplies body and
    // shoulder; the parent/editor never receives an effect or a captured text bitmap.
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
        Unloaded += (_, _) => { StopRefraction(); DetachMaterialHost(); _preparedFrame?.Dispose(); _preparedFrame = null; };
        IsVisibleChanged += (_, _) => RefreshRefraction();
    }
    internal void RefreshRefraction()
    {
        if (_evidenceFrozen) return;
        if (_requestedSkin != Skin)
        { _requestedSkin = Skin; _refractionFailed = false; RefractionFailure = null; }
        var source = IsLoaded && !IsOutline && !_highContrast && PaperSkins.UsesNativeBackdrop(Skin)
            ? PresentationSource.FromVisual(this) as HwndSource : null;
        ObserveMaterialHost(source);
        var enabled = AppController.Current?.State.LiquidGlassRefraction != false;
        var active = enabled && RequestsLiveBackground && IsLoaded && IsVisible && !_highContrast &&
            IsMaterialHostVisible && ActualWidth >= 8 && ActualHeight >= 8 &&
            DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;
        if (!active)
        {
            StopRefraction();
            if (!RequestsLiveBackground || !enabled) { _refractionFailed = false; RefractionFailure = null; }
            return;
        }
        // The captured scene is independent of the recipe. Reuse it when switching
        // materials on the same HWND; only the effect/finish changes, never a blank frame.
        if (_capture != null && _captureHwnd != source!.Handle) StopRefraction();
        if (_refractionFailed) return;
        try
        {
            var hwnd = source!.Handle;
            var geometry = CaptureRegion(hwnd);
            if (geometry == null) return;
            if (_capture == null)
            {
                _capture = new DesktopLensCapture(hwnd, geometry, Dispatcher, FailRefraction, OnCaptureFrameReady);
                _captureHwnd = hwnd;
                _capturedSkin = Skin;
                RequestRefractionRender();
            }
            else _capture.SetRegion(geometry);
            _captureGeometry = geometry;
            _cropDirty = true;
            RequestRefractionRender();
            // Keep the current world-space scene during resize/reposition. A replacement
            // frame changes coverage, not the visible material or its opacity.
            if (_refractionVisual != null) UpdateRefractionCrop();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        { FailRefraction(ex); }
    }
    private void FailRefraction(Exception error)
    {
        RefractionFailure = error.Message;
        Debug.WriteLine("Glass background unavailable; keeping material fallback: " + error.Message);
        _refractionFailed = true; StopRefraction();
    }
    private void OnCaptureFrameReady() => OnRefractionRendering(null, EventArgs.Empty);

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
                if (PresentRefraction(_pendingFrame))
                { _pendingFrame.Dispose(); _pendingFrame = null; }
            }
            if (_cropDirty) UpdateRefractionCrop();
            // New pixels are presented by one coalesced callback, not queued behind input
            // and then delayed another frame. Only a busy bitmap needs a render retry.
            if (_pendingFrame != null) RequestRefractionRender();
            else UnhookCaptureRendering();
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
                    slice.ImageBounds = null;
                    if (Skin == PaperSkins.LiquidGlass)
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
        if (_refractionVisual == null)
        {
            _refractionVisual = new ContainerVisual(); _opticalFinish = new DrawingVisual();
            _finishVersion = -1; AddVisualChild(_refractionVisual);
        }
        if (_refractionVisual.Children.Count != frame.Tiles.Length + 1)
        {
            _refractionVisual.Children.Clear();
            for (var i = 0; i < frame.Tiles.Length; i++) _refractionVisual.Children.Add(_slices[i].Visual);
            _refractionVisual.Children.Add(_opticalFinish!);
        }
        EnsureGeometry(); _refractionVisual.Clip = _shape;
        _refractionVisual.Opacity = 1;
        _presentedGeometry = _captureGeometry ?? frame.Geometry; _cropDirty = true;
        UpdateRefractionCrop(); RefractionFrameCount++; RefractionFailure = null;
        return true;
    }
    private void UpdateRefractionCrop()
    {
        if (_captureHwnd == IntPtr.Zero || _presentedGeometry == null || _refractionVisual == null) return;
        if (!DesktopLensCapture.TryGetBounds(_captureHwnd, out var window)) return;
        var origin = PointToScreen(new Point()); // retain the fractional screen position
        var dpi = VisualTreeHelper.GetDpi(this);
        EnsureGeometry(); EnsureBrushes(Colors.Transparent);
        _refractionVisual.Clip = _shape;
        for (var i = 0; i < _refractionVisual.Children.Count - 1; i++)
        {
            var slice = _slices[i]; var tile = slice.Layout!; var target = tile.Target; var bounds = tile.Bounds;
            // The capture rectangle rounds out to physical pixels, but the optical
            // surface must retain its exact DIP extent at fractional desktop scaling.
            var size = RenderSize;
            slice.Visual.Offset = new Vector(target.X / dpi.DpiScaleX, target.Y / dpi.DpiScaleY);
            if (Skin != PaperSkins.LiquidGlass)
            {
                // Blur an overscanned scene BEFORE clipping the shell, so there is no
                // dark halo from transparent pixels and no blur of text or menu items.
                var imageBounds = new Rect((bounds.X - origin.X - target.X) / dpi.DpiScaleX,
                    (bounds.Y - origin.Y - target.Y) / dpi.DpiScaleY,
                    bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
                if (slice.ImageBounds != imageBounds)
                {
                    using var dc = slice.Visual.RenderOpen(); dc.DrawImage(slice.Bitmap, imageBounds);
                    slice.ImageBounds = imageBounds;
                }
                slice.Diffusion.Radius = (Skin switch
                {
                    PaperSkins.Mica => 38, PaperSkins.Acrylic => 26,
                    PaperSkins.TracingPaper => 18, _ => 12
                }) * MaterialStrength;
                slice.Visual.Effect = slice.Diffusion;
                continue;
            }
            if (!ReferenceEquals(slice.Visual.Effect, slice.Effect))
            {
                slice.Effect.Scene = new ImageBrush(slice.Bitmap) { Stretch = Stretch.Fill };
                slice.Visual.Effect = slice.Effect; slice.ImageBounds = null;
            }
            if (slice.Visual.ContentBounds.Size != size)
            { using var dc = slice.Visual.RenderOpen(); dc.DrawRectangle(Brushes.Transparent, null, new Rect(size)); }
            var metrics = GlassMetrics.For(RenderSize, _dark);
            slice.Effect.Extent = new Point4D(ActualWidth, ActualHeight, 1 / Math.Max(.001, metrics.Bezel),
                1 - metrics.Magnification * _refractionStrength * MaterialStrength);
            var limit = Math.Min(ActualWidth, ActualHeight) * .5;
            slice.Effect.Radii = new Point4D(Math.Min(limit, metrics.OpticalRadius(CornerRadius.TopLeft)),
                Math.Min(limit, metrics.OpticalRadius(CornerRadius.TopRight)),
                Math.Min(limit, metrics.OpticalRadius(CornerRadius.BottomRight)),
                Math.Min(limit, metrics.OpticalRadius(CornerRadius.BottomLeft)));
            slice.Effect.Crop = new Point4D(size.Width * dpi.DpiScaleX / bounds.Width, size.Height * dpi.DpiScaleY / bounds.Height,
                (origin.X + target.X - bounds.X) / (double)bounds.Width,
                (origin.Y + target.Y - bounds.Y) / (double)bounds.Height);
            var bend = metrics.Displacement * _refractionStrength * MaterialStrength;
            slice.Effect.Shift = new Point(bend * dpi.DpiScaleX / bounds.Width, bend * dpi.DpiScaleY / bounds.Height);
            // Never sharpen an upscaled low-resolution sample into a pixel grid on a
            // very large paper. Scattering has a half-source-texel floor in addition to bilinear sampling.
            var blur = metrics.Blur * MaterialStrength;
            slice.Effect.Scattering = new Point4D(
                Math.Max(blur * dpi.DpiScaleX / bounds.Width, .5 / tile.PixelWidth),
                Math.Max(blur * dpi.DpiScaleY / bounds.Height, .5 / tile.PixelHeight),
                1 + (metrics.Saturation - 1) * MaterialStrength, 1);
            slice.Effect.Dispersion = GlassMetrics.ChromaticSpread * _dispersionStrength;
            // The same paint as the static fallback goes ABOVE this opaque scene.
            // Otherwise it disappears under the DrawingVisual, leaving just a bright rim.
        }
        RefreshOpticalFinish();
        _cropDirty = false;
    }
    private void RefreshOpticalFinish()
    {
        if (_opticalFinish != null && (_finishVersion != _surfaceVersion || !ReferenceEquals(_finishBorderBrush, BorderBrush)))
        {
            using var dc = _opticalFinish.RenderOpen();
            dc.PushOpacity(MaterialStrength);
            dc.DrawGeometry(_fill, null, _shape);
            if (Skin == PaperSkins.TracingPaper)
                dc.DrawRectangle(_dark ? DarkFibers : LightFibers, null, new Rect(RenderSize));
            dc.DrawRectangle(_shine, null, new Rect(RenderSize));
            PaintMaterialDetails(dc);
            dc.Pop();
            // The native owner may hide this stroke, but never let a live scene cover it.
            dc.DrawGeometry(BorderBrush, null, _borderRing);
            _finishVersion = _surfaceVersion; _finishBorderBrush = BorderBrush;
        }
    }
    private Point4D LiquidTint => _dark
        ? new Point4D(.085, .10, .13, GlassMetrics.For(RenderSize, true).Tint)
        : new Point4D(.965, .98, 1, GlassMetrics.For(RenderSize, false).Tint);
    internal void SetRefractionStrengthForEvidence(double value)
    {
        _refractionStrength = value;
        foreach (var slice in _slices)
        {
            var bounds = slice.Layout?.Bounds;
            if (bounds == null) continue;
            var dpi = VisualTreeHelper.GetDpi(this);
            var metrics = GlassMetrics.For(RenderSize, _dark);
            var displacement = metrics.Displacement;
            slice.Effect.Shift = new Point(displacement * dpi.DpiScaleX * value * MaterialStrength / bounds.Value.Width,
                displacement * dpi.DpiScaleY * value * MaterialStrength / bounds.Value.Height);
            var extent = slice.Effect.Extent;
            slice.Effect.Extent = new Point4D(extent.X, extent.Y, extent.Z,
                1 - metrics.Magnification * value * MaterialStrength);
        }
    }
    internal void SetDispersionForEvidence(double value)
    {
        _dispersionStrength = value;
        foreach (var slice in _slices)
            slice.Effect.Dispersion = GlassMetrics.ChromaticSpread * value;
    }
    internal IDisposable FreezeRefractionForEvidence()
    {
        var active = _capture != null; _evidenceFrozen = true;
        _capture?.Dispose(); _capture = null;
        _pendingFrame?.Dispose(); _pendingFrame = null;
        UnhookCaptureRendering();
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
    private void UnhookCaptureRendering()
    {
        if (_renderingSubscribed) { CompositionTarget.Rendering -= OnRefractionRendering; _renderingSubscribed = false; }
    }
    private void StopRefraction()
    {
        _capture?.Dispose(); _capture = null;
        _pendingFrame?.Dispose(); _pendingFrame = null;
        UnhookCaptureRendering();
        if (_refractionVisual != null)
        { _refractionVisual.Children.Clear(); RemoveVisualChild(_refractionVisual); _refractionVisual = null; InvalidateVisual(); }
        _opticalFinish = null; _finishVersion = -1; _finishBorderBrush = null;
        _captureHwnd = IntPtr.Zero; _capturedSkin = null;
        _slices.Clear(); _captureGeometry = _presentedGeometry = null;
    }
}
