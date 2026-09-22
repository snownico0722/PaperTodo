using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private sealed class LensScene
    {
        internal readonly DrawingVisual Visual = new();
        internal readonly BlurEffect Diffusion = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        internal Rect? ImageBounds;
        internal WriteableBitmap? Bitmap;
        internal LensCaptureLayout.Scene? Layout;
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
        if (PresentationSource.FromVisual(this) is not HwndSource source ||
            !TryGetMaterialScreenOrigin(source, out var origin)) return null;
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
        var enabled = AppController.Current?.State.LiveBackgroundProcessing != false;
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
        var mappingChanged = scene.Layout != layout;
        var replaceBitmap = immutable || bitmap == null || bitmap.IsFrozen || mappingChanged;
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
        // A fresh WriteableBitmap still has a deferred back-to-front copy after Unlock.
        // Changing only the object identity does not publish its pixels atomically with
        // the new screen mapping. Complete private mapped textures before binding them.
        // The first subsequent SAME-region update may become mutable again; its mapping
        // is unchanged, so later content updates keep the cheap zero-wait reuse path.
        if (immutable || mappingChanged) bitmap.Freeze();
        if (replaceBitmap)
        {
            scene.Bitmap = bitmap;
            scene.ImageBounds = null;
            // Both the current effect and later recipe switches use this exact bitmap.
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
            PresentationSource.FromVisual(this) is not HwndSource { IsDisposed: false } source ||
            !TryGetMaterialScreenOrigin(source, out var origin)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        EnsureGeometry(); EnsureBrushes(Colors.Transparent);
        _refractionVisual.Clip = _shape;
        var scene = _scene; var tile = scene.Layout; var bounds = tile.Bounds;
        // The capture rectangle rounds out to physical pixels, but the optical
        // surface must retain its exact DIP extent at fractional desktop scaling.
        var size = RenderSize;
        // Blur an overscanned scene before clipping the shell, so there is no dark halo
        // from transparent pixels and no blur of text or menu items.
        var imageBounds = new Rect(0, 0, bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
        scene.Visual.Offset = new Vector((bounds.X - origin.X) / dpi.DpiScaleX, (bounds.Y - origin.Y) / dpi.DpiScaleY);
        if (scene.ImageBounds != imageBounds)
        {
            using var dc = scene.Visual.RenderOpen();
            dc.DrawImage(scene.Bitmap, imageBounds);
            RefractionSceneDrawCount++;
            scene.ImageBounds = imageBounds;
        }
        scene.Diffusion.Radius = Theme.MaterialColors.Diffusion * MaterialStrength;
        scene.Visual.Effect = scene.Diffusion;
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
