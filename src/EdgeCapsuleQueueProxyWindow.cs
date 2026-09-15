using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// DirectComposition output and a separate, non-drawing input window. The output is always
/// mouse-transparent; the input HWND's OS region, not HTTRANSPARENT across threads, excludes holes.
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

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintState paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintState paint);

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
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonDoubleClick = 0x0206;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonDoubleClick = 0x0209;
    private const int WmDpiChanged = 0x02E0;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const uint LwaAlpha = 0x00000002;
    private const int RgnOr = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndNotTopmost = new(-2);
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
    private IntPtr _previousInputWindowProcedure;
    private DeviceScreenRect _bounds;
    private bool _boundsKnown = true;
    private DeviceScreenRect[]? _inputRegions;
    private DeviceScreenRect[]? _screenInputRegions;
    private DeviceScreenRect _screenInputBounds;
    private long _mutationVersion;
    private bool _positioning;
    private bool _shown;
    private bool _topmost;
    private bool _inputHiddenForFailure;
    private bool _disposed;
    private bool _disposing;

    private EdgeCapsuleQueueProxyWindow(
        IntPtr handle,
        DeviceScreenRect bounds,
        Func<DeviceScreenPoint, bool> containsVisual,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        Handle = handle;
        _bounds = bounds;
        _containsVisual = containsVisual;
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _compositionInvalidated = compositionInvalidated;
        _outputLost = outputLost;
    }

    public IntPtr Handle { get; private set; }
    internal IntPtr InputHandle { get; private set; }

    public static EdgeCapsuleQueueProxyWindow? TryCreate(
        DeviceScreenRect bounds,
        bool topmost,
        Func<DeviceScreenPoint, bool> containsVisual,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged,
        Action compositionInvalidated,
        Action outputLost)
    {
        if (!ValidBounds(bounds))
        {
            return null;
        }

        var exStyle = WsExToolWindow |
            WsExNoActivate |
            WsExNoRedirectionBitmap |
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
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var window = new EdgeCapsuleQueueProxyWindow(
            handle,
            bounds,
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
        // WS_EX_TRANSPARENT alone only documents same-thread paint ordering. Layered windows
        // explicitly pass mouse input underneath, and DComp supports layered HWND targets.
        // Keep alpha 255: transparency of the displayed content still comes from the live visual.
        if (!SetLayeredWindowAttributes(handle, 0, 255, LwaAlpha))
        {
            window.Dispose();
            return null;
        }
        var input = CreateWindowEx(exStyle, "Static", string.Empty, WsPopup,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (input == IntPtr.Zero)
        {
            window.Dispose();
            return null;
        }
        window.InputHandle = input;
        lock (Instances) Instances[input] = window;
        window._previousInputWindowProcedure = SetWindowLongPtr(input, GwlWndProc, WindowProcedurePointer);
        // An actual empty HRGN is required. A null HRGN would restore the whole output rectangle.
        if (window._previousInputWindowProcedure == IntPtr.Zero ||
            !window.TrySetInputRegions(Array.Empty<DeviceScreenRect>()))
        {
            window.Dispose();
            return null;
        }
        return window;
    }

    public bool Show(DeviceScreenRect bounds, bool topmost)
    {
        if (_disposed || _disposing || _positioning || Handle == IntPtr.Zero ||
            InputHandle == IntPtr.Zero || !ValidBounds(bounds))
        {
            return false;
        }
        var version = ++_mutationVersion;
        var output = Handle;
        var input = InputHandle;
        _positioning = true;
        try
        {
            // A pooled HWND cannot carry old screen-space input into another queue position.
            // Publication supplies the new presented regions after both windows have moved.
            if (bounds != _bounds || _inputRegions == null)
            {
                _screenInputRegions = null;
                if (!SetInputRegionsCore(Array.Empty<DeviceScreenRect>(), version, output, input)) return false;
            }
            if (!SetWindowPos(output, topmost ? HwndTopmost : HwndNotTopmost,
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder) ||
                !IsCurrent(version, output, input))
            { _screenInputRegions = null; return false; }
            _boundsKnown = false;
            if (!SetWindowPos(input, topmost ? HwndTopmost : HwndNotTopmost,
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
            {
                FailInput(version, output, input);
                return false;
            }
            if (!IsCurrent(version, output, input)) return false;
            _bounds = bounds;
            _boundsKnown = true;
            _topmost = topmost;
            _shown = true;
            _inputHiddenForFailure = false;
            return true;
        }
        finally { _positioning = false; }
    }

    public void Hide()
    {
        if (_disposed || _disposing) return;
        var version = ++_mutationVersion;
        var output = Handle;
        var input = InputHandle;
        _shown = false;
        _screenInputRegions = null;
        if (input != IntPtr.Zero) _ = ShowWindow(input, SwHide);
        if (!IsCurrent(version, output, input)) return;
        if (output != IntPtr.Zero) _ = ShowWindow(output, SwHide);
        if (!IsCurrent(version, output, input)) return;
        if (input != IntPtr.Zero) _ = SetInputRegionsCore(Array.Empty<DeviceScreenRect>(), version, output, input);
    }

    internal bool TrySetInputRegions(IReadOnlyList<DeviceScreenRect> screenBounds)
    {
        ArgumentNullException.ThrowIfNull(screenBounds);
        if (_disposed || _disposing || Handle == IntPtr.Zero || InputHandle == IntPtr.Zero ||
            (_positioning && screenBounds.Count != 0)) return false;
        if (!_positioning && !_boundsKnown)
        {
            // A failed/reentrant resize may already have moved the HWND. Do not interpret later
            // screen-space regions against the old requested position in that case.
            if (!GetWindowRect(InputHandle, out var nativeBounds) || !ValidBounds(nativeBounds.ToDevice()))
            {
                FailInput(++_mutationVersion, Handle, InputHandle);
                return false;
            }
            _bounds = nativeBounds.ToDevice();
            _boundsKnown = true;
            _inputRegions = null;
            _screenInputRegions = null;
        }
        // The common retained/unchanged tick must neither allocate nor call GDI. This is only a
        // successful native-command cache, not another presentation or pointer authority.
        if (!_inputHiddenForFailure && _screenInputBounds == _bounds &&
            _screenInputRegions is { } cached && cached.Length == screenBounds.Count)
        {
            var same = true;
            for (var index = 0; index < cached.Length; index++)
                if (cached[index] != screenBounds[index]) { same = false; break; }
            if (same) return true;
        }
        var requested = screenBounds.ToArray();
        var regions = new List<DeviceScreenRect>(requested.Length);
        foreach (var screen in requested)
        {
            var left = Math.Max(screen.Left, _bounds.Left);
            var top = Math.Max(screen.Top, _bounds.Top);
            var right = Math.Min(screen.Right, _bounds.Right);
            var bottom = Math.Min(screen.Bottom, _bounds.Bottom);
            if (right <= left || bottom <= top) continue;
            regions.Add(new DeviceScreenRect(left - _bounds.Left, top - _bounds.Top,
                right - _bounds.Left, bottom - _bounds.Top));
        }
        var normalized = regions.Distinct().OrderBy(rect => rect.Left).ThenBy(rect => rect.Top)
            .ThenBy(rect => rect.Right).ThenBy(rect => rect.Bottom).ToArray();
        if (_inputRegions != null && _inputRegions.AsSpan().SequenceEqual(normalized) && !_inputHiddenForFailure)
        {
            _screenInputRegions = requested;
            _screenInputBounds = _bounds;
            return true;
        }
        var version = ++_mutationVersion;
        var output = Handle;
        var input = InputHandle;
        if (!SetInputRegionsCore(normalized, version, output, input)) return false;
        if (_shown && _inputHiddenForFailure)
        {
            _inputHiddenForFailure = false;
            if (!SetWindowPos(input, _topmost ? HwndTopmost : HwndNotTopmost,
                    _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
                    SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
            {
                FailInput(version, output, input);
                return false;
            }
        }
        if (!IsCurrent(version, output, input)) return false;
        _screenInputRegions = requested;
        _screenInputBounds = _bounds;
        return true;
    }

    private bool SetInputRegionsCore(DeviceScreenRect[] regions, long version, IntPtr output, IntPtr input)
    {
        if (!IsCurrent(version, output, input) || input == IntPtr.Zero) return false;
        if (_inputRegions != null && _inputRegions.AsSpan().SequenceEqual(regions)) return true;
        var union = CreateRectRgn(0, 0, 0, 0);
        if (union == IntPtr.Zero) { FailInput(version, output, input); return false; }
        try
        {
            foreach (var rect in regions)
            {
                var part = CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (part == IntPtr.Zero) { FailInput(version, output, input); return false; }
                try
                {
                    if (CombineRgn(union, union, part, RgnOr) == 0)
                    { FailInput(version, output, input); return false; }
                }
                finally { _ = DeleteObject(part); }
            }
            // SetWindowRgn sends synchronous position messages. A nested request for the old
            // region must not fast-path against a cache that the outer native call is replacing.
            _inputRegions = null;
            _screenInputRegions = null;
            if (SetWindowRgn(input, union, redraw: false) == 0)
            { FailInput(version, output, input); return false; }
            union = IntPtr.Zero; // Ownership transferred even if a synchronous callback replaced us.
            if (!IsCurrent(version, output, input)) return false;
            _inputRegions = regions;
            return true;
        }
        finally { if (union != IntPtr.Zero) _ = DeleteObject(union); }
    }

    private bool IsCurrent(long version, IntPtr output, IntPtr input) =>
        !_disposed && !_disposing && _mutationVersion == version && Handle == output && InputHandle == input;

    private void FailInput(long version, IntPtr output, IntPtr input)
    {
        if (!IsCurrent(version, output, input)) return;
        _inputRegions = null;
        _screenInputRegions = null;
        _inputHiddenForFailure = true;
        // Keep the visual cover for the caller's existing rollback/handoff, but never leave stale
        // input regions intercepting another application after a failed region/placement change.
        if (input != IntPtr.Zero) _ = ShowWindow(input, SwHide);
    }

    private static bool ValidBounds(DeviceScreenRect bounds) =>
        (long)bounds.Right - bounds.Left is > 0 and <= 0x03ffffff &&
        (long)bounds.Bottom - bounds.Top is > 0 and <= 0x03ffffff;

    private IntPtr WindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam)
    {
        var isInput = hwnd == InputHandle;
        var previous = isInput ? _previousInputWindowProcedure : _previousWindowProcedure;
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                {
                    if (!isInput) return new IntPtr(HtTransparent);
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
                    // Complete the native paint cycle even though DComp owns the pixels.
                    // ValidateRect alone does not acknowledge this layered output's paint
                    // request and can leave WM_PAINT spinning on an idle hidden HWND.
                    _ = BeginPaint(hwnd, out var paint);
                    try { if (!isInput) _compositionInvalidated(); }
                    finally { _ = EndPaint(hwnd, ref paint); }
                    return IntPtr.Zero;
                case WmLButtonDown:
                case WmLButtonDoubleClick:
                case WmRButtonDown:
                case WmRButtonDoubleClick:
                case WmMiddleButtonDown:
                case WmMiddleButtonDoubleClick:
                    // A native double click replaces the second DOWN; it is still a press and
                    // must obey the same immediate handoff / drop-on-retry ownership boundary.
                    if (!isInput) return IntPtr.Zero;
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
                    var destroyedResult = previous != IntPtr.Zero
                        ? CallWindowProc(
                            previous,
                            hwnd,
                            message,
                            wParam,
                            lParam)
                        : DefWindowProc(hwnd, message, wParam, lParam);
                    lock (Instances)
                    {
                        Instances.Remove(hwnd);
                    }
                    ++_mutationVersion;
                    if (isInput) { InputHandle = IntPtr.Zero; _inputRegions = null; _screenInputRegions = null; }
                    else if (Handle == hwnd) Handle = IntPtr.Zero;
                    if (!_disposing)
                    {
                        // The output itself is gone, unlike an ordinary display/DPI change where
                        // the last proxy frame can safely remain as a handoff cover.
                        if (isInput) _environmentChanged();
                        else _outputLost();
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

        return previous != IntPtr.Zero
            ? CallWindowProc(
                previous,
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
        ++_mutationVersion;
        _shown = false;
        try
        {
            // A failure retiring one HWND must not prevent hiding/retiring its partner.
            if (DestroyOwnedWindow(InputHandle, _previousInputWindowProcedure)) InputHandle = IntPtr.Zero;
            if (DestroyOwnedWindow(Handle, _previousWindowProcedure)) Handle = IntPtr.Zero;
            _inputRegions = null;
            _screenInputRegions = null;
            _disposed = Handle == IntPtr.Zero && InputHandle == IntPtr.Zero;
        }
        finally { _disposing = false; }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal readonly DeviceScreenRect ToDevice() => new(Left, Top, Right, Bottom);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
