using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>Bounded local background sampling. Worker owns GDI, dispatcher owns WPF.
/// One latest-frame mailbox and one coalesced presentation notification; no UI-thread capture/wait.
/// Samples are neither saved nor uploaded. A changed exclusion lease stops capture.</summary>
internal sealed class DesktopLensCapture : IDisposable
{
    internal sealed record Region(int OffsetX, int OffsetY, int Width, int Height, int Padding);
    internal sealed record Tile(LensCaptureLayout.Tile Layout, byte[] Pixels);
    internal sealed class Frame : IDisposable
    {
        internal readonly Tile[] Tiles;
        internal readonly Region Geometry;
        private int _disposed;
        internal Frame(Tile[] tiles, Region geometry) => (Tiles, Geometry) = (tiles, geometry);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var tile in Tiles) ArrayPool<byte>.Shared.Return(tile.Pixels, clearArray: true);
        }
    }
    private readonly IntPtr _hwnd;
    private readonly uint _oldAffinity;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Exception> _failed;
    private readonly Action? _frameReady;
    private int _notificationQueued;
    private readonly CancellationTokenSource _cancel = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Task _worker;
    private Region _region;
    private Frame? _latest;
    private int _disposed;
    private long _motionUntil, _captures, _published, _sampledPixels;
    private static long ClockMilliseconds => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    internal const int ActiveInterval = 100;
    internal const int MovingInterval = ActiveInterval;
    internal static int CaptureInterval(bool moving, int quiet) => !moving && quiet >= 10 ? 250 : ActiveInterval;
    internal bool IsStopped => Volatile.Read(ref _disposed) != 0;
    internal long CaptureCount => Interlocked.Read(ref _captures);
    internal long PublishedCount => Interlocked.Read(ref _published);
    internal long SampledPixels => Interlocked.Read(ref _sampledPixels);
    internal Frame? TakeLatest() => Interlocked.Exchange(ref _latest, null);

    internal DesktopLensCapture(IntPtr hwnd, Region region, Dispatcher dispatcher, Action<Exception> failed, Action? frameReady = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Background exclusion requires Windows 10 2004 or later.");
        if (!GetWindowDisplayAffinity(hwnd, out _oldAffinity) || !SetWindowDisplayAffinity(hwnd, 0x11))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclude the lens from its own background.");
        _hwnd = hwnd; _region = region; _dispatcher = dispatcher; _failed = failed; _frameReady = frameReady;
        try
        {
            _worker = Task.Factory.StartNew(CaptureLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            SetWindowDisplayAffinity(hwnd, _oldAffinity); _cancel.Dispose(); _wake.Dispose(); throw;
        }
    }
    internal void SetRegion(Region region)
    {
        if (_region == region || IsStopped) return;
        Volatile.Write(ref _region, region); MarkMoving();
    }
    internal void MarkMoving()
    {
        if (IsStopped) return;
        Interlocked.Exchange(ref _motionUntil, ClockMilliseconds + 160);
        _wake.Set();
    }
    private void CaptureLoop()
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var surfaces = new CaptureSurface[1];
        try
        {
            var waits = new WaitHandle[] { _cancel.Token.WaitHandle, _wake };
            var quiet = 0; var next = ClockMilliseconds; var driverPauseUntil = 0L; var lastSample = next - ActiveInterval;
            LensCaptureLayout.Tile[]? oldLayout = null;
            Region? oldGeometry = null;
            DwmFlush(); // Exclusion is established before the first background sample.
            while (!_cancel.IsCancellationRequested)
            {
                var now = ClockMilliseconds;
                var moving = now < Interlocked.Read(ref _motionUntil);
                // Movement may wake idle detection, but NEVER turns it into high-rate readback.
                // WPF reprojects the larger scene independently between these samples.
                if (moving) next = Math.Max(Math.Max(driverPauseUntil, lastSample + ActiveInterval), Math.Min(next, now));
                if (now < next)
                {
                    if (WaitHandle.WaitAny(waits, (int)Math.Min(125, next - now)) == 0) break;
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                lastSample = now;
                var geometry = Volatile.Read(ref _region);
                next = now + ActiveInterval;
                if (!TryGetBounds(_hwnd, out var window) || IsIconic(_hwnd) || !IsWindowVisible(_hwnd)) continue;
                if (!GetWindowDisplayAffinity(_hwnd, out var affinity) || affinity != 0x11)
                    throw new InvalidOperationException("Background exclusion changed; stopping to prevent recursive feedback.");
                var desktop = DesktopBounds;
                var layout = LensCaptureLayout.Create(window, geometry, desktop);
                if (layout.Length == 0) continue;
                var changed = oldGeometry != geometry || oldLayout == null || !layout.AsSpan().SequenceEqual(oldLayout);
                for (var i = 0; i < layout.Length; i++)
                {
                    surfaces[i] ??= new CaptureSurface();
                    surfaces[i].Capture(layout[i]);
                    Interlocked.Add(ref _sampledPixels, (long)layout[i].PixelWidth * layout[i].PixelHeight);
                }
                GdiFlush(); // One readback/completion, never four separately sampled edges.
                for (var i = 0; i < layout.Length; i++) changed |= surfaces[i].RememberChangedPixels();
                Interlocked.Increment(ref _captures);
                if (_cancel.IsCancellationRequested) break;
                if (changed)
                {
                    var tiles = new Tile[layout.Length]; var count = 0;
                    try
                    {
                        for (; count < tiles.Length; count++) tiles[count] = new Tile(layout[count], surfaces[count].Snapshot());
                        var frame = new Frame(tiles, geometry);
                        Interlocked.Exchange(ref _latest, frame)?.Dispose();
                        Interlocked.Increment(ref _published);
                        NotifyFrameReady();
                    }
                    catch
                    {
                        for (var i = 0; i < count; i++) ArrayPool<byte>.Shared.Return(tiles[i].Pixels, clearArray: true);
                        throw;
                    }
                    quiet = 0; oldGeometry = geometry; oldLayout = layout;
                }
                else quiet++;
                // Unchanged pixels never enter WPF. A still desktop gradually drops to
                // low-rate change detection; motion wakes it. Expensive GDI drivers also
                // get breathing room rather than a continuous GPU-readback loop.
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Readback remains low-rate even during dragging. Slow drivers still get
                // backpressure; motion and the shader use the retained scene at render cadence.
                var interval = CaptureInterval(moving, quiet);
                var completed = ClockMilliseconds;
                driverPauseUntil = completed + Math.Max(1, (long)(elapsed * .5));
                next = Math.Max(driverPauseUntil, completed + (long)(interval - elapsed));
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException or ArgumentException)
        {
            if (!_cancel.IsCancellationRequested && !_dispatcher.HasShutdownStarted)
            {
                try { _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!IsStopped) _failed(ex); })); }
                catch (InvalidOperationException) { /* Dispatcher shutdown won. */ }
            }
        }
        finally
        {
            foreach (var surface in surfaces) surface?.Dispose();
            if (IsStopped) Interlocked.Exchange(ref _latest, null)?.Dispose();
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
        }
    }
    private void NotifyFrameReady()
    {
        if (_frameReady == null || IsStopped || _dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Interlocked.Exchange(ref _notificationQueued, 0);
                if (!IsStopped) _frameReady();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _notificationQueued, 0); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        Interlocked.Exchange(ref _latest, null)?.Dispose();
        if (GetWindowDisplayAffinity(_hwnd, out var current) && current == 0x11)
            SetWindowDisplayAffinity(_hwnd, _oldAffinity);
        _ = _worker.ContinueWith(_ => { _cancel.Dispose(); _wake.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal static Int32Rect DesktopBounds => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    internal static Task<Frame?> PreparePopupAsync(Int32Rect requested, bool liquid, CancellationToken token) => Task.Run(() =>
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            token.ThrowIfCancellationRequested();
            if (liquid) LiquidRefractionEffect.PrepareBytecode();
            var geometry = new Region(0, 0, requested.Width, requested.Height, 0);
            var layout = LensCaptureLayout.Create(requested, geometry, DesktopBounds);
            if (layout.Length == 0) return null;
            using var surface = new CaptureSurface();
            surface.Capture(layout[0]); GdiFlush(); surface.RememberChangedPixels();
            token.ThrowIfCancellationRequested();
            return new Frame([new Tile(layout[0], surface.Snapshot())], geometry);
        }
        finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
    }, token);

    internal static bool TryGetBounds(IntPtr hwnd, out Int32Rect bounds)
    {
        if (GetWindowRect(hwnd, out var r)) { bounds = new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
        bounds = default; return false;
    }
    internal static bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd);
    internal static uint ReadAffinity(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out var value) ? value : uint.MaxValue;

    private sealed class CaptureSurface : IDisposable
    {
        private IntPtr _screen, _dc, _bitmap, _previous, _bits;
        private int _width, _height;
        private byte[] _last = [];
        internal CaptureSurface()
        {
            _screen = GetDC(IntPtr.Zero);
            if (_screen != IntPtr.Zero) _dc = CreateCompatibleDC(_screen);
            if (_dc == IntPtr.Zero) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        internal void Capture(LensCaptureLayout.Tile tile)
        {
            if (_width != tile.PixelWidth || _height != tile.PixelHeight)
            {
                ReleaseBitmap();
                var info = new BitmapInfo { Size = 40, Width = tile.PixelWidth, Height = -tile.PixelHeight, Planes = 1, Bits = 32 };
                _bitmap = CreateDIBSection(_screen, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _previous = SelectObject(_dc, _bitmap);
                if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
                { DeleteObject(_bitmap); _bitmap = _bits = IntPtr.Zero; throw new Win32Exception(Marshal.GetLastWin32Error()); }
                _width = tile.PixelWidth; _height = tile.PixelHeight;
                SetStretchBltMode(_dc, 4 /* HALFTONE */);
                SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
            }
            var b = tile.Bounds;
            // The last aligned cell can extend a few pixels beyond the virtual desktop.
            // Clear it before GDI clips the screen source; stale DIB pixels must not leak.
            var desktop = DesktopBounds;
            if (b.X < desktop.X || b.Y < desktop.Y || b.X + b.Width > desktop.X + desktop.Width ||
                b.Y + b.Height > desktop.Y + desktop.Height)
                ClearPixels();
            var ok = b.Width == _width && b.Height == _height
                ? BitBlt(_dc, 0, 0, _width, _height, _screen, b.X, b.Y, 0x40CC0020)
                : StretchBlt(_dc, 0, 0, _width, _height, _screen, b.X, b.Y, b.Width, b.Height, 0x40CC0020);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private unsafe void ClearPixels() => new Span<byte>((void*)_bits, checked(_width * _height * 4)).Clear();
        internal unsafe bool RememberChangedPixels()
        {
            var pixels = new ReadOnlySpan<byte>((void*)_bits, checked(_width * _height * 4));
            if (pixels.SequenceEqual(_last)) return false;
            if (_last.Length != pixels.Length) _last = new byte[pixels.Length];
            pixels.CopyTo(_last); return true;
        }
        internal byte[] Snapshot()
        {
            var bytes = ArrayPool<byte>.Shared.Rent(_last.Length);
            _last.CopyTo(bytes, 0); return bytes;
        }
        private void ReleaseBitmap()
        {
            Array.Clear(_last); _last = [];
            if (_bitmap == IntPtr.Zero) return;
            SelectObject(_dc, _previous); DeleteObject(_bitmap); _bitmap = _previous = _bits = IntPtr.Zero;
        }
        public void Dispose()
        {
            ReleaseBitmap();
            if (_dc != IntPtr.Zero) { DeleteDC(_dc); _dc = IntPtr.Zero; }
            if (_screen != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screen); _screen = IntPtr.Zero; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { internal int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, ImageSize; internal int XPels, YPels; internal uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
