using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One-shot local desktop capture for layered material surfaces. A capture exists only long enough
/// to publish one immutable frame; there is no polling loop, motion wake-up, or change detection.
/// </summary>
internal sealed class DesktopBackgroundCapture : IDisposable
{
    internal const uint CaptureRasterOperation = 0x00CC0020; // SRCCOPY, never CAPTUREBLT.
    internal sealed record Region(int OffsetX, int OffsetY, int Width, int Height, int Padding);

    internal sealed class Frame : IDisposable
    {
        internal BackgroundCaptureLayout.Scene Layout { get; }
        internal BitmapSource Bitmap { get; }
        internal bool PreBlurred { get; }

        internal Frame(BackgroundCaptureLayout.Scene layout, BitmapSource bitmap, bool preBlurred = false)
        {
            Layout = layout;
            Bitmap = bitmap;
            PreBlurred = preBlurred;
        }

        // Kept for focused raster tests; BitmapSource copies the supplied pixels immediately.
        internal Frame(BackgroundCaptureLayout.Scene layout, byte[] pixels)
            : this(layout, CreateBitmap(layout.PixelWidth, layout.PixelHeight, pixels), false)
        {
        }

        public void Dispose()
        {
            // Immutable BitmapSource owns its copied pixels; no pooled frame lifetime remains.
        }
    }

    internal sealed record Snapshot(BackgroundCaptureLayout.Scene Layout, BitmapSource Bitmap, bool PreBlurred);

    private readonly IntPtr _hwnd;
    private readonly uint _oldAffinity;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Exception> _failed;
    private readonly Action? _frameReady;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _captureTask;
    private readonly object _gate = new();
    private Frame? _latest;
    private int _disposed;

    internal IntPtr WindowHandle => _hwnd;
    internal bool IsStopped => Volatile.Read(ref _disposed) != 0;
    internal Task Completion => _captureTask;

    internal DesktopBackgroundCapture(
        IntPtr hwnd,
        Region region,
        Dispatcher dispatcher,
        Action<Exception> failed,
        Action? frameReady = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Background exclusion requires Windows 10 2004 or later.");
        if (!GetWindowDisplayAffinity(hwnd, out _oldAffinity) || !SetWindowDisplayAffinity(hwnd, 0x11))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclude the surface from its own background.");

        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _failed = failed;
        _frameReady = frameReady;
        _captureTask = Task.Run(() => CaptureOnce(region, _cancel.Token));
    }

    internal Frame? TakeLatest()
    {
        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    private void CaptureOnce(Region region, CancellationToken token)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            token.ThrowIfCancellationRequested();
            DwmFlush();
            if (!TryGetBounds(_hwnd, out var window) || IsIconic(_hwnd) || !IsWindowVisible(_hwnd))
                return;
            if (!GetWindowDisplayAffinity(_hwnd, out var affinity) || affinity != 0x11)
                throw new InvalidOperationException("Background exclusion changed before the static snapshot.");

            var layout = BackgroundCaptureLayout.Create(window, region, DesktopBounds);
            if (layout == null) return;
            using var surface = new CaptureSurface();
            surface.Capture(layout.Bounds, layout.PixelWidth, layout.PixelHeight);
            GdiFlush();
            token.ThrowIfCancellationRequested();
            Publish(new Frame(layout, surface.CreateBitmap()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException or ArgumentException)
        {
            if (!IsStopped && !_dispatcher.HasShutdownStarted)
            {
                try
                {
                    _dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => { if (!IsStopped) _failed(ex); }));
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher shutdown won.
                }
            }
        }
        finally
        {
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            NotifyCompleted();
        }
    }

    private void Publish(Frame frame)
    {
        lock (_gate)
        {
            if (IsStopped) return;
            _latest = frame;
        }
    }

    private void NotifyCompleted()
    {
        if (_frameReady == null || IsStopped || _dispatcher.HasShutdownStarted) return;
        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() => { if (!IsStopped) _frameReady(); }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown won.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        lock (_gate) _latest = null;
        if (GetWindowDisplayAffinity(_hwnd, out var current) && current == 0x11)
            SetWindowDisplayAffinity(_hwnd, _oldAffinity);
        _ = _captureTask.ContinueWith(
            _ => _cancel.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static Task<Frame?> PreparePopupAsync(Int32Rect requested, CancellationToken token) =>
        Task.Run(() =>
        {
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                token.ThrowIfCancellationRequested();
                var geometry = new Region(0, 0, requested.Width, requested.Height, 0);
                var layout = BackgroundCaptureLayout.Create(requested, geometry, DesktopBounds);
                if (layout == null) return null;
                using var surface = new CaptureSurface();
                surface.Capture(layout.Bounds, layout.PixelWidth, layout.PixelHeight);
                GdiFlush();
                token.ThrowIfCancellationRequested();
                return new Frame(layout, surface.CreateBitmap());
            }
            finally
            {
                if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            }
        }, token);

    // One drag snapshot covers the entire virtual desktop at half width/height. The blur is baked
    // into this immutable texture once; movement only changes the screen-space crop.
    internal static Task<Snapshot?> PrepareDragAsync(IntPtr excludeHwnd, CancellationToken token) =>
        Task.Run(() =>
        {
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            uint previousAffinity = 0;
            var affinityChanged = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (excludeHwnd != IntPtr.Zero)
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
                        !GetWindowDisplayAffinity(excludeHwnd, out previousAffinity) ||
                        !SetWindowDisplayAffinity(excludeHwnd, 0x11))
                    {
                        return null;
                    }
                    affinityChanged = true;
                    DwmFlush();
                }

                var desktop = DesktopBounds;
                if (desktop.Width <= 0 || desktop.Height <= 0) return null;
                var width = Math.Max(1, (desktop.Width + 1) / 2);
                var height = Math.Max(1, (desktop.Height + 1) / 2);
                using var surface = new CaptureSurface();
                surface.Capture(desktop, width, height);
                GdiFlush();
                token.ThrowIfCancellationRequested();

                var pixels = surface.ReadPixels();
                ApplyLightGaussianBlur(pixels, width, height);
                token.ThrowIfCancellationRequested();
                return new Snapshot(
                    new BackgroundCaptureLayout.Scene(desktop, width, height),
                    CreateBitmap(width, height, pixels),
                    PreBlurred: true);
            }
            finally
            {
                if (affinityChanged &&
                    GetWindowDisplayAffinity(excludeHwnd, out var current) &&
                    current == 0x11)
                {
                    SetWindowDisplayAffinity(excludeHwnd, previousAffinity);
                }
                if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            }
        }, token);

    private static void ApplyLightGaussianBlur(byte[] pixels, int width, int height)
    {
        if (width < 3 || height < 3) return;
        // Radius 2 / sigma 1.1 is deliberately light. Combined with 50% downsampling it removes
        // text-level detail without turning the drag surface into a featureless color block.
        const int radius = 2;
        const double sigma = 1.1;
        var kernel = new double[radius * 2 + 1];
        var sum = 0d;
        for (var i = -radius; i <= radius; i++)
        {
            var value = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = value;
            sum += value;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var temp = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dst = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var value = 0d;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, width - 1);
                    value += pixels[(y * width + sx) * 4 + c] * kernel[k + radius];
                }
                temp[dst + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
            temp[dst + 3] = pixels[dst + 3];
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dst = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var value = 0d;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(y + k, 0, height - 1);
                    value += temp[(sy * width + x) * 4 + c] * kernel[k + radius];
                }
                pixels[dst + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
        }
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    internal static Int32Rect DesktopBounds =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    internal static bool TryGetBounds(IntPtr hwnd, out Int32Rect bounds)
    {
        if (GetWindowRect(hwnd, out var r))
        {
            bounds = new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }
        bounds = default;
        return false;
    }

    internal static bool IsVisible(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    internal static uint ReadAffinity(IntPtr hwnd) =>
        GetWindowDisplayAffinity(hwnd, out var value) ? value : uint.MaxValue;

    private sealed class CaptureSurface : IDisposable
    {
        private IntPtr _screen;
        private IntPtr _dc;
        private IntPtr _bitmap;
        private IntPtr _previous;
        private IntPtr _bits;
        private int _width;
        private int _height;

        internal CaptureSurface()
        {
            _screen = GetDC(IntPtr.Zero);
            if (_screen != IntPtr.Zero) _dc = CreateCompatibleDC(_screen);
            if (_dc == IntPtr.Zero)
            {
                Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        internal void Capture(Int32Rect source, int targetWidth, int targetHeight)
        {
            EnsureBitmap(targetWidth, targetHeight);
            var desktop = DesktopBounds;
            if (source.X < desktop.X || source.Y < desktop.Y ||
                source.X + source.Width > desktop.X + desktop.Width ||
                source.Y + source.Height > desktop.Y + desktop.Height)
            {
                Marshal.Copy(new byte[checked(_width * _height * 4)], 0, _bits, checked(_width * _height * 4));
            }

            var ok = source.Width == _width && source.Height == _height
                ? BitBlt(_dc, 0, 0, _width, _height, _screen, source.X, source.Y, CaptureRasterOperation)
                : StretchBlt(_dc, 0, 0, _width, _height, _screen,
                    source.X, source.Y, source.Width, source.Height, CaptureRasterOperation);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal byte[] ReadPixels()
        {
            var bytes = new byte[checked(_width * _height * 4)];
            Marshal.Copy(_bits, bytes, 0, bytes.Length);
            return bytes;
        }

        internal BitmapSource CreateBitmap() => DesktopBackgroundCapture.CreateBitmap(_width, _height, ReadPixels());

        private void EnsureBitmap(int width, int height)
        {
            if (_bitmap != IntPtr.Zero && _width == width && _height == height) return;
            ReleaseBitmap();
            var info = new BitmapInfo
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                Bits = 32
            };
            _bitmap = CreateDIBSection(_screen, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = SelectObject(_dc, _bitmap);
            if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
            {
                DeleteObject(_bitmap);
                _bitmap = _bits = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _width = width;
            _height = height;
            SetStretchBltMode(_dc, 4 /* HALFTONE */);
            SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
        }

        private void ReleaseBitmap()
        {
            if (_bitmap == IntPtr.Zero) return;
            SelectObject(_dc, _previous);
            DeleteObject(_bitmap);
            _bitmap = _previous = _bits = IntPtr.Zero;
            _width = _height = 0;
        }

        public void Dispose()
        {
            ReleaseBitmap();
            if (_dc != IntPtr.Zero)
            {
                DeleteDC(_dc);
                _dc = IntPtr.Zero;
            }
            if (_screen != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, _screen);
                _screen = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort Bits;
        internal uint Compression;
        internal uint ImageSize;
        internal int XPels;
        internal int YPels;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(
        IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
