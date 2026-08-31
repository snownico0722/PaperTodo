using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal static class WindowWorkAreaHelper
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;
    private static readonly object MonitorCacheGate = new();
    private static IReadOnlyList<MonitorEntry>? _cachedMonitors;

    public static void InvalidateMonitorGeometryCache()
    {
        lock (MonitorCacheGate)
        {
            _cachedMonitors = null;
        }
    }

    public static Rect WorkAreaFor(Rect dipRect)
    {
        if (dipRect.IsEmpty ||
            double.IsNaN(dipRect.Left) ||
            double.IsNaN(dipRect.Top) ||
            double.IsInfinity(dipRect.Left) ||
            double.IsInfinity(dipRect.Top))
        {
            return SystemParameters.WorkArea;
        }

        try
        {
            var nativeRect = DipRectToDevice(dipRect);
            var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var info = new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
            if (!GetMonitorInfo(monitor, ref info) || info.WorkArea.IsEmpty)
            {
                return SystemParameters.WorkArea;
            }

            return DeviceRectToDip(info.WorkArea);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    public static Rect WorkAreaFor(Window? window)
    {
        if (window == null)
        {
            return SystemParameters.WorkArea;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var info = new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
            if (!GetMonitorInfo(monitor, ref info) || info.WorkArea.IsEmpty)
            {
                return SystemParameters.WorkArea;
            }

            return DeviceRectToDip(window, info.WorkArea);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    public static bool TryGetMonitorDeviceName(Window? window, out string deviceName)
    {
        deviceName = "";
        if (window == null)
        {
            return false;
        }

        try
        {
            return TryGetMonitorDeviceName(new WindowInteropHelper(window).Handle, out deviceName);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetMonitorDeviceName(IntPtr windowHandle, out string deviceName)
    {
        deviceName = "";
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoEx(monitor, ref info))
            {
                return false;
            }

            deviceName = info.DeviceNameString;
            return !string.IsNullOrEmpty(deviceName);
        }
        catch
        {
            return false;
        }
    }

    // The DIP work-area of the monitor whose device name matches, or null if no such monitor
    // is currently connected. Used to resolve the deep-capsule stack's persisted anchor.
    public static Rect? WorkAreaForDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return null;
        }

        if (!TryEnumerateMonitors(out var monitors))
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.Ordinal) &&
                !monitor.WorkArea.IsEmpty)
            {
                return DeviceRectToDip(monitor.WorkArea);
            }
        }

        return null;
    }

    public static string NormalizeQueueMonitorDeviceName(string? deviceName)
    {
        var value = (deviceName ?? "").Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var primary = PrimaryMonitorDeviceName();
        return !string.IsNullOrEmpty(primary) && string.Equals(value, primary, StringComparison.Ordinal)
            ? ""
            : value;
    }

    public static string PrimaryMonitorDeviceName()
    {
        try
        {
            var primaryProbe = new NativeRect
            {
                Left = 0,
                Top = 0,
                Right = 1,
                Bottom = 1
            };
            var monitor = MonitorFromRect(ref primaryProbe, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return "";
            }

            var info = new MonitorInfoEx();
            info.Size = Marshal.SizeOf<MonitorInfoEx>();
            return GetMonitorInfoEx(monitor, ref info) ? info.DeviceNameString : "";
        }
        catch
        {
            return "";
        }
    }

    // Convert a PointToScreen point (physical screen pixels) into the app's screen DIP space.
    // Most persisted/window geometry in this app uses the system-DPI coordinate space, so callers
    // should use this when comparing a raw Win32 screen point with DeviceRectToDip rectangles.
    public static GlobalScreenDipPoint DeviceScreenPointToDip(DeviceScreenPoint screenPoint)
    {
        try
        {
            var (scaleX, scaleY) = SystemDpiScale();
            return new GlobalScreenDipPoint(screenPoint.X / scaleX, screenPoint.Y / scaleY);
        }
        catch
        {
            return new GlobalScreenDipPoint(screenPoint.X, screenPoint.Y);
        }
    }

    // The device name + DIP work-area of the monitor under a PointToScreen point (physical
    // screen pixels). Falls back to the nearest monitor. Used when dropping a capsule across
    // edges/monitors; using the raw device point avoids mixed-DPI monitor misidentification.
    public static (string DeviceName, Rect WorkArea)? MonitorAtDeviceScreenPoint(DeviceScreenPoint screenPoint)
    {
        try
        {
            var nativePoint = new NativePoint
            {
                X = (int)Math.Round(screenPoint.X),
                Y = (int)Math.Round(screenPoint.Y)
            };
            var monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfoEx();
            info.Size = Marshal.SizeOf<MonitorInfoEx>();
            if (!GetMonitorInfoEx(monitor, ref info) || info.WorkArea.IsEmpty)
            {
                return null;
            }

            return (info.DeviceNameString, DeviceRectToDip(info.WorkArea));
        }
        catch
        {
            return null;
        }
    }

    public static bool TryGetMonitorGeometryForDevice(string? deviceName, out MonitorGeometry geometry)
        => TryGetMonitorGeometryForDevice(deviceName, dpiWindow: null, out geometry);

    public static IReadOnlyList<MonitorGeometry> ConnectedMonitorGeometries()
    {
        if (!TryEnumerateMonitors(out var monitors) || monitors.Count == 0)
        {
            return Array.Empty<MonitorGeometry>();
        }

        return monitors
            .Select(monitor => CreateMonitorGeometry(monitor, dpiWindow: null))
            .ToArray();
    }

    public static bool TryGetMonitorGeometryForDevice(
        string? deviceName,
        Window? dpiWindow,
        out MonitorGeometry geometry)
    {
        geometry = default;
        if (!TryEnumerateMonitors(out var monitors) || monitors.Count == 0)
        {
            return false;
        }

        var requestedName = (deviceName ?? "").Trim();
        var monitor = string.IsNullOrEmpty(requestedName)
            ? monitors.FirstOrDefault(entry => entry.IsPrimary)
            : monitors.FirstOrDefault(entry =>
                string.Equals(entry.DeviceName, requestedName, StringComparison.Ordinal));
        if (monitor.Handle == IntPtr.Zero)
        {
            monitor = monitors.FirstOrDefault(entry => entry.IsPrimary);
            if (monitor.Handle == IntPtr.Zero)
            {
                return false;
            }
        }

        geometry = CreateMonitorGeometry(monitor, dpiWindow);
        return true;
    }

    public static bool TryGetMonitorGeometryAtDeviceScreenPoint(
        DeviceScreenPoint screenPoint,
        out MonitorGeometry geometry)
        => TryGetMonitorGeometryAtDeviceScreenPoint(screenPoint, dpiWindow: null, out geometry);

    public static bool ExceedsDragThreshold(
        DeviceScreenPoint start,
        DeviceScreenPoint current,
        Window? dpiWindow = null,
        double additionalDistanceDip = 0)
    {
        var scaleX = 1.0;
        var scaleY = 1.0;
        if (TryGetMonitorGeometryAtDeviceScreenPoint(start, dpiWindow, out var geometry))
        {
            scaleX = geometry.DpiScaleX;
            scaleY = geometry.DpiScaleY;
        }

        var additionalDistance = Math.Max(0, additionalDistanceDip);
        return Math.Abs(current.X - start.X) / scaleX >=
                SystemParameters.MinimumHorizontalDragDistance + additionalDistance ||
            Math.Abs(current.Y - start.Y) / scaleY >=
                SystemParameters.MinimumVerticalDragDistance + additionalDistance;
    }

    public static bool TryGetMonitorGeometryAtDeviceScreenPoint(
        DeviceScreenPoint screenPoint,
        Window? dpiWindow,
        out MonitorGeometry geometry)
    {
        geometry = default;
        var nativePoint = new NativePoint
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y)
        };
        var handle = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (TryEnumerateMonitors(out var monitors))
        {
            var cachedMonitor = monitors.FirstOrDefault(entry => entry.Handle == handle);
            if (cachedMonitor.Handle != IntPtr.Zero)
            {
                geometry = CreateMonitorGeometry(cachedMonitor, dpiWindow);
                return true;
            }
        }

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoEx(handle, ref info) || info.WorkArea.IsEmpty)
        {
            return false;
        }

        geometry = CreateMonitorGeometry(CreateMonitorEntry(
            handle,
            info.DeviceNameString,
            info.WorkArea,
            (info.Flags & MonitorInfoPrimary) != 0), dpiWindow);
        return true;
    }

    private static bool TryEnumerateMonitors(out IReadOnlyList<MonitorEntry> monitors)
    {
        lock (MonitorCacheGate)
        {
            if (_cachedMonitors is { Count: > 0 } cached)
            {
                monitors = cached;
                return true;
            }

            var results = new List<MonitorEntry>();
            try
            {
                var succeeded = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeRect _, IntPtr _) =>
                {
                    var info = new MonitorInfoEx();
                    info.Size = Marshal.SizeOf<MonitorInfoEx>();
                    if (GetMonitorInfoEx(hMonitor, ref info))
                    {
                        results.Add(CreateMonitorEntry(
                            hMonitor,
                            info.DeviceNameString,
                            info.WorkArea,
                            (info.Flags & MonitorInfoPrimary) != 0));
                    }

                    return true;
                }, IntPtr.Zero);
                if (!succeeded || results.Count == 0)
                {
                    monitors = results;
                    return false;
                }

                _cachedMonitors = results;
                monitors = results;
                return true;
            }
            catch
            {
                monitors = Array.Empty<MonitorEntry>();
                return false;
            }
        }
    }

    private static MonitorEntry CreateMonitorEntry(
        IntPtr handle,
        string deviceName,
        NativeRect workArea,
        bool isPrimary)
    {
        // This is only the pre-HWND bootstrap for a target monitor. Once a real capsule host is
        // on that monitor, CreateMonitorGeometry replaces these scales with GetDpiForWindow so
        // layout and physical bounds share the DPI of the HWND that Windows is actually moving.
        double scaleX;
        double scaleY;
        if (GetDpiForMonitor(handle, 0, out var dpiX, out var dpiY) == 0)
        {
            scaleX = ValidScale(dpiX / 96.0);
            scaleY = ValidScale(dpiY / 96.0);
        }
        else
        {
            (scaleX, scaleY) = SystemDpiScale();
        }

        return new MonitorEntry(handle, deviceName, workArea, isPrimary, scaleX, scaleY);
    }

    private static MonitorGeometry CreateMonitorGeometry(MonitorEntry monitor, Window? dpiWindow)
    {
        var scaleX = monitor.DpiScaleX;
        var scaleY = monitor.DpiScaleY;
        if (TryGetWindowDpiScaleForMonitor(dpiWindow, monitor.Handle, out var windowScale))
        {
            scaleX = windowScale;
            scaleY = windowScale;
        }

        return new MonitorGeometry(
            monitor.DeviceName,
            new DeviceScreenRect(
                monitor.WorkArea.Left,
                monitor.WorkArea.Top,
                monitor.WorkArea.Right,
                monitor.WorkArea.Bottom),
            scaleX,
            scaleY);
    }

    private static bool TryGetWindowDpiScaleForMonitor(
        Window? window,
        IntPtr monitor,
        out double scale)
    {
        scale = 1.0;
        if (window == null)
        {
            return false;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero ||
                MonitorFromWindow(handle, MonitorDefaultToNearest) != monitor)
            {
                return false;
            }

            var dpi = GetDpiForWindow(handle);
            if (dpi == 0)
            {
                return false;
            }

            scale = ValidScale(dpi / 96.0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct MonitorEntry(
        IntPtr Handle,
        string DeviceName,
        NativeRect WorkArea,
        bool IsPrimary,
        double DpiScaleX,
        double DpiScaleY);

    private static Rect DeviceRectToDip(Visual reference, NativeRect rect)
    {
        var source = PresentationSource.FromVisual(reference);
        if (source?.CompositionTarget != null)
        {
            var transform = source.CompositionTarget.TransformFromDevice;
            return new Rect(
                transform.Transform(new Point(rect.Left, rect.Top)),
                transform.Transform(new Point(rect.Right, rect.Bottom)));
        }

        var dpi = VisualTreeHelper.GetDpi(reference);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        return new Rect(
            rect.Left / scaleX,
            rect.Top / scaleY,
            (rect.Right - rect.Left) / scaleX,
            (rect.Bottom - rect.Top) / scaleY);
    }

    private static NativeRect DipRectToDevice(Rect rect)
    {
        var (scaleX, scaleY) = SystemDpiScale();
        return new NativeRect
        {
            Left = (int)Math.Floor(rect.Left * scaleX),
            Top = (int)Math.Floor(rect.Top * scaleY),
            Right = (int)Math.Ceiling(rect.Right * scaleX),
            Bottom = (int)Math.Ceiling(rect.Bottom * scaleY)
        };
    }

    private static Rect DeviceRectToDip(NativeRect rect)
    {
        var (scaleX, scaleY) = SystemDpiScale();
        return new Rect(
            rect.Left / scaleX,
            rect.Top / scaleY,
            (rect.Right - rect.Left) / scaleX,
            (rect.Bottom - rect.Top) / scaleY);
    }

    private static (double ScaleX, double ScaleY) SystemDpiScale()
    {
        var primaryProbe = new NativeRect
        {
            Left = 0,
            Top = 0,
            Right = 1,
            Bottom = 1
        };
        var monitor = MonitorFromRect(ref primaryProbe, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return (1.0, 1.0);
        }

        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info) || info.Monitor.IsEmpty)
        {
            return (1.0, 1.0);
        }

        var scaleX = info.Monitor.Width / Math.Max(1.0, SystemParameters.PrimaryScreenWidth);
        var scaleY = info.Monitor.Height / Math.Max(1.0, SystemParameters.PrimaryScreenHeight);
        return (ValidScale(scaleX), ValidScale(scaleY));
    }

    private static double ValidScale(double scale)
    {
        return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ? 1.0 : scale;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect lprc, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref NativeRect lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public bool IsEmpty => Right <= Left || Bottom <= Top;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] Device;

        public readonly string DeviceNameString =>
            Device == null ? "" : new string(Device).TrimEnd('\0');
    }
}
