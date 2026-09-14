using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// Native output target for one active queue transaction. WS_EX_NOREDIRECTIONBITMAP keeps this
/// window from allocating another full RGBA backing surface; DirectComposition supplies every
/// visible pixel.
/// </summary>
internal sealed class EdgeCapsuleQueueProxyWindow : IDisposable
{
    private const int GwlWndProc = -4;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmDestroy = 0x0002;
    private const int WmNcDestroy = 0x0082;
    private const int WmDisplayChange = 0x007E;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmDpiChanged = 0x02E0;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Dictionary<IntPtr, EdgeCapsuleQueueProxyWindow> Instances = new();
    private static readonly WndProc WindowProcedure = DispatchWindowMessage;
    private static readonly IntPtr WindowProcedurePointer =
        Marshal.GetFunctionPointerForDelegate(WindowProcedure);

    private readonly Func<DeviceScreenPoint, bool> _containsVisual;
    private readonly Action<EdgeCapsulePointerDown> _interactionRequested;
    private readonly Action _environmentChanged;
    private readonly Action _compositionInvalidated;
    private readonly Action _outputLost;
    private IntPtr _previousWindowProcedure;
    private bool _disposed;
    private bool _disposing;

    private EdgeCapsuleQueueProxyWindow(
        IntPtr handle,
        Func<DeviceScreenPoint, bool> containsVisual,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        Handle = handle;
        _containsVisual = containsVisual;
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _compositionInvalidated = compositionInvalidated;
        _outputLost = outputLost;
    }

    public IntPtr Handle { get; private set; }

    public static EdgeCapsuleQueueProxyWindow? TryCreate(
        DeviceScreenRect bounds,
        bool topmost,
        Func<DeviceScreenPoint, bool> containsVisual,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        if (bounds.IsEmpty)
        {
            return null;
        }

        var exStyle = WsExToolWindow |
            WsExNoActivate |
            WsExNoRedirectionBitmap |
            (topmost ? WsExTopmost : 0);
        var handle = CreateWindowEx(
            exStyle,
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
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var window = new EdgeCapsuleQueueProxyWindow(
            handle,
            containsVisual,
            interactionRequested,
            environmentChanged,
            compositionInvalidated,
            outputLost);
        lock (Instances)
        {
            Instances[handle] = window;
        }
        window._previousWindowProcedure = SetWindowLongPtr(
            handle,
            GwlWndProc,
            WindowProcedurePointer);
        if (window._previousWindowProcedure == IntPtr.Zero)
        {
            window.Dispose();
            return null;
        }
        return window;
    }

    public bool Show(DeviceScreenRect bounds, bool topmost)
    {
        if (_disposed || Handle == IntPtr.Zero || bounds.IsEmpty)
        {
            return false;
        }
        // SWP_SHOWWINDOW and SWP_NOACTIVATE publish position, size and visibility together.
        // A second ShowWindow request would repeat that native publication on the cold path.
        return SetWindowPos(
            Handle,
            topmost ? HwndTopmost : HwndTop,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
    }

    public void Hide()
    {
        if (!_disposed && Handle != IntPtr.Zero)
        {
            _ = ShowWindow(Handle, SwHide);
        }
    }

    private IntPtr WindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                {
                    var packed = lParam.ToInt64();
                    var point = new DeviceScreenPoint(
                        unchecked((short)(packed & 0xFFFF)),
                        unchecked((short)((packed >> 16) & 0xFFFF)));
                    return new IntPtr(_containsVisual(point) ? HtClient : HtTransparent);
                }
                case WmMouseActivate:
                    return new IntPtr(MaNoActivate);
                case WmEraseBackground:
                    return new IntPtr(1);
                case WmPaint:
                    _compositionInvalidated();
                    _ = ValidateRect(hwnd, IntPtr.Zero);
                    return IntPtr.Zero;
                case WmLButtonDown:
                case WmRButtonDown:
                case WmMiddleButtonDown:
                    var packedPoint = lParam.ToInt64();
                    var cursor = new CursorPoint
                    {
                        X = unchecked((short)(packedPoint & 0xFFFF)),
                        Y = unchecked((short)((packedPoint >> 16) & 0xFFFF))
                    };
                    if (ClientToScreen(hwnd, ref cursor))
                    {
                        _interactionRequested(new EdgeCapsulePointerDown(
                            new DeviceScreenPoint(cursor.X, cursor.Y), message, wParam));
                    }
                    return IntPtr.Zero;
                case WmDpiChanged:
                case WmDisplayChange:
                    _environmentChanged();
                    return IntPtr.Zero;
                case WmDestroy:
                    break;
                case WmNcDestroy:
                    var destroyedResult = _previousWindowProcedure != IntPtr.Zero
                        ? CallWindowProc(
                            _previousWindowProcedure,
                            hwnd,
                            message,
                            wParam,
                            lParam)
                        : DefWindowProc(hwnd, message, wParam, lParam);
                    lock (Instances)
                    {
                        Instances.Remove(hwnd);
                    }
                    if (Handle == hwnd)
                    {
                        Handle = IntPtr.Zero;
                    }
                    if (!_disposing)
                    {
                        // The output itself is gone, unlike an ordinary display/DPI change where
                        // the last proxy frame can safely remain as a handoff cover.
                        _outputLost();
                    }
                    return destroyedResult;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "Edge capsule queue proxy window callback failed. Message=0x{0:X}; Exception={1}",
                message,
                ex);
            return message == WmNcHitTest
                ? new IntPtr(HtTransparent)
                : IntPtr.Zero;
        }

        return _previousWindowProcedure != IntPtr.Zero
            ? CallWindowProc(
                _previousWindowProcedure,
                hwnd,
                message,
                wParam,
                lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static IntPtr DispatchWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam)
    {
        EdgeCapsuleQueueProxyWindow? window;
        lock (Instances)
        {
            Instances.TryGetValue(hwnd, out window);
        }
        return window?.WindowMessage(hwnd, message, wParam, lParam) ??
            DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed || _disposing)
        {
            return;
        }
        _disposing = true;
        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            _disposed = true;
            _disposing = false;
            return;
        }
        _ = ShowWindow(handle, SwHide);
        if (_previousWindowProcedure != IntPtr.Zero)
        {
            if (SetWindowLongPtr(handle, GwlWndProc, _previousWindowProcedure) == IntPtr.Zero)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Failed to restore edge capsule queue proxy WndProc for HWND 0x{0:X}.",
                    handle.ToInt64());
            }
        }
        if (!DestroyWindow(handle))
        {
            System.Diagnostics.Trace.TraceWarning(
                "Failed to destroy edge capsule queue proxy HWND 0x{0:X}.",
                handle.ToInt64());
            _disposing = false;
            return;
        }
        lock (Instances)
        {
            Instances.Remove(handle);
        }
        Handle = IntPtr.Zero;
        _disposed = true;
        _disposing = false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr hwnd,
        int index,
        IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previous,
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
