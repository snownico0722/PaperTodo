using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// UI-owned DirectComposition output plus a dedicated-thread, non-drawing native input window.
/// The output stays permanently mouse-transparent. The input HWND owns only a finite HRGN and
/// publishes that region on its own message loop so DComp translation does not depend on the WPF
/// Dispatcher being available.
/// </summary>
internal sealed class EdgeCapsuleQueueProxyWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintState
    {
        public IntPtr DeviceContext;
        public int Erase;
        public int Left, Top, Right, Bottom;
        public int Restore, IncrementalUpdate;
        private uint _reserved0, _reserved1, _reserved2, _reserved3;
        private uint _reserved4, _reserved5, _reserved6, _reserved7;
    }

    private const int GwlWndProc = -4;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmDestroy = 0x0002;
    private const int WmNcDestroy = 0x0082;
    private const int WmDisplayChange = 0x007E;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmDpiChanged = 0x02E0;
    private const int WmInputInteraction = 0x8000 + 0x732;
    private const int WmInputEnvironment = 0x8000 + 0x733;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint InputHandoffTimeoutMilliseconds = 20;
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Dictionary<IntPtr, EdgeCapsuleQueueProxyWindow> Instances = new();
    private static readonly WndProc WindowProcedure = DispatchWindowMessage;
    private static readonly IntPtr WindowProcedurePointer = Marshal.GetFunctionPointerForDelegate(WindowProcedure);

    private readonly Action<EdgeCapsulePointerDown> _interactionRequested;
    private readonly Action _environmentChanged;
    private readonly Action _compositionInvalidated;
    private readonly Action _outputLost;
    private readonly ConcurrentDictionary<long, EdgeCapsulePointerDown> _pendingInput = new();
    private EdgeCapsuleQueueDedicatedInputOwner? _inputOwner;
    private IntPtr _previousWindowProcedure;
    private DeviceScreenRect _bounds;
    private long _nextInputToken;
    private long _mutationVersion;
    private bool _shown;
    private bool _disposed;
    private bool _disposing;

    private EdgeCapsuleQueueProxyWindow(
        IntPtr handle,
        DeviceScreenRect bounds,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        Handle = handle;
        _bounds = bounds;
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _compositionInvalidated = compositionInvalidated;
        _outputLost = outputLost;
    }

    public IntPtr Handle { get; private set; }
    internal IntPtr InputHandle => _inputOwner?.Handle ?? IntPtr.Zero;
    internal uint InputOwnerThreadId => _inputOwner?.ThreadId ?? 0;
    internal bool IsInputAnimationActive => _inputOwner?.IsAnimationActive == true;
    internal int InputRegionUpdateCount => _inputOwner?.RegionUpdateCount ?? 0;

    public static EdgeCapsuleQueueProxyWindow? TryCreate(
        DeviceScreenRect bounds,
        bool topmost,
        Func<DeviceScreenPoint, bool> containsVisual,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        if (!ValidBounds(bounds)) return null;

        // containsVisual remains in the API because call sites already provide it, but the physical
        // input HRGN is now the sole native hit truth. Re-evaluating mutable WPF state on the native
        // owner thread would create a second racing presentation model.
        GC.KeepAlive(containsVisual);

        var exStyle = WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap |
            (topmost ? WsExTopmost : 0);
        var handle = CreateWindowEx(
            exStyle | WsExLayered | WsExTransparent,
            "Static",
            string.Empty,
            WsPopup,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (handle == IntPtr.Zero) return null;

        var window = new EdgeCapsuleQueueProxyWindow(
            handle,
            bounds,
            interactionRequested,
            environmentChanged,
            compositionInvalidated,
            outputLost);
        lock (Instances) Instances[handle] = window;
        window._previousWindowProcedure = SetWindowLongPtr(handle, GwlWndProc, WindowProcedurePointer);
        if (window._previousWindowProcedure == IntPtr.Zero ||
            !SetLayeredWindowAttributes(handle, 0, 255, LwaAlpha))
        {
            window.Dispose();
            return null;
        }

        window._inputOwner = EdgeCapsuleQueueDedicatedInputOwner.TryCreate(
            bounds,
            topmost,
            window.ForwardInputFromNativeOwner,
            window.ForwardEnvironmentFromNativeOwner);
        if (window._inputOwner == null)
        {
            window.Dispose();
            return null;
        }
        return window;
    }

    public bool Show(DeviceScreenRect bounds, bool topmost)
    {
        if (_disposed || _disposing || Handle == IntPtr.Zero || _inputOwner == null ||
            !ValidBounds(bounds)) return false;
        var version = ++_mutationVersion;
        var output = Handle;
        if (!SetWindowPos(
                output,
                topmost ? HwndTopmost : HwndNotTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder) ||
            !IsCurrent(version, output))
            return false;
        if (!_inputOwner.Show(bounds, topmost) || !IsCurrent(version, output))
        {
            _ = ShowWindow(output, SwHide);
            return false;
        }
        _bounds = bounds;
        _shown = true;
        return true;
    }

    public void Hide()
    {
        if (_disposed || _disposing) return;
        var version = ++_mutationVersion;
        var output = Handle;
        _shown = false;
        _inputOwner?.Hide();
        if (!IsCurrent(version, output)) return;
        if (output != IntPtr.Zero) _ = ShowWindow(output, SwHide);
    }

    internal bool TrySetInputRegions(IReadOnlyList<DeviceScreenRect> screenBounds)
    {
        ArgumentNullException.ThrowIfNull(screenBounds);
        if (_disposed || _disposing || Handle == IntPtr.Zero || _inputOwner == null) return false;
        return _inputOwner.TrySetRegions(screenBounds);
    }

    internal bool TryStartInputAnimation(EdgeCapsuleQueueInputAnimationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (_disposed || _disposing || !_shown || Handle == IntPtr.Zero || _inputOwner == null)
            return false;
        return _inputOwner.TryStartAnimation(ticket);
    }

    internal bool CancelInputAnimationAndClear() =>
        !_disposed && !_disposing && _inputOwner?.CancelAnimationAndClear() == true;

    private bool IsCurrent(long version, IntPtr output) =>
        !_disposed && !_disposing && _mutationVersion == version && Handle == output;

    private void ForwardInputFromNativeOwner(EdgeCapsulePointerDown input)
    {
        var output = Handle;
        if (_disposed || _disposing || output == IntPtr.Zero) return;
        var token = Interlocked.Increment(ref _nextInputToken);
        _pendingInput[token] = input;
        try
        {
            // A press is allowed to synchronously request the existing UI-owned handoff only when
            // that owner is responsive now. If WPF is stalled, time out and swallow the press;
            // never replay it later and never block the native animation timer behind the UI pump.
            _ = SendMessageTimeout(
                output,
                WmInputInteraction,
                new IntPtr(token),
                IntPtr.Zero,
                SmtoBlock | SmtoAbortIfHung,
                InputHandoffTimeoutMilliseconds,
                out _);
        }
        finally
        {
            _pendingInput.TryRemove(token, out _);
        }
    }

    private void ForwardEnvironmentFromNativeOwner()
    {
        var output = Handle;
        if (_disposed || _disposing || output == IntPtr.Zero) return;
        _ = PostMessage(output, WmInputEnvironment, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                    return new IntPtr(HtTransparent);
                case WmMouseActivate:
                    return new IntPtr(MaNoActivate);
                case WmEraseBackground:
                    return new IntPtr(1);
                case WmPaint:
                    _ = BeginPaint(hwnd, out var paint);
                    try { _compositionInvalidated(); }
                    finally { _ = EndPaint(hwnd, ref paint); }
                    return IntPtr.Zero;
                case WmInputInteraction:
                    if (_pendingInput.TryRemove(wParam.ToInt64(), out var input))
                        _interactionRequested(input);
                    return IntPtr.Zero;
                case WmInputEnvironment:
                    _environmentChanged();
                    return IntPtr.Zero;
                case WmDpiChanged:
                case WmDisplayChange:
                    _ = _inputOwner?.CancelAnimationAndClear();
                    _environmentChanged();
                    return IntPtr.Zero;
                case WmDestroy:
                    break;
                case WmNcDestroy:
                    var destroyedResult = _previousWindowProcedure != IntPtr.Zero
                        ? CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam)
                        : DefWindowProc(hwnd, message, wParam, lParam);
                    lock (Instances) Instances.Remove(hwnd);
                    ++_mutationVersion;
                    Handle = IntPtr.Zero;
                    _inputOwner?.Hide();
                    _pendingInput.Clear();
                    if (!_disposing) _outputLost();
                    return destroyedResult;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "Edge capsule queue output callback failed. Message=0x{0:X}; Exception={1}",
                message,
                ex);
            return message == WmNcHitTest ? new IntPtr(HtTransparent) : IntPtr.Zero;
        }

        return _previousWindowProcedure != IntPtr.Zero
            ? CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static IntPtr DispatchWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam)
    {
        EdgeCapsuleQueueProxyWindow? window;
        lock (Instances) Instances.TryGetValue(hwnd, out window);
        return window?.WindowMessage(hwnd, message, wParam, lParam) ??
            DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        ++_mutationVersion;
        _shown = false;
        try
        {
            _pendingInput.Clear();
            try { _inputOwner?.Dispose(); } catch { }
            _inputOwner = null;
            if (DestroyOwnedWindow(Handle, _previousWindowProcedure)) Handle = IntPtr.Zero;
            _disposed = Handle == IntPtr.Zero;
        }
        finally
        {
            _disposing = false;
        }
    }

    private static bool DestroyOwnedWindow(IntPtr handle, IntPtr previous)
    {
        if (handle == IntPtr.Zero) return true;
        _ = ShowWindow(handle, SwHide);
        if (previous != IntPtr.Zero && SetWindowLongPtr(handle, GwlWndProc, previous) == IntPtr.Zero)
            System.Diagnostics.Trace.TraceWarning("Failed to restore proxy WndProc for HWND 0x{0:X}.", handle.ToInt64());
        if (!DestroyWindow(handle))
        {
            System.Diagnostics.Trace.TraceWarning("Failed to destroy proxy HWND 0x{0:X}.", handle.ToInt64());
            return false;
        }
        lock (Instances) Instances.Remove(handle);
        return true;
    }

    private static bool ValidBounds(DeviceScreenRect bounds) =>
        (long)bounds.Right - bounds.Left is > 0 and <= 0x03ffffff &&
        (long)bounds.Bottom - bounds.Top is > 0 and <= 0x03ffffff;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintState paint);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintState paint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}