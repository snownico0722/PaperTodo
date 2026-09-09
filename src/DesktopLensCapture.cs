using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>Short-lived, local capture of the rectangle behind one expanded lens.
/// No files, network, screen-wide history, cursor capture or UI-thread BitBlt. A single
/// pending presentation is allowed. Failure never silently captures our own window.</summary>
internal sealed class DesktopLensCapture : IDisposable
{
    internal sealed record Region(int OffsetX, int OffsetY, int Width, int Height, int Padding);
    internal sealed class Frame : IDisposable
    {
        internal readonly byte[] Pixels;
        internal readonly Int32Rect Bounds;
        internal readonly Region Geometry;
        private int _disposed;
        internal Frame(byte[] pixels, Int32Rect bounds, Region geometry) => (Pixels, Bounds, Geometry) = (pixels, bounds, geometry);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) ArrayPool<byte>.Shared.Return(Pixels, clearArray: true); }
    }
    private readonly IntPtr _hwnd;
    private readonly uint _oldAffinity;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Frame> _present;
    private readonly Action<Exception> _failed;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _worker;
    private Region _region;
    private int _pending, _disposed;
    internal bool IsStopped => Volatile.Read(ref _disposed) != 0;

    internal DesktopLensCapture(IntPtr hwnd, Region region, Dispatcher dispatcher,
        Action<Frame> present, Action<Exception> failed)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Background exclusion requires Windows 10 2004 or later.");
        if (!GetWindowDisplayAffinity(hwnd, out _oldAffinity) || !SetWindowDisplayAffinity(hwnd, 0x11))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclude the lens from its own background.");
        _hwnd = hwnd; _region = region; _dispatcher = dispatcher; _present = present; _failed = failed;
        try
        {
            _worker = Task.Factory.StartNew(CaptureLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            SetWindowDisplayAffinity(hwnd, _oldAffinity); _cancel.Dispose(); throw;
        }
    }
    internal void SetRegion(Region region) => Volatile.Write(ref _region, region);

    private void CaptureLoop()
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            using var surface = new CaptureSurface();
            DwmFlush(); // Finish exclusion before accepting the first source frame.
            do
            {
                if (_cancel.IsCancellationRequested) break;
                if (Volatile.Read(ref _pending) != 0) continue;
                var geometry = Volatile.Read(ref _region);
                if (!GetWindowRect(_hwnd, out var r) || IsIconic(_hwnd) || !IsWindowVisible(_hwnd)) continue;
                if (!GetWindowDisplayAffinity(_hwnd, out var affinity) || affinity != 0x11)
                    throw new InvalidOperationException("Background exclusion was changed; stopping to prevent recursive feedback.");
                var box = new Int32Rect(r.Left + geometry.OffsetX - geometry.Padding,
                    r.Top + geometry.OffsetY - geometry.Padding,
                    geometry.Width + geometry.Padding * 2, geometry.Height + geometry.Padding * 2);
                var left = Math.Max(box.X, GetSystemMetrics(76)); var top = Math.Max(box.Y, GetSystemMetrics(77));
                var right = Math.Min(box.X + box.Width, GetSystemMetrics(76) + GetSystemMetrics(78));
                var bottom = Math.Min(box.Y + box.Height, GetSystemMetrics(77) + GetSystemMetrics(79));
                if (right <= left || bottom <= top) continue;
                box = new Int32Rect(left, top, right - left, bottom - top);
                if ((long)box.Width * box.Height > 4_194_304 || box.Width > 8192 || box.Height > 8192)
                    throw new InvalidOperationException("Lens capture exceeds its 4-megapixel surface budget.");
                var frame = new Frame(surface.Read(box), box, geometry);
                if (_cancel.IsCancellationRequested || _dispatcher.HasShutdownStarted) { frame.Dispose(); break; }
                Interlocked.Exchange(ref _pending, 1);
                try
                {
                    var operation = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        try { if (!_cancel.IsCancellationRequested) _present(frame); }
                        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
                        { if (!_cancel.IsCancellationRequested) _failed(ex); }
                        finally { frame.Dispose(); Interlocked.Exchange(ref _pending, 0); }
                    }));
                    // Subscribe before checking: shutdown can abort between these operations.
                    operation.Aborted += (_, _) => { frame.Dispose(); Interlocked.Exchange(ref _pending, 0); };
                    if (operation.Status == DispatcherOperationStatus.Aborted)
                    { frame.Dispose(); Interlocked.Exchange(ref _pending, 0); break; }
                }
                catch { frame.Dispose(); Interlocked.Exchange(ref _pending, 0); throw; }
            } while (!_cancel.Token.WaitHandle.WaitOne(16));
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
        {
            if (!_cancel.IsCancellationRequested && !_dispatcher.HasShutdownStarted)
            {
                try { _dispatcher.BeginInvoke(new Action(() => { if (!_cancel.IsCancellationRequested) _failed(ex); })); }
                catch (InvalidOperationException) { /* Dispatcher shutdown won the race. */ }
            }
        }
        finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        // Do not overwrite a later affinity decision made by another component.
        if (GetWindowDisplayAffinity(_hwnd, out var current) && current == 0x11)
            SetWindowDisplayAffinity(_hwnd, _oldAffinity);
        _ = _worker.ContinueWith(_ => _cancel.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal static bool TryGetBounds(IntPtr hwnd, out Int32Rect bounds)
    {
        if (GetWindowRect(hwnd, out var r)) { bounds = new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
        bounds = default; return false;
    }
    internal static uint ReadAffinity(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out var value) ? value : uint.MaxValue;

    // Reused DIB/DCs belong to the capture worker. Alpha is ignored (WPF Bgr32),
    // never passed from uninitialized GDI bytes into the window compositor.
    private sealed class CaptureSurface : IDisposable
    {
        private IntPtr _screen, _dc, _bitmap, _previous, _bits;
        private int _width, _height;
        internal CaptureSurface()
        {
            _screen = GetDC(IntPtr.Zero);
            if (_screen != IntPtr.Zero) _dc = CreateCompatibleDC(_screen);
            if (_dc == IntPtr.Zero) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        internal byte[] Read(Int32Rect box)
        {
            if (_width != box.Width || _height != box.Height)
            {
                ReleaseBitmap();
                var info = new BitmapInfo { Size = 40, Width = box.Width, Height = -box.Height, Planes = 1, Bits = 32 };
                _bitmap = CreateDIBSection(_screen, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _previous = SelectObject(_dc, _bitmap);
                if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
                { DeleteObject(_bitmap); _bitmap = _bits = IntPtr.Zero; throw new Win32Exception(Marshal.GetLastWin32Error()); }
                _width = box.Width; _height = box.Height;
            }
            if (!BitBlt(_dc, 0, 0, box.Width, box.Height, _screen, box.X, box.Y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            GdiFlush();
            var bytes = ArrayPool<byte>.Shared.Rent(checked(box.Width * box.Height * 4));
            Marshal.Copy(_bits, bytes, 0, box.Width * box.Height * 4); return bytes;
        }
        private void ReleaseBitmap()
        {
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
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
