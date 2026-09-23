using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    // One immutable background scene per auxiliary surface. Menus use the prepared pre-open frame;
    // capsules take one local snapshot when they settle. No owner in this class polls the desktop.
    internal sealed class BackgroundSession
    {
        private readonly SkinBorder _owner;
        internal BackgroundSession(SkinBorder owner) => _owner = owner;

        private sealed class BackgroundScene
        {
            internal readonly DrawingVisual Visual = new();
            internal readonly BlurEffect Diffusion = new()
            {
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
            internal Rect? ImageBounds;
            internal ImageSource? Bitmap;
            internal BackgroundCaptureLayout.Scene? Layout;
            internal bool PreBlurred;
        }

        private ContainerVisual? _visual;
        private DrawingVisual? _finish;
        private int _finishVersion = -1;
        private Brush? _finishBorderBrush;
        private BackgroundScene? _scene;
        private DesktopBackgroundCapture? _capture;
        private string? _requestedSkin;
        private bool _failed;
        private bool _evidenceFrozen;
        private bool _recaptureRequested = true;
        private bool _dragSnapshotActive;

        internal ContainerVisual? Visual => _visual;
        internal DrawingVisual? SceneVisual => _scene?.Visual;
        internal ImageSource? Bitmap => _scene?.Bitmap;
        internal DesktopBackgroundCapture? Capture => _capture;
        internal bool HasPreparedFrame => _owner.IsMenu && _scene?.Layout != null;
        internal bool IsBackgroundActive => _scene?.Layout != null;
        internal bool HasBackgroundCapture => _capture != null;
        internal int BackgroundFrameCount { get; private set; }
        internal long BackgroundUploadedPixels { get; private set; }
        internal string? BackgroundFailure { get; private set; }
        internal int BackgroundProjectionCount { get; private set; }
        internal int BackgroundSceneDrawCount { get; private set; }

        internal void PrepareMenuBackground(DesktopBackgroundCapture.Frame? frame, bool failed)
        {
            Stop();
            _owner.SuppressStaticBackgroundForOpening = failed;
            _owner._menuRendered = false;
            _owner.MenuFallbackRenderCount = 0;
            _owner.FirstMenuRenderUsedBackground = false;
            if (frame == null) return;
            try
            {
                SetScene(frame.Layout, frame.Bitmap, frame.PreBlurred);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                _owner.SuppressStaticBackgroundForOpening = true;
                FailBackground(ex);
            }
        }

        internal void PresentPreparedMenuBackground()
        {
            if (!_owner.IsMenu || _scene?.Layout == null ||
                _owner.ActualWidth < 8 || _owner.ActualHeight < 8 ||
                PresentationSource.FromVisual(_owner) is not HwndSource { IsDisposed: false })
            {
                return;
            }
            Project();
        }

        internal void TranslationChanged() => Project();

        internal void GeometryChanged()
        {
            if (_dragSnapshotActive)
            {
                Project();
                return;
            }
            _recaptureRequested = true;
            RefreshBackground();
        }

        internal void ResetFailure()
        {
            _failed = false;
            BackgroundFailure = null;
        }

        private DesktopBackgroundCapture.Region? CaptureRegion(IntPtr hwnd)
        {
            if (!DesktopBackgroundCapture.TryGetBounds(hwnd, out var bounds)) return null;
            var dpi = VisualTreeHelper.GetDpi(_owner);
            if (PresentationSource.FromVisual(_owner) is not HwndSource source ||
                !MaterialSurfaceHost.TryGetScreenOrigin(_owner, source, out var origin))
            {
                return null;
            }
            return new(
                (int)Math.Floor(origin.X) - bounds.X,
                (int)Math.Floor(origin.Y) - bounds.Y,
                (int)Math.Ceiling(_owner.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(_owner.ActualHeight * dpi.DpiScaleY),
                BackgroundCaptureLayout.Padding(dpi));
        }

        internal void RefreshBackground()
        {
            if (_evidenceFrozen) return;
            if (_requestedSkin != _owner.Skin)
            {
                _requestedSkin = _owner.Skin;
                _failed = false;
                BackgroundFailure = null;
            }

            var source = _owner.IsLoaded && !_owner.IsOutline && !_owner.UseLightweightMaterial &&
                !_owner._highContrast && PaperSkins.UsesNativeBackdrop(_owner.Skin)
                    ? PresentationSource.FromVisual(_owner) as HwndSource
                    : null;
            var active = _owner.RequestsSampledBackground &&
                _owner.IsLoaded && _owner.IsVisible && !_owner._highContrast &&
                _owner.IsMaterialHostVisible && _owner.ActualWidth >= 8 && _owner.ActualHeight >= 8 &&
                DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;

            if (!active)
            {
                // A menu frame is prepared before Popup.IsOpen and can briefly be arranged before
                // SHOWWINDOW. Keep that one immutable scene until the menu is actually unloaded.
                if (_owner.IsMenu && _scene?.Layout != null && _owner.RequestsSampledBackground &&
                    !_owner.SuppressStaticBackgroundForOpening)
                {
                    return;
                }
                Stop();
                if (!_owner.RequestsSampledBackground)
                {
                    _failed = false;
                    BackgroundFailure = null;
                }
                return;
            }

            if (_dragSnapshotActive)
            {
                Project();
                return;
            }

            // Menu background is captured once before opening and never refreshed while visible.
            if (_owner.IsMenu)
            {
                Project();
                RefreshFinish();
                return;
            }

            if (_finishVersion != _owner._surfaceVersion ||
                !ReferenceEquals(_finishBorderBrush, _owner.BorderBrush))
            {
                RefreshFinish();
            }

            if (_failed || _capture != null || (_scene?.Layout != null && !_recaptureRequested))
                return;

            try
            {
                var geometry = CaptureRegion(source!.Handle);
                if (geometry == null) return;
                _recaptureRequested = false;
                _capture = new DesktopBackgroundCapture(
                    source.Handle,
                    geometry,
                    _owner.Dispatcher,
                    FailBackground,
                    OnCaptureFrameReady);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or
                System.Runtime.InteropServices.ExternalException or DllNotFoundException or
                EntryPointNotFoundException or NotSupportedException)
            {
                FailBackground(ex);
            }
        }

        private void OnCaptureFrameReady()
        {
            var capture = _capture;
            if (capture == null) return;
            try
            {
                var frame = capture.TakeLatest();
                _capture = null;
                capture.Dispose();
                if (frame != null)
                {
                    PresentFrame(frame);
                }
                if (_recaptureRequested) RefreshBackground();
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                _capture = null;
                capture.Dispose();
                FailBackground(ex);
            }
        }

        private void FailBackground(Exception error)
        {
            BackgroundFailure = error.Message;
            Debug.WriteLine("Static material background unavailable; keeping fallback: " + error.Message);
            _failed = true;
            CancelCapture();
        }

        internal bool PresentFrame(DesktopBackgroundCapture.Frame frame)
        {
            SetScene(frame.Layout, frame.Bitmap, frame.PreBlurred);
            return true;
        }

        internal void UseDragSnapshot(DesktopBackgroundCapture.Snapshot snapshot)
        {
            if (!_owner.IsCapsule || !PaperSkins.UsesSampledAuxiliary(_owner.Skin)) return;
            _dragSnapshotActive = true;
            CancelCapture();
            SetScene(snapshot.Layout, snapshot.Bitmap, snapshot.PreBlurred);
        }

        internal void EndDragSnapshot()
        {
            if (!_dragSnapshotActive) return;
            _dragSnapshotActive = false;
            ClearScene();
            _recaptureRequested = true;
            RefreshBackground();
        }

        private void SetScene(
            BackgroundCaptureLayout.Scene layout,
            ImageSource bitmap,
            bool preBlurred)
        {
            var scene = _scene ??= new BackgroundScene();
            scene.Bitmap = bitmap;
            scene.Layout = layout;
            scene.PreBlurred = preBlurred;
            scene.ImageBounds = null;
            BackgroundUploadedPixels += (long)layout.PixelWidth * layout.PixelHeight;

            if (_visual == null)
            {
                _visual = new ContainerVisual();
                _finish = new DrawingVisual();
                _visual.Children.Add(scene.Visual);
                _visual.Children.Add(_finish);
                _finishVersion = -1;
                _owner.AttachBackgroundVisual(_visual);
                _owner.InvalidateVisual();
            }

            _owner.EnsureGeometry();
            _visual.Clip = _owner._shape;
            Project();
            BackgroundFrameCount++;
            BackgroundFailure = null;
        }

        internal void Project()
        {
            if (_scene?.Layout == null || _scene.Bitmap == null || _visual == null ||
                PresentationSource.FromVisual(_owner) is not HwndSource { IsDisposed: false } source ||
                !MaterialSurfaceHost.TryGetScreenOrigin(_owner, source, out var origin))
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(_owner);
            _owner.EnsureGeometry();
            _owner.EnsureBrushes(Colors.Transparent);
            _visual.Clip = _owner._shape;

            var scene = _scene;
            var tile = scene.Layout;
            var bounds = tile.Bounds;
            var imageBounds = new Rect(
                0,
                0,
                bounds.Width / dpi.DpiScaleX,
                bounds.Height / dpi.DpiScaleY);
            scene.Visual.Offset = new Vector(
                (bounds.X - origin.X) / dpi.DpiScaleX,
                (bounds.Y - origin.Y) / dpi.DpiScaleY);

            if (scene.ImageBounds != imageBounds)
            {
                using var dc = scene.Visual.RenderOpen();
                dc.DrawImage(scene.Bitmap, imageBounds);
                BackgroundSceneDrawCount++;
                scene.ImageBounds = imageBounds;
            }

            // Drag snapshots already contain one baked light Gaussian blur. Static local/menu
            // snapshots retain the material-specific diffusion used by the current recipe.
            var diffusionRadius = scene.PreBlurred
                ? 0
                : Theme.MaterialColors.Diffusion * _owner.MaterialStrength;
            if (Math.Abs(scene.Diffusion.Radius - diffusionRadius) > 0.001)
                scene.Diffusion.Radius = diffusionRadius;
            var effect = diffusionRadius > 0 ? scene.Diffusion : null;
            if (!ReferenceEquals(scene.Visual.Effect, effect))
                scene.Visual.Effect = effect;
            RefreshFinish();
            BackgroundProjectionCount++;
        }

        internal void RefreshFinish()
        {
            if (_finish == null ||
                (_finishVersion == _owner._surfaceVersion &&
                 ReferenceEquals(_finishBorderBrush, _owner.BorderBrush)))
            {
                return;
            }

            using var dc = _finish.RenderOpen();
            _owner.PaintMaterialBase(dc);
            dc.PushOpacity(_owner.MaterialStrength);
            if (_owner.Skin == PaperSkins.TracingPaper && !_owner.UseLightweightMaterial)
                dc.DrawRectangle(_owner._dark ? DarkFibers : LightFibers, null, new Rect(_owner.RenderSize));
            dc.DrawRectangle(_owner._shine, null, new Rect(_owner.RenderSize));
            _owner.PaintMaterialDetails(dc);
            dc.Pop();
            if (_owner.DrawBaseOuterBorder)
                dc.DrawGeometry(_owner.BorderBrush, null, _owner._borderRing);
            _finishVersion = _owner._surfaceVersion;
            _finishBorderBrush = _owner.BorderBrush;
        }

        internal IDisposable FreezeBackgroundForEvidence()
        {
            var resume = _capture != null;
            _evidenceFrozen = true;
            CancelCapture();
            return new EvidenceFreeze(this, resume);
        }

        private sealed class EvidenceFreeze(BackgroundSession surface, bool resume) : IDisposable
        {
            private BackgroundSession? _surface = surface;
            public void Dispose()
            {
                if (_surface is not { } owner) return;
                _surface = null;
                owner._evidenceFrozen = false;
                if (resume) owner.RefreshBackground();
            }
        }

        private void CancelCapture()
        {
            _capture?.Dispose();
            _capture = null;
        }

        private void ClearScene()
        {
            if (_visual != null)
            {
                _visual.Children.Clear();
                _owner.DetachBackgroundVisual(_visual);
                _visual = null;
                _owner.InvalidateVisual();
            }
            _finish = null;
            _finishVersion = -1;
            _finishBorderBrush = null;
            _scene = null;
        }

        internal void Stop()
        {
            CancelCapture();
            ClearScene();
            _dragSnapshotActive = false;
            _recaptureRequested = true;
        }
    }
}
