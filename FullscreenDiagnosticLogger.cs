using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Temporary diagnostics for fullscreen-avoidance investigation. This observer never calls
/// TryGetFullscreenWindow and never changes z-order or detector state. Remove after testing.
/// </summary>
internal static class FullscreenDiagnosticLogger
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const uint GwOwner = 4;
    private const int FullscreenTolerance = 2;
    private const int ZOrderLimit = 100;
    private const long MaxLogBytes = 32L * 1024 * 1024;

    private static readonly object FileGate = new();
    private static readonly Type DetectorType = typeof(FullscreenForegroundWindowDetector);
    private static readonly FieldInfo? LastExternalField = DetectorField("_lastExternalForegroundWindow");
    private static readonly FieldInfo? SessionWindowField = DetectorField("_foregroundSessionWindow");
    private static readonly FieldInfo? SessionPidField = DetectorField("_foregroundSessionProcessId");
    private static readonly FieldInfo? TrackedField = DetectorField("_trackedFullscreenWindow");
    private static readonly FieldInfo? IgnoredField = DetectorField("_ignoredSessionFullscreenWindows");
    private static readonly FieldInfo? ContinuationField = DetectorField("_temporaryContinuationWindow");
    private static readonly FieldInfo? AvoidanceField = ControllerField("_fullscreenAvoidanceWindow");
    private static readonly FieldInfo? AvoidanceMonitorField = ControllerField("_fullscreenAvoidanceMonitorDeviceName");
    private static readonly FieldInfo? LastGlobalScanField = ControllerField("_lastFullscreenGlobalScanAt");
    private static readonly FieldInfo? ForceGlobalScanField = ControllerField("_fullscreenEventForceGlobalScan");

    private static System.Threading.Timer? _timer;
    private static int _queued;
    private static long _sequence;
    private static string _lastFingerprint = "";
    private static DateTimeOffset _lastPeriodicAt = DateTimeOffset.MinValue;

    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "fullscreen-diagnostic.log");
    private static string PreviousLogPath => Path.Combine(AppContext.BaseDirectory, "fullscreen-diagnostic.previous.log");

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !IsPaperTodoProcess())
            {
                return;
            }

            WriteSessionHeader();
            _timer = new System.Threading.Timer(
                static _ => QueueSample(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Diagnostics must never affect startup.
        }
    }

    private static void QueueSample()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0)
        {
            return;
        }

        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _queued, 0);
                return;
            }

            _ = dispatcher.BeginInvoke((Action)SampleOnUiThread, DispatcherPriority.Background);
        }
        catch
        {
            Interlocked.Exchange(ref _queued, 0);
        }
    }

    private static void SampleOnUiThread()
    {
        try
        {
            var state = CaptureState();
            var fingerprint = Fingerprint(state);
            var now = DateTimeOffset.UtcNow;
            var changed = !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal);
            var periodic = now - _lastPeriodicAt >= TimeSpan.FromSeconds(2);
            if (!changed && !periodic)
            {
                return;
            }

            _lastFingerprint = fingerprint;
            _lastPeriodicAt = now;
            var text = BuildSnapshot(
                Interlocked.Increment(ref _sequence),
                changed ? "state-change" : "periodic",
                state);
            ThreadPool.QueueUserWorkItem(static value => Append((string)value!), text);
        }
        catch
        {
            // Windows can disappear between any two diagnostic calls.
        }
        finally
        {
            Interlocked.Exchange(ref _queued, 0);
        }
    }

    private static State CaptureState()
    {
        var controller = TryGetController();
        return new State(
            GetForegroundWindow(),
            ReadStaticIntPtr(LastExternalField),
            ReadStaticIntPtr(SessionWindowField),
            ReadStaticUInt32(SessionPidField),
            ReadStaticIntPtr(TrackedField),
            ReadIgnored(),
            ReadStaticIntPtr(ContinuationField),
            ReadInstanceIntPtr(controller, AvoidanceField),
            ReadInstanceString(controller, AvoidanceMonitorField),
            ReadInstanceDateTimeOffset(controller, LastGlobalScanField),
            ReadInstanceInt32(controller, ForceGlobalScanField));
    }

    private static string Fingerprint(State state) => string.Join('|',
        Hex(state.Foreground),
        Hex(state.LastExternal),
        Hex(state.SessionWindow),
        state.SessionPid,
        Hex(state.Tracked),
        string.Join(',', state.Ignored.Select(Hex)),
        Hex(state.Continuation),
        Hex(state.Avoidance),
        state.AvoidanceMonitor,
        state.ForceGlobalScan);

    private static string BuildSnapshot(long sequence, string reason, State state)
    {
        var builder = new StringBuilder(48 * 1024);
        builder.AppendLine();
        builder.Append("================ FULLSCREEN DIAGNOSTIC #")
            .Append(sequence)
            .Append(" reason=").Append(reason)
            .Append(" local=").Append(DateTimeOffset.Now.ToString("O"))
            .AppendLine(" ================");
        builder.Append("controller shouldSuppress=").Append(state.Avoidance != IntPtr.Zero ? '1' : '0')
            .Append(" avoidance=0x").Append(Hex(state.Avoidance))
            .Append(" monitor=").Append(Quote(state.AvoidanceMonitor))
            .Append(" lastGlobalScanUtc=").Append(state.LastGlobalScan.ToString("O"))
            .Append(" forceGlobalScan=").Append(state.ForceGlobalScan)
            .AppendLine();
        builder.Append("detector foreground=0x").Append(Hex(state.Foreground))
            .Append(" lastExternal=0x").Append(Hex(state.LastExternal))
            .Append(" session=0x").Append(Hex(state.SessionWindow))
            .Append(" sessionPid=").Append(state.SessionPid)
            .Append(" tracked=0x").Append(Hex(state.Tracked))
            .Append(" continuation=0x").Append(Hex(state.Continuation))
            .Append(" ignored=").Append(FormatHandles(state.Ignored))
            .AppendLine();

        builder.AppendLine("keyWindows:");
        AppendWindow(builder, "foreground", state.Foreground, state);
        AppendWindow(builder, "lastExternal", state.LastExternal, state);
        AppendWindow(builder, "session", state.SessionWindow, state);
        AppendWindow(builder, "tracked", state.Tracked, state);
        AppendWindow(builder, "avoidance", state.Avoidance, state);
        foreach (var ignored in state.Ignored)
        {
            AppendWindow(builder, "ignored", ignored, state);
        }

        AppendSamePidWindows(builder, state);
        AppendFullscreenLikeWindows(builder, state);
        AppendZOrder(builder, state);

        builder.AppendLine("existingDetectorSnapshot:");
        try
        {
            builder.Append(FullscreenForegroundWindowDetector.BuildDebugSnapshot());
        }
        catch (Exception ex)
        {
            builder.Append("snapshotError=").Append(Quote(ex.GetBaseException().Message)).AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendSamePidWindows(StringBuilder builder, State state)
    {
        var pid = state.SessionPid != 0 ? state.SessionPid : ProcessIdFor(state.Foreground);
        builder.Append("samePidTopWindows pid=").Append(pid)
            .Append(" process=").Append(Quote(ProcessName(pid))).AppendLine(":");
        var count = 0;
        if (pid != 0)
        {
            EnumWindows((hwnd, _) =>
            {
                if (ProcessIdFor(hwnd) == pid)
                {
                    AppendWindow(builder, "samePid" + count.ToString("D2"), hwnd, state);
                    count++;
                }
                return true;
            }, IntPtr.Zero);
        }
        if (count == 0) builder.AppendLine("samePid=<none>");
    }

    private static void AppendFullscreenLikeWindows(StringBuilder builder, State state)
    {
        builder.AppendLine("fullscreenLikeTopWindows:");
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            var detail = ReadDetail(hwnd);
            if (detail.DwmCovers || detail.RawCovers)
            {
                AppendWindow(builder, "fullLike" + count.ToString("D2"), hwnd, state, detail);
                count++;
            }
            return true;
        }, IntPtr.Zero);
        if (count == 0) builder.AppendLine("fullscreenLike=<none>");
    }

    private static void AppendZOrder(StringBuilder builder, State state)
    {
        builder.AppendLine("zOrder top-to-bottom:");
        var index = 0;
        EnumWindows((hwnd, _) =>
        {
            if (index >= ZOrderLimit) return false;
            var d = ReadDetail(hwnd);
            builder.Append("z=").Append(index.ToString("D3"))
                .Append(" markers=").Append(Markers(hwnd, state))
                .Append(" hwnd=0x").Append(Hex(hwnd))
                .Append(" pid=").Append(d.ProcessId)
                .Append(" tid=").Append(d.ThreadId)
                .Append(" process=").Append(Quote(d.ProcessName))
                .Append(" class=").Append(Quote(d.ClassName))
                .Append(" title=").Append(Quote(d.Title))
                .Append(" visible=").Append(d.Visible ? '1' : '0')
                .Append(" iconic=").Append(d.Iconic ? '1' : '0')
                .Append(" topmost=").Append(d.Topmost ? '1' : '0')
                .Append(" tool=").Append(d.Tool ? '1' : '0')
                .Append(" cloaked=").Append(d.Cloaked)
                .Append(" candidateReason=").Append(d.CandidateReason)
                .Append(" root=0x").Append(Hex(d.Root))
                .Append(" rootOwner=0x").Append(Hex(d.RootOwner))
                .Append(" owner=0x").Append(Hex(d.Owner))
                .Append(" dwmRect=").Append(d.HasDwm ? FormatRect(d.DwmRect) : "<none>")
                .Append(" rawRect=").Append(d.HasRaw ? FormatRect(d.RawRect) : "<none>")
                .Append(" monitor=").Append(d.HasMonitor ? FormatRect(d.Monitor) : "<none>")
                .Append(" dwmCovers=").Append(d.DwmCovers ? '1' : '0')
                .Append(" rawCovers=").Append(d.RawCovers ? '1' : '0')
                .Append(" dwmDelta=").Append(d.HasDwm && d.HasMonitor ? Delta(d.DwmRect, d.Monitor) : "<none>")
                .Append(" rawDelta=").Append(d.HasRaw && d.HasMonitor ? Delta(d.RawRect, d.Monitor) : "<none>")
                .AppendLine();
            index++;
            return true;
        }, IntPtr.Zero);
    }

    private static void AppendWindow(StringBuilder builder, string label, IntPtr hwnd, State state, Detail? existing = null)
    {
        var d = existing ?? ReadDetail(hwnd);
        builder.Append(label)
            .Append(" markers=").Append(Markers(hwnd, state))
            .Append(" z=").Append(ZIndex(hwnd))
            .Append(" hwnd=0x").Append(Hex(hwnd))
            .Append(" pid=").Append(d.ProcessId)
            .Append(" tid=").Append(d.ThreadId)
            .Append(" process=").Append(Quote(d.ProcessName))
            .Append(" class=").Append(Quote(d.ClassName))
            .Append(" title=").Append(Quote(d.Title))
            .Append(" valid=").Append(d.Valid ? '1' : '0')
            .Append(" visible=").Append(d.Visible ? '1' : '0')
            .Append(" iconic=").Append(d.Iconic ? '1' : '0')
            .Append(" topmost=").Append(d.Topmost ? '1' : '0')
            .Append(" tool=").Append(d.Tool ? '1' : '0')
            .Append(" cloaked=").Append(d.Cloaked)
            .Append(" candidateReason=").Append(d.CandidateReason)
            .Append(" root=0x").Append(Hex(d.Root))
            .Append(" rootOwner=0x").Append(Hex(d.RootOwner))
            .Append(" owner=0x").Append(Hex(d.Owner))
            .Append(" style=0x").Append(d.Style.ToString("X8"))
            .Append(" exStyle=0x").Append(d.ExStyle.ToString("X8"))
            .Append(" dwmRect=").Append(d.HasDwm ? FormatRect(d.DwmRect) : "<none>")
            .Append(" rawRect=").Append(d.HasRaw ? FormatRect(d.RawRect) : "<none>")
            .Append(" monitor=").Append(d.HasMonitor ? FormatRect(d.Monitor) : "<none>")
            .Append(" workArea=").Append(d.HasMonitor ? FormatRect(d.WorkArea) : "<none>")
            .Append(" dwmCovers=").Append(d.DwmCovers ? '1' : '0')
            .Append(" rawCovers=").Append(d.RawCovers ? '1' : '0')
            .Append(" dwmDelta=").Append(d.HasDwm && d.HasMonitor ? Delta(d.DwmRect, d.Monitor) : "<none>")
            .Append(" rawDelta=").Append(d.HasRaw && d.HasMonitor ? Delta(d.RawRect, d.Monitor) : "<none>")
            .AppendLine();
    }

    private static Detail ReadDetail(IntPtr hwnd)
    {
        var valid = hwnd != IntPtr.Zero && IsWindow(hwnd);
        uint pid = 0;
        var tid = valid ? GetWindowThreadProcessId(hwnd, out pid) : 0;
        var style = valid ? GetWindowLong(hwnd, GwlStyle) : 0;
        var exStyle = valid ? GetWindowLong(hwnd, GwlExStyle) : 0;
        var visible = valid && IsWindowVisible(hwnd);
        var iconic = valid && IsIconic(hwnd);
        var cloaked = TryGetCloaked(hwnd, out var cloakValue) ? cloakValue : -1;
        var hasDwm = TryGetDwmRect(hwnd, out var dwmRect);
        var hasRaw = TryGetRawRect(hwnd, out var rawRect);
        var sourceRect = hasDwm ? dwmRect : rawRect;
        var hasMonitor = TryGetMonitor(sourceRect, out var monitor, out var workArea);
        var className = WindowClass(hwnd);
        return new Detail(
            valid, tid, pid, ProcessName(pid), className, WindowTitle(hwnd), style, exStyle,
            visible, iconic, (exStyle & WsExTopmost) != 0, (exStyle & WsExToolWindow) != 0,
            cloaked, CandidateReason(hwnd, valid, pid, visible, iconic, exStyle, cloaked, className),
            valid ? GetAncestor(hwnd, GaRoot) : IntPtr.Zero,
            valid ? GetAncestor(hwnd, GaRootOwner) : IntPtr.Zero,
            valid ? GetWindow(hwnd, GwOwner) : IntPtr.Zero,
            hasDwm, dwmRect, hasRaw, rawRect, hasMonitor, monitor, workArea,
            hasDwm && hasMonitor && Covers(dwmRect, monitor),
            hasRaw && hasMonitor && Covers(rawRect, monitor));
    }

    private static string CandidateReason(IntPtr hwnd, bool valid, uint pid, bool visible, bool iconic, int exStyle, int cloaked, string className)
    {
        if (hwnd == IntPtr.Zero) return "zero";
        if (hwnd == GetShellWindow()) return "shell-window";
        if (!valid) return "invalid";
        if (pid == Environment.ProcessId) return "current-process";
        if (!visible) return "hidden";
        if (iconic) return "iconic";
        if ((exStyle & WsExToolWindow) != 0) return "tool-window";
        if (cloaked > 0) return "cloaked";
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return "shell-class";
        return "candidate";
    }

    private static string Markers(IntPtr hwnd, State state)
    {
        if (hwnd == IntPtr.Zero) return "-";
        var value = new StringBuilder();
        if (hwnd == state.Foreground) value.Append('F');
        if (hwnd == state.LastExternal) value.Append('L');
        if (hwnd == state.SessionWindow) value.Append('S');
        if (hwnd == state.Tracked) value.Append('T');
        if (hwnd == state.Avoidance) value.Append('A');
        if (hwnd == state.Continuation) value.Append('C');
        if (state.Ignored.Contains(hwnd)) value.Append('I');
        if (ProcessIdFor(hwnd) == Environment.ProcessId) value.Append('P');
        return value.Length == 0 ? "-" : value.ToString();
    }

    private static int ZIndex(IntPtr target)
    {
        if (target == IntPtr.Zero) return -1;
        var index = 0;
        var result = -1;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == target)
            {
                result = index;
                return false;
            }
            index++;
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool Covers(NativeRect window, NativeRect monitor) =>
        window.Left <= monitor.Left + FullscreenTolerance &&
        window.Top <= monitor.Top + FullscreenTolerance &&
        window.Right >= monitor.Right - FullscreenTolerance &&
        window.Bottom >= monitor.Bottom - FullscreenTolerance;

    private static string Delta(NativeRect window, NativeRect monitor) =>
        $"[L={window.Left - monitor.Left},T={window.Top - monitor.Top},R={monitor.Right - window.Right},B={monitor.Bottom - window.Bottom}]";

    private static bool TryGetDwmRect(IntPtr hwnd, out NativeRect rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero &&
               DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0 &&
               !rect.IsEmpty;
    }

    private static bool TryGetRawRect(IntPtr hwnd, out NativeRect rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect) && !rect.IsEmpty;
    }

    private static bool TryGetMonitor(NativeRect rect, out NativeRect monitor, out NativeRect workArea)
    {
        monitor = default;
        workArea = default;
        if (rect.IsEmpty) return false;
        var source = rect;
        var handle = MonitorFromRect(ref source, MonitorDefaultToNearest);
        if (handle == IntPtr.Zero) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(handle, ref info)) return false;
        monitor = info.Monitor;
        workArea = info.WorkArea;
        return true;
    }

    private static bool TryGetCloaked(IntPtr hwnd, out int cloaked)
    {
        cloaked = 0;
        return hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0;
    }

    private static uint ProcessIdFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return process.ProcessName;
        }
        catch { return "<unavailable>"; }
    }

    private static string WindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : "";
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var length = Math.Min(GetWindowTextLength(hwnd), 512);
        if (length <= 0) return "";
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool IsPaperTodoProcess()
    {
        var path = Environment.ProcessPath;
        return string.Equals(
            string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileNameWithoutExtension(path),
            "PaperTodo",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSessionHeader()
    {
        lock (FileGate)
        {
            var text = new StringBuilder()
                .AppendLine("PaperTodo fullscreen diagnostic test build")
                .Append("startedLocal=").Append(DateTimeOffset.Now.ToString("O")).AppendLine()
                .Append("processId=").Append(Environment.ProcessId).AppendLine()
                .Append("processPath=").Append(Quote(Environment.ProcessPath)).AppendLine()
                .Append("appVersion=").Append(Quote(typeof(AppController).Assembly.GetName().Version?.ToString())).AppendLine()
                .Append("framework=").Append(Quote(RuntimeInformation.FrameworkDescription)).AppendLine()
                .Append("os=").Append(Quote(RuntimeInformation.OSDescription)).AppendLine()
                .AppendLine("markers: F=foreground L=lastExternal S=session T=tracked A=avoidance C=continuation I=ignored P=PaperTodo")
                .AppendLine("Positive right/bottom deltas mean the window falls short of that monitor edge.")
                .ToString();
            File.WriteAllText(LogPath, text, new UTF8Encoding(false));
        }
    }

    private static void Append(string text)
    {
        try
        {
            lock (FileGate)
            {
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length >= MaxLogBytes)
                {
                    try { File.Move(LogPath, PreviousLogPath, overwrite: true); }
                    catch { File.WriteAllText(LogPath, string.Empty, new UTF8Encoding(false)); }
                }
                File.AppendAllText(LogPath, text, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostic I/O must never affect normal behavior.
        }
    }

    private static AppController? TryGetController()
    {
        try { return AppController.Current; }
        catch { return null; }
    }

    private static FieldInfo? DetectorField(string name) =>
        DetectorType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);

    private static FieldInfo? ControllerField(string name) =>
        typeof(AppController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

    private static IntPtr ReadStaticIntPtr(FieldInfo? field)
    {
        try { return field?.GetValue(null) is IntPtr value ? value : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    private static uint ReadStaticUInt32(FieldInfo? field)
    {
        try { return field?.GetValue(null) is uint value ? value : 0; }
        catch { return 0; }
    }

    private static IntPtr[] ReadIgnored()
    {
        try
        {
            if (IgnoredField?.GetValue(null) is IEnumerable values)
            {
                return values.Cast<object>().OfType<IntPtr>().OrderBy(value => value.ToInt64()).ToArray();
            }
        }
        catch { }
        return [];
    }

    private static IntPtr ReadInstanceIntPtr(AppController? target, FieldInfo? field)
    {
        try { return target != null && field?.GetValue(target) is IntPtr value ? value : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    private static string ReadInstanceString(AppController? target, FieldInfo? field)
    {
        try { return target != null && field?.GetValue(target) is string value ? value : ""; }
        catch { return ""; }
    }

    private static int ReadInstanceInt32(AppController? target, FieldInfo? field)
    {
        try { return target != null && field?.GetValue(target) is int value ? value : 0; }
        catch { return 0; }
    }

    private static DateTimeOffset ReadInstanceDateTimeOffset(AppController? target, FieldInfo? field)
    {
        try { return target != null && field?.GetValue(target) is DateTimeOffset value ? value : DateTimeOffset.MinValue; }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string Hex(IntPtr value) => value.ToInt64().ToString("X");
    private static string FormatRect(NativeRect rect) => $"[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom} {rect.Width}x{rect.Height}]";
    private static string FormatHandles(IEnumerable<IntPtr> values)
    {
        var items = values.Select(value => "0x" + Hex(value)).ToArray();
        return items.Length == 0 ? "<none>" : string.Join(',', items);
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (value.Length > 180) value = value[..180] + "...";
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record State(
        IntPtr Foreground,
        IntPtr LastExternal,
        IntPtr SessionWindow,
        uint SessionPid,
        IntPtr Tracked,
        IntPtr[] Ignored,
        IntPtr Continuation,
        IntPtr Avoidance,
        string AvoidanceMonitor,
        DateTimeOffset LastGlobalScan,
        int ForceGlobalScan);

    private sealed record Detail(
        bool Valid,
        uint ThreadId,
        uint ProcessId,
        string ProcessName,
        string ClassName,
        string Title,
        int Style,
        int ExStyle,
        bool Visible,
        bool Iconic,
        bool Topmost,
        bool Tool,
        int Cloaked,
        string CandidateReason,
        IntPtr Root,
        IntPtr RootOwner,
        IntPtr Owner,
        bool HasDwm,
        NativeRect DwmRect,
        bool HasRaw,
        NativeRect RawRect,
        bool HasMonitor,
        NativeRect Monitor,
        NativeRect WorkArea,
        bool DwmCovers,
        bool RawCovers);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Right <= Left || Bottom <= Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
