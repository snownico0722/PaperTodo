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
    private sealed class LensScene
    {
        internal readonly DrawingVisual Visual = new();
        private LiquidRefractionEffect? _effect;
        internal LiquidRefractionEffect Effect => _effect ??= new();
        internal readonly BlurEffect Diffusion = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        internal Rect? ImageBounds;
        internal Size? LiquidSize;
        internal WriteableBitmap? Bitmap;
        internal ImageBrush? SceneBrush;
        internal LensCaptureLayout.Scene? Layout;
        internal (Size Size, CornerRadius Radius, double DpiX, double DpiY, int Width, int Height,
            int PixelsX, int PixelsY, bool Dark, double Strength, double Refraction, double Dispersion)? OpticalKey;

    }
    private ContainerVisual? _refractionVisual;
    private DrawingVisual? _opticalFinish;
    private int _finishVersion = -1;
    private Brush? _finishBorderBrush;
    private LensScene? _scene;
    private DesktopLensCapture? _capture;
    private DesktopLensCapture.Frame? _pendingFrame, _preparedFrame;
    internal bool SuppressLiveBackgroundForOpening { get; set; }
    internal bool FirstMenuRenderUsedBackground { get; private set; }
    private bool _menuRendered;
    internal int MenuFallbackRenderCount { get; private set; }
    internal int RefractionProjectionCount { get; private set; }
    internal int RefractionSceneDrawCount { get; private set; }

    internal void PrepareMenuBackground(DesktopLensCapture.Frame? frame, bool failed)
    {
        _preparedFrame?.Dispose(); _preparedFrame = frame;
        StopRefraction();
        SuppressLiveBackgroundForOpening = failed; _menuRendered = false; MenuFallbackRenderCount = 0;
        FirstMenuRenderUsedBackground = false;
        if (frame == null) return;
        try
        {
            // Build the bitmap/visual graph BEFORE IsOpen. A scene object created in
            // OnRender is not proof that its bitmap reached the compositor that frame.
            // Freezing the first bitmap publishes immutable pixels, not a pending
            // WriteableBitmap back-to-front copy. Only final positioning waits for HWND.
            if (UploadRefraction(frame, immutable: true))
            { frame.Dispose(); _preparedFrame = null; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            _preparedFrame?.Dispose(); _preparedFrame = null;
            SuppressLiveBackgroundForOpening = true; FailRefraction(ex);
        }
    }

    // The scene is already uploaded before the popup exists. Arrange/Loaded only
    // project it at FINAL WPF placement; no desktop readback or first-upload delay.
    private void PresentPreparedMenuBackground()
    {
        if (!IsMenu || _capture != null || (_preparedFrame == null && _scene?.Layout == null) ||
            ActualWidth < 8 || ActualHeight < 8 ||
            PresentationSource.FromVisual(this) is not HwndSource source || source.IsDisposed) return;
        try
        {
            _captureHwnd = source.Handle;
            _captureGeometry = CaptureRegion(source.Handle);
            if (_captureGeometry == null) return;
            if (_preparedFrame != null && UploadRefraction(_preparedFrame, immutable: true))
            { _preparedFrame.Dispose(); _preparedFrame = null; }
            UpdateRefractionCrop();
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
            (int)Math.Min(1024, Math.Ceiling(256 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY))));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PresentPreparedMenuBackground();
    }

    private IntPtr _captureHwnd;
    private string? _requestedSkin;
    private DesktopLensCapture.Region? _captureGeometry;
    private bool _refractionFailed, _evidenceFrozen, _renderingSubscribed, _cropDirty;
    private double _refractionStrength = 1;
    private double _dispersionStrength = 1;
    internal bool IsRefractionActive => _capture != null && _scene?.Layout != null;
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
        SizeChanged += (_, _) => { _refractionFailed = false; PresentPreparedMenuBackground(); RefreshRefraction(); };
        Loaded += (_, _) => { PresentPreparedMenuBackground(); RefreshRefraction(); };
        Unloaded += (_, _) => { StopRefraction(); DetachMaterialHost(); _preparedFrame?.Dispose(); _preparedFrame = null; };
        IsVisibleChanged += (_, _) => RefreshRefraction();
    }
    internal void RefreshRefraction()
    {
        if (_evidenceFrozen) return;
        if (_requestedSkin != Skin)
        { _requestedSkin = Skin; _refractionFailed = false; RefractionFailure = null; }
        var source = IsLoaded && !IsOutline && !UseLightweightMaterial && !_highContrast && PaperSkins.UsesNativeBackdrop(Skin)
            ? PresentationSource.FromVisual(this) as HwndSource : null;
        ObserveMaterialHost(source);
        var enabled = AppController.Current?.State.LiquidGlassRefraction != false;
        var active = enabled && RequestsLiveBackground && IsLoaded && IsVisible && !_highContrast &&
            IsMaterialHostVisible && ActualWidth >= 8 && ActualHeight >= 8 &&
            DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;
        if (!active)
        {
            // OnRender records drawing commands during arrange, BEFORE Loaded/SHOWWINDOW.
            // A first OnRender therefore does not mean the popup has become visible.
            // Retain the primed scene until capture can take over; Unloaded releases a
            // cancelled opening. Once a worker exists, normal hide/opacity teardown wins.
            if (IsMenu && _capture == null && _refractionVisual != null && enabled && RequestsLiveBackground &&
                !_highContrast && !UseLightweightMaterial && !SuppressLiveBackgroundForOpening &&
                DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled)
                return;
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
                RequestRefractionRender();
            }
            else _capture.SetRegion(geometry);
            _captureGeometry = geometry;
            _cropDirty = true;
            RequestRefractionRender();
            // Keep the current world-space scene during resize/reposition. A replacement
            // frame changes coverage, not the visible material or its opacity.
            // The rendering callback consumes this geometry once, independent of readback.
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
    // Notifications only schedule a render. Never upload/reproject in a second,
    // competing dispatcher clock or read the desktop from the render callback.
    private void OnCaptureFrameReady() => RequestRefractionRender();

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
    private bool PresentRefraction(DesktopLensCapture.Frame frame) => UploadRefraction(frame, immutable: false);

    private unsafe bool UploadRefraction(DesktopLensCapture.Frame frame, bool immutable)
    {
        var scene = _scene ??= new LensScene();
        var layout = frame.Layout;
        var bitmap = scene.Bitmap;
        // A bitmap's pixels and its world-space mapping form one version. Updating an
        // in-use bitmap for a SAME-SIZED but recentered scene lets the render thread
        // observe new pixels with old crop constants. Replace on any mapping change;
        // stationary samples still reuse their bitmap and zero-wait lock.
        var replaceBitmap = immutable || bitmap == null || bitmap.IsFrozen || scene.Layout != layout;
        if (replaceBitmap)
            bitmap = new WriteableBitmap(layout.PixelWidth, layout.PixelHeight, 96, 96, PixelFormats.Bgr32, null);
        // Prepare the replacement privately. A busy render thread must not expose an
        // empty bitmap or invalidate the currently visible scene while we retry.
        if (!bitmap!.TryLock(new Duration(TimeSpan.Zero))) { RefractionBusyFrames++; return false; }
        try
        {
            var rowBytes = checked(layout.PixelWidth * 4);
            fixed (byte* source = frame.Pixels)
            {
                if (bitmap.BackBufferStride == rowBytes)
                {
                    var bytes = checked(rowBytes * layout.PixelHeight);
                    Buffer.MemoryCopy(source, (void*)bitmap.BackBuffer, bytes, bytes);
                }
                else for (var y = 0; y < layout.PixelHeight; y++)
                    Buffer.MemoryCopy(source + y * rowBytes, (byte*)bitmap.BackBuffer + y * bitmap.BackBufferStride, rowBytes, rowBytes);
            }
            bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }
        finally { bitmap.Unlock(); }
        if (immutable) bitmap.Freeze();
        if (replaceBitmap)
        {
            scene.Bitmap = bitmap;
            scene.ImageBounds = null;
            // Both the current effect and later recipe switches use this exact bitmap.
            scene.SceneBrush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            if (Skin == PaperSkins.LiquidGlass) scene.Effect.Scene = scene.SceneBrush;
        }
        scene.Layout = layout;
        RefractionUploadedPixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
        if (_refractionVisual == null)
        {
            _refractionVisual = new ContainerVisual(); _opticalFinish = new DrawingVisual();
            _refractionVisual.Children.Add(scene.Visual);
            _refractionVisual.Children.Add(_opticalFinish);
            _finishVersion = -1; AddVisualChild(_refractionVisual); InvalidateVisual();
        }
        EnsureGeometry(); _refractionVisual.Clip = _shape;
        _cropDirty = true;
        UpdateRefractionCrop(); RefractionFrameCount++; RefractionFailure = null;
        return true;
    }
    private void UpdateRefractionCrop()
    {
        if (_captureHwnd == IntPtr.Zero || _scene?.Layout == null || _refractionVisual == null ||
            PresentationSource.FromVisual(this) is not HwndSource { IsDisposed: false }) return;
        var origin = PointToScreen(new Point()); // retain the fractional screen position
        var dpi = VisualTreeHelper.GetDpi(this);
        EnsureGeometry(); EnsureBrushes(Colors.Transparent);
        _refractionVisual.Clip = _shape;
        var scene = _scene; var tile = scene.Layout; var bounds = tile.Bounds;
        // The capture rectangle rounds out to physical pixels, but the optical
        // surface must retain its exact DIP extent at fractional desktop scaling.
        var size = RenderSize;
        if (Skin != PaperSkins.LiquidGlass)
        {
            // Blur an overscanned scene BEFORE clipping the shell, so there is no
            // dark halo from transparent pixels and no blur of text or menu items.
            // Keep drawing/effect coordinates fixed in the cached scene. Pure motion
            // changes only the visual offset, rather than re-recording a large blur.
            var imageBounds = new Rect(0, 0, bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
            scene.Visual.Offset = new Vector((bounds.X - origin.X) / dpi.DpiScaleX, (bounds.Y - origin.Y) / dpi.DpiScaleY);
            if (scene.ImageBounds != imageBounds)
            {
                using var dc = scene.Visual.RenderOpen(); dc.DrawImage(scene.Bitmap, imageBounds);
                RefractionSceneDrawCount++;
                scene.ImageBounds = imageBounds;
            }
            scene.Diffusion.Radius = Theme.MaterialColors.Diffusion * MaterialStrength;
            scene.OpticalKey = null; scene.LiquidSize = null;
            scene.Visual.Effect = scene.Diffusion;
        }
        else
        {
            scene.Visual.Offset = new Vector();
            if (!ReferenceEquals(scene.Visual.Effect, scene.Effect))
            {
                scene.Effect.Scene = scene.SceneBrush!;
                scene.Visual.Effect = scene.Effect; scene.ImageBounds = null;
            }
            if (scene.LiquidSize != size)
            {
                using var dc = scene.Visual.RenderOpen(); dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
                scene.LiquidSize = size; RefractionSceneDrawCount++;
            }
            scene.Effect.Crop = new Point4D(size.Width * dpi.DpiScaleX / bounds.Width, size.Height * dpi.DpiScaleY / bounds.Height,
                (origin.X - bounds.X) / bounds.Width, (origin.Y - bounds.Y) / bounds.Height);
            var opticalKey = (size, CornerRadius, dpi.DpiScaleX, dpi.DpiScaleY, bounds.Width, bounds.Height, tile.PixelWidth, tile.PixelHeight,
                _dark, MaterialStrength, _refractionStrength, _dispersionStrength);
            if (scene.OpticalKey != opticalKey)
            {
                scene.OpticalKey = opticalKey;
                var metrics = GlassMetrics.For(RenderSize, _dark);
                scene.Effect.Extent = new Point4D(ActualWidth, ActualHeight, 1 / Math.Max(.001, metrics.Bezel),
                    1 - metrics.Magnification * _refractionStrength * MaterialStrength);
                var limit = Math.Min(ActualWidth, ActualHeight) * .5;
                scene.Effect.Radii = new Point4D(Math.Min(limit, metrics.OpticalRadius(CornerRadius.TopLeft)),
                    Math.Min(limit, metrics.OpticalRadius(CornerRadius.TopRight)),
                    Math.Min(limit, metrics.OpticalRadius(CornerRadius.BottomRight)),
                    Math.Min(limit, metrics.OpticalRadius(CornerRadius.BottomLeft)));
                var bend = metrics.Displacement * _refractionStrength * MaterialStrength;
                scene.Effect.Shift = new Point(bend * dpi.DpiScaleX / bounds.Width, bend * dpi.DpiScaleY / bounds.Height);
                // Never sharpen an upscaled low-resolution sample into a pixel grid on a
                // very large paper. Scattering has a half-source-texel floor in addition to bilinear sampling.
                var blur = metrics.Blur * MaterialStrength;
                scene.Effect.Scattering = new Point4D(
                    Math.Max(blur * dpi.DpiScaleX / bounds.Width, .5 / tile.PixelWidth),
                    Math.Max(blur * dpi.DpiScaleY / bounds.Height, .5 / tile.PixelHeight),
                    1 + (metrics.Saturation - 1) * MaterialStrength, 1);
                scene.Effect.Dispersion = GlassMetrics.ChromaticSpread * _dispersionStrength;
                // The same paint as the static fallback goes ABOVE this opaque scene.
                // Otherwise it disappears under the DrawingVisual, leaving just a bright rim.
            }
        }
        RefreshOpticalFinish();
        _cropDirty = false;
        RefractionProjectionCount++;
    }
    private void RefreshOpticalFinish()
    {
        if (_opticalFinish != null && (_finishVersion != _surfaceVersion || !ReferenceEquals(_finishBorderBrush, BorderBrush)))
        {
            using var dc = _opticalFinish.RenderOpen();
            PaintMaterialBase(dc);
            dc.PushOpacity(MaterialStrength);
            if (Skin == PaperSkins.TracingPaper && !UseLightweightMaterial)
                dc.DrawRectangle(_dark ? DarkFibers : LightFibers, null, new Rect(RenderSize));
            dc.DrawRectangle(_shine, null, new Rect(RenderSize));
            PaintMaterialDetails(dc);
            dc.Pop();
            // The native owner may hide this stroke, but never let a live scene cover it.
            dc.DrawGeometry(BorderBrush, null, _borderRing);
            _finishVersion = _surfaceVersion; _finishBorderBrush = BorderBrush;
        }
    }
    private Point4D LiquidTint
    {
        get
        {
            var color = Theme.MaterialColors.Surface;
            return new(color.R / 255d, color.G / 255d, color.B / 255d, GlassMetrics.For(RenderSize, _dark).Tint);
        }
    }
    internal void SetRefractionStrengthForEvidence(double value)
    {
        _refractionStrength = value;
        if (_scene?.Layout is not { } layout) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var metrics = GlassMetrics.For(RenderSize, _dark);
        _scene.Effect.Shift = new Point(metrics.Displacement * dpi.DpiScaleX * value * MaterialStrength / layout.Bounds.Width,
            metrics.Displacement * dpi.DpiScaleY * value * MaterialStrength / layout.Bounds.Height);
        var extent = _scene.Effect.Extent;
        _scene.Effect.Extent = new Point4D(extent.X, extent.Y, extent.Z,
            1 - metrics.Magnification * value * MaterialStrength);
    }
    internal void SetDispersionForEvidence(double value)
    {
        _dispersionStrength = value;
        if (_scene != null) _scene.Effect.Dispersion = GlassMetrics.ChromaticSpread * value;
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
        _captureHwnd = IntPtr.Zero;
        _scene = null; _captureGeometry = null;
    }
}
