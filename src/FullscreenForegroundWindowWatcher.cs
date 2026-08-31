using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// Signals when the foreground window changes, its top-level bounds change, or the foreground
/// process creates/shows another top-level window. The callback may be raised outside the WPF
/// dispatcher; AppController owns debouncing and dispatcher marshaling.
/// </summary>
internal sealed class FullscreenForegroundWindowWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GaRoot = 2;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;

    private readonly Action<bool> _changed;
    private readonly WinEventProc _callback;
    private IntPtr _foregroundHook;
    private IntPtr _showCreateHook;
    private IntPtr _locationHook;
    private bool _disposed;

    public FullscreenForegroundWindowWatcher(Action<bool> changed)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _callback = HandleWinEvent;
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        _showCreateHook = SetWinEventHook(
            EventObjectCreate,
            EventObjectShow,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        _locationHook = SetWinEventHook(
            EventObjectLocationChange,
            EventObjectLocationChange,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
    }

    private void HandleWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType == EventSystemForeground)
        {
            _changed(false);
            return;
        }

        if (idObject != ObjidWindow || idChild != ChildidSelf)
        {
            return;
        }

        if (eventType == EventObjectLocationChange && hwnd == GetForegroundWindow())
        {
            _changed(false);
            return;
        }

        if ((eventType == EventObjectCreate || eventType == EventObjectShow) &&
            IsTopLevelWindowFromForegroundProcess(hwnd))
        {
            // A normal foreground window can create a separate fullscreen rendering/presentation
            // HWND. Force the session scan after debounce instead of waiting for the 5-second
            // fallback scan.
            _changed(true);
        }
    }

    private static bool IsTopLevelWindowFromForegroundProcess(IntPtr hwnd)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || GetAncestor(hwnd, GaRoot) != hwnd)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        _ = GetWindowThreadProcessId(hwnd, out var windowProcessId);
        return foregroundProcessId != 0 &&
               foregroundProcessId != (uint)Environment.ProcessId &&
               windowProcessId == foregroundProcessId;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_foregroundHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        if (_showCreateHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_showCreateHook);
            _showCreateHook = IntPtr.Zero;
        }
        if (_locationHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
