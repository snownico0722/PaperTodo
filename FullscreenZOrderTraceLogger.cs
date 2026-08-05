using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PaperTodo;

/// <summary>
/// Temporary call-adjacent z-order diagnostics for fullscreen avoidance. WinEvent reorder events
/// and short high-frequency bursts reveal whether PaperTodo is never placed behind the avoidance
/// HWND, or is placed correctly and then raised again. This observer never changes window state.
/// </summary>
internal static class FullscreenZOrderTraceLogger
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectReorder = 0x8004;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const int MaxPaperTodoWindows = 32;
    private const long BurstDurationMilliseconds = 1500;
    private const long BurstSampleIntervalMilliseconds = 10;
    private const long MaxLogBytes = 16L * 1024 * 1024;

    private static readonly Type DetectorType = typeof(FullscreenForegroundWindowDetector);
    private static readonly FieldInfo? TrackedField = DetectorField("_trackedFullscreenWindow");
    private static readonly FieldInfo? SessionField = DetectorField("_foregroundSessionWindow");
    private static readonly FieldInfo? AvoidanceField = ControllerField("_fullscreenAvoidanceWindow");
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly object FileGate = new();
    private static readonly WinEventDelegate WinEventCallback = OnWinEvent;

    private static IntPtr _foregroundHook;
    private static IntPtr _reorderHook;
    private static System.Threading.Timer? _timer;
    private static int _timerRunning;
    private static int _pendingEventSnapshots;
    private static long _sequence;
    private static long _traceUntilTick;
    private static long _lastBurstSampleTick;
    private static string _lastStateFingerprint = "";

    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "fullscreen-zorder-trace.log");
    private static string PreviousLogPath => Path.Combine(AppContext.BaseDirectory, "fullscreen-zorder-trace.previous.log");

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            if (!OperatingSystem.IsWindows() ||
                !string.Equals(
                    Environment.ProcessPath is { } path ? Path.GetFileNameWithoutExtension(path) : "",
                    "PaperTodo",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RotateLogAtStartup();
            EnqueueLine(
                "PaperTodo fullscreen z-order transition trace" + Environment.NewLine +
                $"startedLocal={DateTimeOffset.Now:O}" + Environment.NewLine +
                $"processId={Environment.ProcessId}" + Environment.NewLine +
                "notes: lower z means nearer the front; relation=front means PaperTodo is incorrectly above avoidance." + Environment.NewLine);

            _foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                WinEventCallback,
                0,
                0,
                WineventOutOfContext);
            _reorderHook = SetWinEventHook(
                EventObjectReorder,
                EventObjectReorder,
                IntPtr.Zero,
                WinEventCallback,
                0,
                0,
                WineventOutOfContext);

            _timer = new System.Threading.Timer(
                static _ => OnTimer(),
                null,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(BurstSampleIntervalMilliseconds));
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Shutdown();
        }
        catch
        {
            // Diagnostics must never affect startup.
        }
    }

    private static void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            if (eventType == EventObjectReorder &&
                objectId != ObjidWindow &&
                objectId != 0)
            {
                return;
            }
            if (eventType == EventObjectReorder &&
                childId != ChildidSelf &&
                childId != 0)
            {
                return;
            }

            var state = ReadState();
            var root = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GaRoot);
            var eventProcessId = ProcessIdFor(root != IntPtr.Zero ? root : hwnd);
            var relevant = eventType == EventSystemForeground ||
                state.Avoidance != IntPtr.Zero ||
                eventProcessId == Environment.ProcessId ||
                hwnd == state.Foreground ||
                hwnd == state.Avoidance ||
                hwnd == state.Tracked;
            if (!relevant)
            {
                return;
            }

            ExtendBurst();
            if (Interlocked.Increment(ref _pendingEventSnapshots) > 12)
            {
                Interlocked.Decrement(ref _pendingEventSnapshots);
                return;
            }

            ThreadPool.QueueUserWorkItem(static payload =>
            {
                var data = (EventPayload)payload!;
                try
                {
                    EnqueueLine(CaptureSnapshot(
                        data.EventType == EventSystemForeground ? "foreground-event" : "reorder-event",
                        data.EventType,
                        data.Hwnd,
                        data.ObjectId,
                        data.ChildId,
                        data.EventThread,
                        data.EventTime));
                }
                catch
                {
                    // A destroyed HWND between the event and snapshot is expected.
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingEventSnapshots);
                }
            }, new EventPayload(eventType, hwnd, objectId, childId, eventThread, eventTime));
        }
        catch
        {
            // WinEvent callbacks must never escape into user32.
        }
    }

    private static void OnTimer()
    {
        if (Interlocked.Exchange(ref _timerRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var state = ReadState();
            var fingerprint = StateFingerprint(state);
            if (!string.Equals(fingerprint, _lastStateFingerprint, StringComparison.Ordinal))
            {
                _lastStateFingerprint = fingerprint;
                ExtendBurst();
                EnqueueLine(CaptureSnapshot("controller-state-change", 0, IntPtr.Zero, 0, 0, 0, 0));
            }

            var now = Environment.TickCount64;
            if (now <= Interlocked.Read(ref _traceUntilTick) &&
                now - Interlocked.Read(ref _lastBurstSampleTick) >= BurstSampleIntervalMilliseconds)
            {
                Interlocked.Exchange(ref _lastBurstSampleTick, now);
                EnqueueLine(CaptureSnapshot("burst-sample", 0, IntPtr.Zero, 0, 0, 0, 0));
            }

            FlushPendingLines();
        }
        catch
        {
            // Diagnostics are best effort only.
        }
        finally
        {
            Interlocked.Exchange(ref _timerRunning, 0);
        }
    }

    private static void ExtendBurst()
    {
        var until = Environment.TickCount64 + BurstDurationMilliseconds;
        while (true)
        {
            var current = Interlocked.Read(ref _traceUntilTick);
            if (current >= until ||
                Interlocked.CompareExchange(ref _traceUntilTick, until, current) == current)
            {
                return;
            }
        }
    }

    private static TraceState ReadState()
    {
        var controller = AppController.Current;
        return new TraceState(
            GetForegroundWindow(),
            ReadHandle(TrackedField, null),
            ReadHandle(SessionField, null),
            controller == null ? IntPtr.Zero : ReadHandle(AvoidanceField, controller));
    }

    private static string CaptureSnapshot(
        string kind,
        uint eventType,
        IntPtr eventHwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        var state = ReadState();
        var zOrder = CaptureZOrder();
        var builder = new StringBuilder(2048);
        var sequence = Interlocked.Increment(ref _sequence);

        builder.Append("trace seq=").Append(sequence)
            .Append(" local=").Append(DateTimeOffset.Now.ToString("O"))
            .Append(" tick=").Append(Environment.TickCount64)
            .Append(" kind=").Append(kind);
        if (eventType != 0)
        {
            builder.Append(" event=0x").Append(eventType.ToString("X4"))
                .Append(" eventHwnd=").Append(FormatHandle(eventHwnd))
                .Append(" objectId=").Append(objectId)
                .Append(" childId=").Append(childId)
                .Append(" eventThread=").Append(eventThread)
                .Append(" eventTime=").Append(eventTime);
        }
        builder.AppendLine();

        AppendTarget(builder, "foreground", state.Foreground, zOrder);
        AppendTarget(builder, "avoidance", state.Avoidance, zOrder);
        AppendTarget(builder, "tracked", state.Tracked, zOrder);
        AppendTarget(builder, "session", state.Session, zOrder);

        builder.AppendLine("paperTodoWindows:");
        var paperWindows = zOrder.Windows
            .Where(hwnd => ProcessIdFor(hwnd) == Environment.ProcessId)
            .Select(hwnd => new
            {
                Hwnd = hwnd,
                Z = zOrder.Index.TryGetValue(hwnd, out var z) ? z : -1
            })
            .OrderBy(item => item.Z)
            .Take(MaxPaperTodoWindows)
            .ToList();
        if (paperWindows.Count == 0)
        {
            builder.AppendLine("  <none>");
        }
        else
        {
            foreach (var item in paperWindows)
            {
                var owner = GetWindow(item.Hwnd, GwOwner);
                var ownerZ = owner != IntPtr.Zero && zOrder.Index.TryGetValue(owner, out var oz) ? oz : -1;
                var avoidanceZ = state.Avoidance != IntPtr.Zero &&
                    zOrder.Index.TryGetValue(state.Avoidance, out var az)
                    ? az
                    : -1;
                var relation = item.Z < 0 || avoidanceZ < 0
                    ? "unknown"
                    : item.Z < avoidanceZ
                        ? "front"
                        : item.Z > avoidanceZ
                            ? "behind"
                            : "same";
                var exStyle = GetWindowLong(item.Hwnd, GwlExStyle);
                builder.Append("  hwnd=").Append(FormatHandle(item.Hwnd))
                    .Append(" z=").Append(item.Z)
                    .Append(" topmost=").Append((exStyle & WsExTopmost) != 0 ? '1' : '0')
                    .Append(" visible=").Append(IsWindowVisible(item.Hwnd) ? '1' : '0')
                    .Append(" owner=").Append(FormatHandle(owner))
                    .Append(" ownerZ=").Append(ownerZ)
                    .Append(" relation=").Append(relation)
                    .Append(" class=").Append(Quote(GetClass(item.Hwnd)))
                    .Append(" title=").Append(Quote(GetTitle(item.Hwnd)))
                    .AppendLine();
            }
        }

        return builder.ToString();
    }

    private static void AppendTarget(
        StringBuilder builder,
        string label,
        IntPtr hwnd,
        ZOrderSnapshot zOrder)
    {
        var z = hwnd != IntPtr.Zero && zOrder.Index.TryGetValue(hwnd, out var index) ? index : -1;
        var owner = hwnd == IntPtr.Zero ? IntPtr.Zero : GetWindow(hwnd, GwOwner);
        var ownerZ = owner != IntPtr.Zero && zOrder.Index.TryGetValue(owner, out var ownerIndex)
            ? ownerIndex
            : -1;
        var exStyle = hwnd == IntPtr.Zero ? 0 : GetWindowLong(hwnd, GwlExStyle);
        builder.Append(label)
            .Append(" hwnd=").Append(FormatHandle(hwnd))
            .Append(" z=").Append(z)
            .Append(" pid=").Append(ProcessIdFor(hwnd))
            .Append(" topmost=").Append((exStyle & WsExTopmost) != 0 ? '1' : '0')
            .Append(" visible=").Append(hwnd != IntPtr.Zero && IsWindowVisible(hwnd) ? '1' : '0')
            .Append(" owner=").Append(FormatHandle(owner))
            .Append(" ownerZ=").Append(ownerZ)
            .Append(" class=").Append(Quote(GetClass(hwnd)))
            .Append(" title=").Append(Quote(GetTitle(hwnd)))
            .AppendLine();
    }

    private static ZOrderSnapshot CaptureZOrder()
    {
        var windows = new List<IntPtr>(256);
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var index = new Dictionary<IntPtr, int>();
        for (var i = 0; i < windows.Count; i++)
        {
            index[windows[i]] = i;
        }
        return new ZOrderSnapshot(windows, index);
    }

    private static string StateFingerprint(TraceState state) =>
        $"{state.Foreground.ToInt64():X}:{state.Avoidance.ToInt64():X}:" +
        $"{state.Tracked.ToInt64():X}:{state.Session.ToInt64():X}";

    private static FieldInfo? DetectorField(string name) =>
        DetectorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static FieldInfo? ControllerField(string name) =>
        typeof(AppController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

    private static IntPtr ReadHandle(FieldInfo? field, object? owner)
    {
        try
        {
            return field?.GetValue(owner) is IntPtr handle ? handle : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static uint ProcessIdFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static string GetClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }
        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : "";
    }

    private static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }
        var length = Math.Min(GetWindowTextLength(hwnd), 256);
        if (length <= 0)
        {
            return "";
        }
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string Quote(string value)
    {
        value = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        if (value.Length > 100)
        {
            value = value[..100] + "...";
        }
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatHandle(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    private static void EnqueueLine(string text)
    {
        PendingLines.Enqueue(text.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? text
            : text + Environment.NewLine);
    }

    private static void FlushPendingLines()
    {
        if (PendingLines.IsEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        while (PendingLines.TryDequeue(out var line))
        {
            builder.Append(line);
            if (builder.Length >= 256 * 1024)
            {
                break;
            }
        }
        if (builder.Length == 0)
        {
            return;
        }

        lock (FileGate)
        {
            try
            {
                RotateLogIfNeeded();
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging failure must not affect PaperTodo.
            }
        }
    }

    private static void RotateLogAtStartup()
    {
        lock (FileGate)
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    File.Move(LogPath, PreviousLogPath, overwrite: true);
                }
            }
            catch
            {
                // A locked previous log is not fatal.
            }
        }
    }

    private static void RotateLogIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }
        File.Move(LogPath, PreviousLogPath, overwrite: true);
    }

    private static void Shutdown()
    {
        try
        {
            _timer?.Dispose();
            _timer = null;
            if (_foregroundHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_reorderHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_reorderHook);
                _reorderHook = IntPtr.Zero;
            }
            FlushPendingLines();
        }
        catch
        {
            // Process exit continues regardless of diagnostic cleanup.
        }
    }

    private sealed record TraceState(
        IntPtr Foreground,
        IntPtr Tracked,
        IntPtr Session,
        IntPtr Avoidance);

    private sealed record ZOrderSnapshot(
        IReadOnlyList<IntPtr> Windows,
        IReadOnlyDictionary<IntPtr, int> Index);

    private sealed record EventPayload(
        uint EventType,
        IntPtr Hwnd,
        int ObjectId,
        int ChildId,
        uint EventThread,
        uint EventTime);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHook,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);
}
