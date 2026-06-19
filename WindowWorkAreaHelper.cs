using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal static class WindowWorkAreaHelper
{
    private const uint MonitorDefaultToNearest = 2;

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

    // The DIP work-area of the monitor whose device name matches, or null if no such monitor
    // is currently connected. Used to resolve the deep-capsule stack's persisted anchor.
    public static Rect? WorkAreaForDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return null;
        }

        foreach (var monitor in EnumerateMonitors())
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
    public static Point DeviceScreenPointToDip(Point screenPoint)
    {
        try
        {
            var (scaleX, scaleY) = SystemDpiScale();
            return new Point(screenPoint.X / scaleX, screenPoint.Y / scaleY);
        }
        catch
        {
            return screenPoint;
        }
    }

    // The device name + DIP work-area of the monitor under a PointToScreen point (physical
    // screen pixels). Falls back to the nearest monitor. Used when dropping a capsule across
    // edges/monitors; using the raw device point avoids mixed-DPI monitor misidentification.
    public static (string DeviceName, Rect WorkArea)? MonitorAtDeviceScreenPoint(Point screenPoint)
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

    // The device name + DIP work-area of the monitor under a screen point (in DIPs).
    // Falls back to the nearest monitor. Used by older callers that already have system-DPI
    // screen coordinates, not raw PointToScreen pixels.
    public static (string DeviceName, Rect WorkArea)? MonitorAtScreenPoint(Point dipPoint)
    {
        try
        {
            var (scaleX, scaleY) = SystemDpiScale();
            var nativePoint = new NativePoint
            {
                X = (int)Math.Round(dipPoint.X * scaleX),
                Y = (int)Math.Round(dipPoint.Y * scaleY)
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

    private static IEnumerable<MonitorEntry> EnumerateMonitors()
    {
        var results = new List<MonitorEntry>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeRect _, IntPtr _) =>
            {
                var info = new MonitorInfoEx();
                info.Size = Marshal.SizeOf<MonitorInfoEx>();
                if (GetMonitorInfoEx(hMonitor, ref info))
                {
                    results.Add(new MonitorEntry(info.DeviceNameString, info.WorkArea));
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Enumeration failure leaves results empty; callers fall back to the primary work area.
        }

        return results;
    }

    private readonly record struct MonitorEntry(string DeviceName, NativeRect WorkArea);

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
