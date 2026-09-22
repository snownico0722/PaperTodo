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
    // Optional resource owner, created only for sampled auxiliary surfaces (or an explicit
    // test/evidence session). Never owns the editor, window, input or layout. Native main
    // windows and ordinary paper pay none of its observation/capture lifetime costs.
    internal sealed class BackgroundSession
    {
        private readonly SkinBorder _owner;
        internal BackgroundSession(SkinBorder owner) => _owner = owner;
        internal ContainerVisual? Visual => _visual;
        internal DrawingVisual? SceneVisual => _scene?.Visual;
        internal WriteableBitmap? Bitmap => _scene?.Bitmap;
        internal DesktopBackgroundCapture? Capture => _capture;
        internal bool HasPreparedFrame => _preparedFrame != null;
        internal void GeometryChanged()
        {
            _cropDirty = true;
            _capture?.MarkMoving();
            RequestRender();
        }
        internal void ResetFailure() { _failed = false; BackgroundFailure = null; }
        private sealed class BackgroundScene
        {
            internal readonly DrawingVisual Visual = new();
            internal readonly BlurEffect Diffusion = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            internal Rect? ImageBounds;
            internal WriteableBitmap? Bitmap;
            internal BackgroundCaptureLayout.Scene? Layout;
        }
        private ContainerVisual? _visual;
        private DrawingVisual? _finish;
        private int _finishVersion = -1;
        private Brush? _finishBorderBrush;
        private BackgroundScene? _scene;
        private DesktopBackgroundCapture? _capture;
        private DesktopBackgroundCapture.Frame? _pendingFrame, _preparedFrame;
        internal int BackgroundProjectionCount { get; private set; }
        internal int BackgroundSceneDrawCount { get; private set; }

        internal void PrepareMenuBackground(DesktopBackgroundCapture.Frame? frame, bool failed)
        {
            Stop();
            _preparedFrame = frame;
            _owner.SuppressLiveBackgroundForOpening = failed; _owner._menuRendered = false; _owner.MenuFallbackRenderCount = 0;
            _owner.FirstMenuRenderUsedBackground = false;
            if (frame == null) return;
            try
            {
                // Build the bitmap/visual graph BEFORE IsOpen. A scene object created in
                // OnRender is not proof that its bitmap reached the compositor that frame.
                // Freezing the first bitmap publishes immutable pixels, not a pending
                // WriteableBitmap back-to-front copy. Only final positioning waits for HWND.
                if (UploadFrame(frame, immutable: true))
                { frame.Dispose(); _preparedFrame = null; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                _preparedFrame?.Dispose(); _preparedFrame = null;
                _owner.SuppressLiveBackgroundForOpening = true; FailBackground(ex);
            }
        }

        // The scene is already uploaded before the popup exists. Arrange/Loaded only
        // project it at FINAL WPF placement; no desktop readback or first-upload delay.
        internal void PresentPreparedMenuBackground()
        {
            if (!_owner.IsMenu || _capture != null || (_preparedFrame == null && _scene?.Layout == null) ||
                _owner.ActualWidth < 8 || _owner.ActualHeight < 8 ||
                PresentationSource.FromVisual(_owner) is not HwndSource source || source.IsDisposed) return;
            try
            {
                if (CaptureRegion(source.Handle) == null) return;
                if (_preparedFrame != null && UploadFrame(_preparedFrame, immutable: true))
                { _preparedFrame.Dispose(); _preparedFrame = null; }
                Project();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
            { _preparedFrame?.Dispose(); _preparedFrame = null; FailBackground(ex); }
        }

        private DesktopBackgroundCapture.Region? CaptureRegion(IntPtr hwnd)
        {
            if (!DesktopBackgroundCapture.TryGetBounds(hwnd, out var bounds)) return null;
            var dpi = VisualTreeHelper.GetDpi(_owner);
            if (PresentationSource.FromVisual(_owner) is not HwndSource source ||
                !MaterialSurfaceHost.TryGetScreenOrigin(_owner, source, out var origin)) return null;
            return new((int)Math.Floor(origin.X) - bounds.X, (int)Math.Floor(origin.Y) - bounds.Y,
                (int)Math.Ceiling(_owner.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(_owner.ActualHeight * dpi.DpiScaleY),
                BackgroundCaptureLayout.Padding(_owner.IsMenu, dpi));
        }


        private string? _requestedSkin;
        private bool _failed, _evidenceFrozen, _renderingSubscribed, _cropDirty;
        internal bool IsBackgroundActive => _capture != null && _scene?.Layout != null;
        internal bool HasBackgroundWorker => _capture != null;
        internal bool HasBackgroundRenderSubscription => _renderingSubscribed;
        internal int BackgroundFrameCount { get; private set; }
        internal long BackgroundUploadedPixels { get; private set; }
        internal int BackgroundBusyFrames { get; private set; }
        internal string? BackgroundFailure { get; private set; }

        internal void RefreshBackground()
        {
            if (_evidenceFrozen) return;
            if (_requestedSkin != _owner.Skin)
            { _requestedSkin = _owner.Skin; _failed = false; BackgroundFailure = null; }
            var source = _owner.IsLoaded && !_owner.IsOutline && !_owner.UseLightweightMaterial && !_owner._highContrast && PaperSkins.UsesNativeBackdrop(_owner.Skin)
                ? PresentationSource.FromVisual(_owner) as HwndSource : null;
            var enabled = AppController.Current?.State.LiveBackgroundProcessing != false;
            var active = enabled && _owner.RequestsLiveBackground && _owner.IsLoaded && _owner.IsVisible && !_owner._highContrast &&
                _owner.IsMaterialHostVisible && _owner.ActualWidth >= 8 && _owner.ActualHeight >= 8 &&
                DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;
            if (!active)
            {
                // OnRender records drawing commands during arrange, BEFORE Loaded/SHOWWINDOW.
                // A first OnRender therefore does not mean the popup has become visible.
                // Retain the primed scene until capture can take over; Unloaded releases a
                // cancelled opening. Once a worker exists, normal hide/opacity teardown wins.
                if (_owner.IsMenu && _capture == null && _visual != null && enabled && _owner.RequestsLiveBackground &&
                    !_owner._highContrast && !_owner.UseLightweightMaterial && !_owner.SuppressLiveBackgroundForOpening &&
                    DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled)
                    return;
                Stop();
                if (!_owner.RequestsLiveBackground || !enabled) { _failed = false; BackgroundFailure = null; }
                return;
            }
            // The captured scene is independent of the recipe. Reuse it when switching
            // materials on the same HWND; only the effect/finish changes, never a blank frame.
            if (_capture != null && _capture.WindowHandle != source!.Handle) Stop();
            if (_failed) return;
            try
            {
                var hwnd = source!.Handle;
                var geometry = CaptureRegion(hwnd);
                if (geometry == null) return;
                if (_capture == null)
                {
                    _capture = new DesktopBackgroundCapture(hwnd, geometry, _owner.Dispatcher, FailBackground, OnCaptureFrameReady);
                    _cropDirty = true;
                }
                else if (_capture.SetRegion(geometry)) _cropDirty = true;
                if (_finishVersion != _owner._surfaceVersion || !ReferenceEquals(_finishBorderBrush, _owner.BorderBrush))
                    _cropDirty = true;
                if (_cropDirty) RequestRender();
                // Keep the current world-space scene during resize/reposition. A replacement
                // frame changes coverage, not the visible material or its opacity.
                // The rendering callback consumes this geometry once, independent of readback.
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            { FailBackground(ex); }
        }
        private void FailBackground(Exception error)
        {
            BackgroundFailure = error.Message;
            Debug.WriteLine("Glass background unavailable; keeping material fallback: " + error.Message);
            _failed = true; Stop();
        }
        // Notifications only schedule a render. Never upload/reproject in a second,
        // competing dispatcher clock or read the desktop from the render callback.
        private void OnCaptureFrameReady() => RequestRender();

        private void RequestRender()
        {
            if (_capture == null || _renderingSubscribed) return;
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
        }
        private void OnRendering(object? sender, EventArgs e)
        {
            if (_capture == null) return;
            try
            {
                var latest = _capture.TakeLatest();
                if (latest != null) { _pendingFrame?.Dispose(); _pendingFrame = latest; }
                if (_pendingFrame != null)
                {
                    if (PresentFrame(_pendingFrame))
                    { _pendingFrame.Dispose(); _pendingFrame = null; }
                }
                if (_cropDirty) Project();
                // New pixels are presented by one coalesced callback, not queued behind input
                // and then delayed another frame. Only a busy bitmap needs a render retry.
                if (_pendingFrame != null) RequestRender();
                else UnhookRendering();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
            { FailBackground(ex); }
        }
        internal bool PresentFrame(DesktopBackgroundCapture.Frame frame) => UploadFrame(frame, immutable: false);

        private unsafe bool UploadFrame(DesktopBackgroundCapture.Frame frame, bool immutable)
        {
            var scene = _scene ??= new BackgroundScene();
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
            if (!bitmap!.TryLock(new Duration(TimeSpan.Zero))) { BackgroundBusyFrames++; return false; }
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
            BackgroundUploadedPixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
            if (_visual == null)
            {
                _visual = new ContainerVisual(); _finish = new DrawingVisual();
                _visual.Children.Add(scene.Visual);
                _visual.Children.Add(_finish);
                _finishVersion = -1; _owner.AttachBackgroundVisual(_visual); _owner.InvalidateVisual();
            }
            _owner.EnsureGeometry(); _visual.Clip = _owner._shape;
            _cropDirty = true;
            Project(); BackgroundFrameCount++; BackgroundFailure = null;
            return true;
        }
        internal void Project()
        {
            if (_scene?.Layout == null || _visual == null ||
                PresentationSource.FromVisual(_owner) is not HwndSource { IsDisposed: false } source ||
                !MaterialSurfaceHost.TryGetScreenOrigin(_owner, source, out var origin)) return;
            var dpi = VisualTreeHelper.GetDpi(_owner);
            _owner.EnsureGeometry(); _owner.EnsureBrushes(Colors.Transparent);
            _visual.Clip = _owner._shape;
            var scene = _scene; var tile = scene.Layout; var bounds = tile.Bounds;
            // Blur an overscanned scene before clipping the shell, so there is no dark halo
            // from transparent pixels and no blur of text or menu items.
            var imageBounds = new Rect(0, 0, bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
            scene.Visual.Offset = new Vector((bounds.X - origin.X) / dpi.DpiScaleX, (bounds.Y - origin.Y) / dpi.DpiScaleY);
            if (scene.ImageBounds != imageBounds)
            {
                using var dc = scene.Visual.RenderOpen();
                dc.DrawImage(scene.Bitmap, imageBounds);
                BackgroundSceneDrawCount++;
                scene.ImageBounds = imageBounds;
            }
            scene.Diffusion.Radius = Theme.MaterialColors.Diffusion * _owner.MaterialStrength;
            scene.Visual.Effect = scene.Diffusion;
            RefreshFinish();
            _cropDirty = false;
            BackgroundProjectionCount++;
        }
        internal void RefreshFinish()
        {
            if (_finish != null && (_finishVersion != _owner._surfaceVersion || !ReferenceEquals(_finishBorderBrush, _owner.BorderBrush)))
            {
                using var dc = _finish.RenderOpen();
                _owner.PaintMaterialBase(dc);
                dc.PushOpacity(_owner.MaterialStrength);
                if (_owner.Skin == PaperSkins.TracingPaper && !_owner.UseLightweightMaterial)
                    dc.DrawRectangle(_owner._dark ? DarkFibers : LightFibers, null, new Rect(_owner.RenderSize));
                dc.DrawRectangle(_owner._shine, null, new Rect(_owner.RenderSize));
                _owner.PaintMaterialDetails(dc);
                dc.Pop();
                // The native owner may hide this stroke, but never let a live scene cover it.
                dc.DrawGeometry(_owner.BorderBrush, null, _owner._borderRing);
                _finishVersion = _owner._surfaceVersion; _finishBorderBrush = _owner.BorderBrush;
            }
        }
        internal IDisposable FreezeBackgroundForEvidence()
        {
            var active = _capture != null; _evidenceFrozen = true;
            _capture?.Dispose(); _capture = null;
            _pendingFrame?.Dispose(); _pendingFrame = null;
            UnhookRendering();
            return new EvidenceFreeze(this, active);
        }
        private sealed class EvidenceFreeze(BackgroundSession surface, bool resume) : IDisposable
        {
            private BackgroundSession? _surface = surface;
            public void Dispose()
            {
                if (_surface is not { } owner) return;
                _surface = null; owner._evidenceFrozen = false;
                if (resume) owner.RefreshBackground();
            }
        }
        private void UnhookRendering()
        {
            if (_renderingSubscribed) { CompositionTarget.Rendering -= OnRendering; _renderingSubscribed = false; }
        }
        internal void Stop()
        {
            _preparedFrame?.Dispose(); _preparedFrame = null;
            _capture?.Dispose(); _capture = null;
            _pendingFrame?.Dispose(); _pendingFrame = null;
            UnhookRendering();
            if (_visual != null)
            { _visual.Children.Clear(); _owner.DetachBackgroundVisual(_visual); _visual = null; _owner.InvalidateVisual(); }
            _finish = null; _finishVersion = -1; _finishBorderBrush = null;
            _scene = null;
        }
    }
}
