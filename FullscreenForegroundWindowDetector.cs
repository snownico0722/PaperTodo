using System.Runtime.InteropServices;
using System.Text;

namespace PaperTodo;

internal static class FullscreenForegroundWindowDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int FullscreenTolerance = 2;
    private const int MinCandidateSize = 160;
    private const int DebugWindowLimit = 60;
    private static IntPtr _lastExternalForegroundWindow;

    public static bool IsForegroundFullscreen()
    {
        return TryGetFullscreenWindow(out _, allowGlobalScan: true);
    }

    public static bool TryGetFullscreenWindow(out IntPtr fullscreenWindow, bool allowGlobalScan)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !IsCurrentProcessWindow(foreground))
        {
            _lastExternalForegroundWindow = foreground;
        }

        if (TryGetTrackedExternalForegroundFullscreen(out fullscreenWindow))
        {
            return true;
        }

        return allowGlobalScan && TryGetAnyExternalFullscreenWindow(out fullscreenWindow);
    }

    public static string BuildDebugSnapshot()
    {
        var builder = new StringBuilder();
        var foreground = GetForegroundWindow();
        var shellWindow = GetShellWindow();

        builder.AppendLine($"time={DateTimeOffset.Now:O}");
        builder.AppendLine($"foreground=0x{foreground.ToInt64():X} foregroundClass={GetWindowClassName(foreground)} foregroundTitle={Quote(GetWindowTitle(foreground))} currentProcess={IsCurrentProcessWindow(foreground)}");
        builder.AppendLine($"lastExternalForeground=0x{_lastExternalForegroundWindow.ToInt64():X}");
        builder.AppendLine("foregroundDetail:");
        AppendDebugWindow(builder, "fg", foreground, shellWindow);
        builder.AppendLine("lastExternalForegroundDetail:");
        AppendDebugWindow(builder, "last", _lastExternalForegroundWindow, shellWindow);
        AppendScreenProbeDebug(builder, foreground, shellWindow);
        AppendForegroundChildDebug(builder, foreground, shellWindow);
        builder.AppendLine("topWindows:");

        var index = 0;
        EnumWindows((hwnd, _) =>
        {
            if (index >= DebugWindowLimit)
            {
                return false;
            }

            AppendDebugWindow(builder, index.ToString("D2"), hwnd, shellWindow);
            index++;
            return true;
        }, IntPtr.Zero);

        if (index == 0)
        {
            builder.AppendLine("windows=<none>");
        }

        return builder.ToString();
    }

    private static bool TryGetTrackedExternalForegroundFullscreen(out IntPtr fullscreenWindow)
    {
        fullscreenWindow = IntPtr.Zero;
        if (_lastExternalForegroundWindow == IntPtr.Zero)
        {
            return false;
        }

        if (!IsWindow(_lastExternalForegroundWindow) || IsCurrentProcessWindow(_lastExternalForegroundWindow))
        {
            _lastExternalForegroundWindow = IntPtr.Zero;
            return false;
        }

        if (IsCandidateExternalWindow(_lastExternalForegroundWindow, GetShellWindow()) &&
            IsFullscreenWindow(_lastExternalForegroundWindow))
        {
            fullscreenWindow = _lastExternalForegroundWindow;
            return true;
        }

        return false;
    }

    private static bool TryGetAnyExternalFullscreenWindow(out IntPtr fullscreenWindow)
    {
        var shellWindow = GetShellWindow();
        fullscreenWindow = IntPtr.Zero;
        var foundWindow = IntPtr.Zero;
        var found = false;

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidateExternalWindow(hwnd, shellWindow))
            {
                return true;
            }

            if (!IsFullscreenWindow(hwnd))
            {
                return true;
            }

            _lastExternalForegroundWindow = hwnd;
            foundWindow = hwnd;
            found = true;
            return false;
        }, IntPtr.Zero);

        fullscreenWindow = foundWindow;
        return found;
    }

    private static bool IsFullscreenWindow(IntPtr hwnd)
    {
        if (TryGetDwmWindowBounds(hwnd, out var dwmRect) &&
            TryGetMonitorInfoForRect(dwmRect, out var dwmMonitorInfo) &&
            IsFullscreenRect(dwmRect, dwmMonitorInfo.Monitor))
        {
            return true;
        }

        return TryGetRawWindowBounds(hwnd, out var rawRect) &&
               TryGetMonitorInfoForRect(rawRect, out var rawMonitorInfo) &&
               IsFullscreenRect(rawRect, rawMonitorInfo.Monitor);
    }

    private static bool TryGetMonitorInfoForRect(Rectangle rect, out MonitorInfo monitorInfo)
    {
        monitorInfo = default;
        if (rect.IsEmpty ||
            rect.Width < MinCandidateSize ||
            rect.Height < MinCandidateSize)
        {
            return false;
        }

        var windowRect = rect;
        var monitor = MonitorFromRect(ref windowRect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        return GetMonitorInfo(monitor, ref monitorInfo);
    }

    private static bool IsFullscreenRect(Rectangle windowRect, Rectangle monitorRect)
    {
        return CoversMonitor(windowRect, monitorRect);
    }

    private static bool IsCandidateExternalWindow(IntPtr hwnd, IntPtr shellWindow)
    {
        if (hwnd == IntPtr.Zero ||
            hwnd == shellWindow ||
            !IsWindow(hwnd) ||
            IsCurrentProcessWindow(hwnd) ||
            !IsVisibleWindow(hwnd) ||
            IsToolWindow(hwnd) ||
            IsCloaked(hwnd) ||
            IsShellClassWindow(hwnd))
        {
            return false;
        }

        return true;
    }

    private static bool IsVisibleWindow(IntPtr hwnd)
    {
        return IsWindowVisible(hwnd) && !IsIconic(hwnd);
    }

    private static bool IsToolWindow(IntPtr hwnd)
    {
        return (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool IsShellClassWindow(IntPtr hwnd)
    {
        var className = GetWindowClassName(hwnd);
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        if (TryGetDwmWindowBounds(hwnd, out rect))
        {
            return true;
        }

        return TryGetRawWindowBounds(hwnd, out rect);
    }

    private static bool TryGetDwmWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rectangle>()) == 0 &&
               !rect.IsEmpty;
    }

    private static bool TryGetRawWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        return GetWindowRect(hwnd, out rect) && !rect.IsEmpty;
    }

    private static bool IsCurrentProcessWindow(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    private static bool CoversMonitor(Rectangle windowRect, Rectangle monitorRect)
    {
        return windowRect.Left <= monitorRect.Left + FullscreenTolerance &&
               windowRect.Top <= monitorRect.Top + FullscreenTolerance &&
               windowRect.Right >= monitorRect.Right - FullscreenTolerance &&
               windowRect.Bottom >= monitorRect.Bottom - FullscreenTolerance;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString(0, length);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(length + 1, 512));
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void AppendDebugWindow(StringBuilder builder, string label, IntPtr hwnd, IntPtr shellWindow)
    {
        var className = GetWindowClassName(hwnd);
        var title = GetWindowTitle(hwnd);
        var style = GetWindowLong(hwnd, GwlStyle);
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        var isVisible = IsWindowVisible(hwnd);
        var isIconic = IsIconic(hwnd);
        var isCurrentProcess = IsCurrentProcessWindow(hwnd);
        var isToolWindow = (exStyle & WsExToolWindow) != 0;
        var isCloaked = TryGetCloaked(hwnd, out var cloaked) && cloaked != 0;
        var isShell = hwnd == shellWindow || className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        var hasDwmBounds = TryGetDwmWindowBounds(hwnd, out var dwmRect);
        var hasRawBounds = TryGetRawWindowBounds(hwnd, out var rawRect);
        var hasBounds = TryGetWindowBounds(hwnd, out var rect);
        MonitorInfo monitorInfo = default;
        var hasMonitor = hasBounds && TryGetMonitorInfoForRect(rect, out monitorInfo);
        var coversMonitor = hasMonitor && IsFullscreenRect(rect, monitorInfo.Monitor);
        MonitorInfo rawMonitorInfo = default;
        var rawCoversMonitor = hasRawBounds &&
            TryGetMonitorInfoForRect(rawRect, out rawMonitorInfo) &&
            IsFullscreenRect(rawRect, rawMonitorInfo.Monitor);
        var candidate = IsCandidateExternalWindow(hwnd, shellWindow);

        builder.Append(label);
        builder.Append(" hwnd=0x").Append(hwnd.ToInt64().ToString("X"));
        builder.Append(" class=").Append(Quote(className));
        builder.Append(" title=").Append(Quote(title));
        builder.Append(" style=0x").Append(style.ToString("X8"));
        builder.Append(" ex=0x").Append(exStyle.ToString("X8"));
        builder.Append(" show=").Append(TryGetWindowShowCommand(hwnd, out var showCommand) ? showCommand.ToString() : "<none>");
        builder.Append(" visible=").Append(isVisible ? "1" : "0");
        builder.Append(" iconic=").Append(isIconic ? "1" : "0");
        builder.Append(" current=").Append(isCurrentProcess ? "1" : "0");
        builder.Append(" tool=").Append(isToolWindow ? "1" : "0");
        builder.Append(" cloaked=").Append(isCloaked ? cloaked.ToString() : "0");
        builder.Append(" shell=").Append(isShell ? "1" : "0");
        builder.Append(" candidate=").Append(candidate ? "1" : "0");
        builder.Append(" dwmRect=").Append(hasDwmBounds ? FormatRect(dwmRect) : "<none>");
        builder.Append(" rawRect=").Append(hasRawBounds ? FormatRect(rawRect) : "<none>");
        builder.Append(" rect=").Append(hasBounds ? FormatRect(rect) : "<none>");
        builder.Append(" monitor=").Append(hasMonitor ? FormatRect(monitorInfo.Monitor) : "<none>");
        builder.Append(" covers=").Append(coversMonitor ? "1" : "0");
        builder.Append(" rawCovers=").Append(rawCoversMonitor ? "1" : "0");
        builder.AppendLine();
    }

    private static void AppendScreenProbeDebug(StringBuilder builder, IntPtr foreground, IntPtr shellWindow)
    {
        if (!TryGetWindowBounds(foreground, out var foregroundRect) ||
            !TryGetMonitorInfoForRect(foregroundRect, out var monitorInfo))
        {
            return;
        }

        var monitor = monitorInfo.Monitor;
        var probes = new (string Label, Point Point)[]
        {
            ("center", new Point((monitor.Left + monitor.Right) / 2, (monitor.Top + monitor.Bottom) / 2)),
            ("topLeft", new Point(monitor.Left + 8, monitor.Top + 8)),
            ("topRight", new Point(monitor.Right - 8, monitor.Top + 8)),
            ("bottomLeft", new Point(monitor.Left + 8, monitor.Bottom - 8)),
            ("bottomRight", new Point(monitor.Right - 8, monitor.Bottom - 8))
        };

        builder.AppendLine("screenProbes:");
        foreach (var probe in probes)
        {
            var hit = WindowFromPoint(probe.Point);
            var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
            builder.Append(probe.Label)
                .Append(" point=[").Append(probe.Point.X).Append(',').Append(probe.Point.Y).Append(']')
                .Append(" hit=0x").Append(hit.ToInt64().ToString("X"))
                .Append(" hitClass=").Append(Quote(GetWindowClassName(hit)))
                .Append(" hitTitle=").Append(Quote(GetWindowTitle(hit)))
                .Append(" root=0x").Append(root.ToInt64().ToString("X"))
                .Append(" rootClass=").Append(Quote(GetWindowClassName(root)))
                .Append(" rootTitle=").Append(Quote(GetWindowTitle(root)))
                .Append(" rootCurrent=").Append(IsCurrentProcessWindow(root) ? "1" : "0")
                .Append(" rootCandidate=").Append(IsCandidateExternalWindow(root, shellWindow) ? "1" : "0")
                .AppendLine();
        }
    }

    private static void AppendForegroundChildDebug(StringBuilder builder, IntPtr foreground, IntPtr shellWindow)
    {
        if (foreground == IntPtr.Zero)
        {
            return;
        }

        builder.AppendLine("foregroundChildren:");
        var index = 0;
        EnumChildWindows(foreground, (hwnd, _) =>
        {
            if (index >= 30)
            {
                return false;
            }

            AppendDebugWindow(builder, "child" + index.ToString("D2"), hwnd, shellWindow);
            index++;
            return true;
        }, IntPtr.Zero);

        if (index == 0)
        {
            builder.AppendLine("children=<none>");
        }
    }

    private static bool TryGetCloaked(IntPtr hwnd, out int cloaked)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0;
    }

    private static bool TryGetWindowShowCommand(IntPtr hwnd, out int showCommand)
    {
        showCommand = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        showCommand = placement.ShowCommand;
        return true;
    }

    private static string FormatRect(Rectangle rect)
    {
        return $"[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom} {rect.Width}x{rect.Height}]";
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (value.Length > 120)
        {
            value = value[..120] + "...";
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rectangle lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rectangle lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Rectangle pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
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
    private struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point MinPosition;
        public Point MaxPosition;
        public Rectangle NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle Monitor;
        public Rectangle WorkArea;
        public uint Flags;
    }
}
