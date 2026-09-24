#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>Opt-in native call/message timing. Subclasses only this thread's own HWNDs and
/// forwards each message exactly once without changing parameters, results or window state.</summary>
internal static class EdgeNativeLatencyObservation
{
    private static readonly bool Requested =
        Environment.GetEnvironmentVariable("PAPERTODO_EDGE_DEEP_OBSERVATIONS") == "1";
    private static readonly HashSet<IntPtr> Handles = new();
    private static readonly SubclassProc Callback = OnMessage;
    private static readonly UIntPtr SubclassId = new(0x50544C41u);
    private static long _nextId;
    [ThreadStatic] private static long _batch;
    [ThreadStatic] private static long _message;
    internal static bool Enabled => Requested && EdgeDiagnosticJournal.Enabled;

    internal readonly struct BatchScope : IDisposable
    {
        private readonly long _id, _previous, _started;
        private readonly ulong _cycles;
        private readonly long _kernel, _user;

        internal BatchScope(long id, long previous)
        {
            _id = id;
            _previous = previous;
            _started = Stopwatch.GetTimestamp();
            QueryThreadCycleTime(GetCurrentThread(), out _cycles);
            GetTimes(out _kernel, out _user);
            _batch = id;
            Record("native.deep.begin", id, GetCurrentThreadId(), previous);
        }

        public void Dispose()
        {
            if (_id == 0) return;
            var ended = Stopwatch.GetTimestamp();
            QueryThreadCycleTime(GetCurrentThread(), out var cycles);
            GetTimes(out var kernel, out var user);
            _batch = _previous;
            Record("native.deep.end", _id, ended - _started,
                unchecked((long)(cycles - _cycles)), kernel - _kernel, user - _user);
        }
    }

    internal static BatchScope BeginBatch(IEnumerable<IntPtr> handles)
    {
        if (!Enabled) return default;
        foreach (var handle in handles)
        {
            if (Handles.Contains(handle)) continue;
            var thread = GetWindowThreadProcessId(handle, out var process);
            if (thread != GetCurrentThreadId() || process != Environment.ProcessId || Handles.Count >= 256)
            {
                Record("native.deep.skipped", handle.ToInt64(), thread, process);
                continue;
            }
            if (SetWindowSubclass(handle, Callback, SubclassId, UIntPtr.Zero))
            {
                Handles.Add(handle);
                Record("native.deep.installed", handle.ToInt64(), thread, process);
            }
            else Record("native.deep.install-failed", handle.ToInt64(), Marshal.GetLastWin32Error());
        }
        return new BatchScope(Interlocked.Increment(ref _nextId), _batch);
    }

    internal static void Remove()
    {
        foreach (var handle in Handles)
            RemoveWindowSubclass(handle, Callback, SubclassId);
        Handles.Clear();
    }

    private static IntPtr OnMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        var batch = _batch;
        if (batch == 0)
        {
            if (message == 0x0082)
            {
                RemoveWindowSubclass(handle, Callback, SubclassId);
                Handles.Remove(handle);
            }
            return DefSubclassProc(handle, message, wParam, lParam);
        }
        var id = Interlocked.Increment(ref _nextId);
        var parent = _message;
        var started = Stopwatch.GetTimestamp();
        _message = id;
        Record("native.message.begin", id, batch, handle.ToInt64(), message, parent);
        try
        {
            return DefSubclassProc(handle, message, wParam, lParam);
        }
        finally
        {
            _message = parent;
            Record("native.message.end", id, Stopwatch.GetTimestamp() - started,
                batch, handle.ToInt64(), message);
            if (message == 0x0082)
            {
                RemoveWindowSubclass(handle, Callback, SubclassId);
                Handles.Remove(handle);
            }
        }
    }

    private static void GetTimes(out long kernel, out long user)
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out kernel, out user);
    }

    private static void Record(string name, long id, long value1 = 0, long value2 = 0,
        long value3 = 0, long value4 = 0)
    {
        try { EdgeCapsulePerformanceDiagnostics.Event(name, id, value1, value2, value3, value4); }
        catch { /* Observations must not interrupt the native message chain. */ }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr handle, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr handle, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit,
        out long kernel, out long user);
}
#endif
