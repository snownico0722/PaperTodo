#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PaperTodo;

/// <summary>
/// Opt-in observation of existing Dispatcher/MIL messages. Queue ages use millisecond units
/// with coarse system-clock precision; a MIL notification can drain several channel messages.
/// This observer never posts messages, requests rendering, marks a message handled, or changes
/// WPF state.
/// </summary>
internal static class EdgeMessageLatencyObservation
{
    private static readonly HashSet<IntPtr> Handles = new();
    private static readonly SubclassProc Callback = OnNativeMessage;
    private static readonly UIntPtr SubclassId = new(0x50544D51u);
    private static readonly bool ObserveDwm =
        Environment.GetEnvironmentVariable("PAPERTODO_EDGE_DWM_OBSERVATIONS") == "1";
    private static uint _channelMessage, _dispatcherMessage, _thread;
    private static bool _installed;
    private static long _nextId;
    [ThreadStatic] private static long _dispatch;

    internal static void Install()
    {
        if (_installed || !EdgeDiagnosticJournal.Enabled ||
            Environment.GetEnvironmentVariable("PAPERTODO_EDGE_MESSAGE_OBSERVATIONS") != "1") return;
        try
        {
            _channelMessage = RegisterWindowMessage("MilChannelNotify");
            _dispatcherMessage = RegisterWindowMessage("DispatcherProcessQueue");
            if (_channelMessage == 0 || _dispatcherMessage == 0)
            {
                Emit("deep.message.unavailable");
                return;
            }
            _thread = GetCurrentThreadId();
            ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
            _installed = true;
            Emit("deep.message.installed", 0, _channelMessage, _dispatcherMessage, _thread);
        }
        catch (Exception error) { Emit("deep.message.error", detail: "install:" + error.GetType().Name); }
    }

    internal static void Remove()
    {
        if (!_installed) return;
        // Installation and removal belong to the existing UI thread.
        if (GetCurrentThreadId() != _thread) return;
        ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
        _installed = false;
        foreach (var handle in Handles) RemoveWindowSubclass(handle, Callback, SubclassId);
        Handles.Clear();
        Emit("deep.message.removed");
    }

    private static void OnThreadMessage(ref MSG message, ref bool handled)
    {
        try
        {
            if (!_installed || (message.message != _channelMessage &&
                message.message != _dispatcherMessage && message.message != 0x0113)) return;
            var id = Interlocked.Increment(ref _nextId);
            // Use the Win32 clock that MSG.time belongs to. Environment.TickCount switches
            // to a different, sleep-excluding time domain starting in .NET 11.
            var now = unchecked((int)GetTickCount());
            // Both values are low 32-bit uptime milliseconds. Unsigned subtraction preserves
            // the wrap boundary; this is queue age, not QPC or requested presentation time.
            var age = unchecked((uint)(now - message.time));
            Emit("deep.message.queued", id, message.hwnd.ToInt64(), message.message,
                message.time, now, age, handled ? 1 : 0);
            if (message.message == _channelMessage && !handled)
                ObserveChannelWindow(message.hwnd);
        }
        catch (Exception error) { Emit("deep.message.error", detail: "filter:" + error.GetType().Name); }
    }

    private static void ObserveChannelWindow(IntPtr handle)
    {
        if (Handles.Contains(handle)) return;
        var thread = GetWindowThreadProcessId(handle, out var process);
        if (thread != _thread || process != Environment.ProcessId || Handles.Count >= 8)
        {
            Emit("deep.message.window-skipped", handle.ToInt64(), thread, process);
            return;
        }
        if (SetWindowSubclass(handle, Callback, SubclassId, UIntPtr.Zero))
        {
            Handles.Add(handle);
            Emit("deep.message.window-installed", handle.ToInt64(), thread, process);
        }
        else Emit("deep.message.install-failed", handle.ToInt64(), Marshal.GetLastWin32Error());
    }

    private static IntPtr OnNativeMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message != _channelMessage)
        {
            if (message == 0x0082)
            {
                RemoveWindowSubclass(handle, Callback, SubclassId);
                Handles.Remove(handle);
            }
            return DefSubclassProc(handle, message, wParam, lParam);
        }
        var id = Interlocked.Increment(ref _nextId);
        var parent = _dispatch;
        var started = Stopwatch.GetTimestamp();
        _dispatch = id;
        Emit("deep.message.channel.begin", id, handle.ToInt64(), message, parent);
        if (ObserveDwm) CaptureDwmTiming(id);
        EdgeDispatcherLatencyObservation.Snapshot("channel-message-before");
        var forwardStarted = Stopwatch.GetTimestamp();
        try { return DefSubclassProc(handle, message, wParam, lParam); }
        finally
        {
            var forwardEnded = Stopwatch.GetTimestamp();
            EdgeDispatcherLatencyObservation.Snapshot("channel-message-after");
            _dispatch = parent;
            Emit("deep.message.channel.end", id, Stopwatch.GetTimestamp() - started, handle.ToInt64(), message,
                forwardEnded - forwardStarted);
        }
    }

    private static void Emit(string name, long id = 0, long value1 = 0, long value2 = 0,
        long value3 = 0, long value4 = 0, double number1 = 0, double number2 = 0, string? detail = null)
    {
        try { EdgeCapsulePerformanceDiagnostics.Event(name, id, value1, value2, value3, value4, number1, number2, detail); }
        catch { /* The original message chain must always proceed. */ }
    }

    private static void CaptureDwmTiming(long id)
    {
        try
        {
            var info = new DwmTimingInfo { Size = 292 };
            var before = Stopwatch.GetTimestamp();
            var result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref info);
            var after = Stopwatch.GetTimestamp();
            Emit("deep.message.dwm-call", id, before, after, result);
            if (result < 0) return;
            Emit("deep.message.dwm-clock", id, unchecked((long)info.QpcVBlank),
                unchecked((long)info.QpcRefreshPeriod), unchecked((long)info.QpcCompose), after);
            Emit("deep.message.dwm-rate", id, info.RefreshNumerator, info.RefreshDenominator,
                info.ComposeNumerator, info.ComposeDenominator);
        }
        catch (Exception error) { Emit("deep.message.error", detail: "dwm-read:" + error.GetType().Name); }
    }

    // dwmapi.h includes pshpack1.h. Reserve the complete 292-byte DWM_TIMING_INFO,
    // although this passive probe only reads its timing prefix. Windows 8.1+ requires HWND=NULL.
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 292)]
    private struct DwmTimingInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint RefreshNumerator;
        [FieldOffset(8)] public uint RefreshDenominator;
        [FieldOffset(12)] public ulong QpcRefreshPeriod;
        [FieldOffset(20)] public uint ComposeNumerator;
        [FieldOffset(24)] public uint ComposeDenominator;
        [FieldOffset(28)] public ulong QpcVBlank;
        [FieldOffset(48)] public ulong QpcCompose;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo timing);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr handle, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr handle, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
#endif
