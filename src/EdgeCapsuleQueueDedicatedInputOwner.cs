using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// Immutable input geometry schedule for one queue translation generation. The owner thread sees
/// only value snapshots: no WPF element, PaperWindow, Dispatcher or mutable controller state crosses
/// the thread boundary.
/// </summary>
internal sealed class EdgeCapsuleQueueInputAnimationTicket
{
    private readonly EdgeCapsuleQueueProxyMemberPlan[] _members;

    internal EdgeCapsuleQueueInputAnimationTicket(
        long startedAtTimestamp,
        int durationMilliseconds,
        IEnumerable<EdgeCapsuleQueueProxyMemberPlan> members)
    {
        if (startedAtTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(startedAtTimestamp));
        if (durationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        StartedAtTimestamp = startedAtTimestamp;
        DurationMilliseconds = durationMilliseconds;
        _members = members.ToArray();
        if (_members.Length == 0)
            throw new ArgumentException("An input animation ticket requires at least one member.", nameof(members));
    }

    internal long StartedAtTimestamp { get; }
    internal int DurationMilliseconds { get; }

    internal long DurationTicks => Math.Max(
        1,
        (long)Math.Round(
            Stopwatch.Frequency * DurationMilliseconds / 1000.0));

    internal long EndTimestamp => checked(StartedAtTimestamp + DurationTicks);

    internal DeviceScreenRect[] Sample(long timestamp)
    {
        var result = new List<DeviceScreenRect>(_members.Length);
        foreach (var member in _members)
        {
            var frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member,
                StartedAtTimestamp,
                DurationMilliseconds,
                timestamp);
            if (frame.Visible && frame.IsHitTestVisible && !frame.InteractiveBounds.IsEmpty)
                result.Add(frame.InteractiveBounds);
        }
        return result.ToArray();
    }
}

/// <summary>
/// Owns only the queue's native input HWND on a dedicated Win32 message loop. The DirectComposition
/// output HWND remains on the WPF/queue runtime thread. All region mutation happens on the HWND's
/// actual owner thread, so an unavailable WPF Dispatcher cannot stall SetWindowRgn publication.
/// </summary>
internal sealed class EdgeCapsuleQueueDedicatedInputOwner : IDisposable
{
    private const uint WorkMessage = 0x8000 + 0x731;
    private const int WmDestroy = 0x0002;
    private const int WmNcDestroy = 0x0082;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmTimer = 0x0113;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonDoubleClick = 0x0206;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonDoubleClick = 0x0209;
    private const int WmQuit = 0x0012;
    private const int GwlWndProc = -4;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExNoActivate = 0x08000000;
    private const int HtClient = 1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const int RgnOr = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const nuint AnimationTimerId = 1;
    private const uint AnimationTimerMilliseconds = 4;

    private static readonly IntPtr HwndNotTopmost = new(-2);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Dictionary<IntPtr, EdgeCapsuleQueueDedicatedInputOwner> Instances = new();
    private static readonly WndProc WindowProcedure = DispatchWindowMessage;
    private static readonly IntPtr WindowProcedurePointer = Marshal.GetFunctionPointerForDelegate(WindowProcedure);

    private readonly Thread _thread;
    private readonly ConcurrentQueue<WorkItem> _work = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<EdgeCapsulePointerDown> _interactionRequested;
    private readonly Action _environmentChanged;
    private DeviceScreenRect _initialBounds;
    private bool _initialTopmost;
    private IntPtr _previousWindowProcedure;
    private DeviceScreenRect _bounds;
    private DeviceScreenRect[]? _screenRegions;
    private EdgeCapsuleQueueInputAnimationTicket? _ticket;
    private Exception? _failure;
    private int _disposed;
    private int _disposing;
    private int _animationActive;
    private int _regionUpdateCount;
    private long _handleValue;
    private uint _nativeThreadId;
    private bool _shown;

    private sealed class WorkItem
    {
        internal required Func<bool> Action { get; init; }
        internal required TaskCompletionSource<bool> Completion { get; init; }
    }

    private EdgeCapsuleQueueDedicatedInputOwner(
        DeviceScreenRect bounds,
        bool topmost,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged)
    {
        _initialBounds = bounds;
        _initialTopmost = topmost;
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PaperTodo Edge input owner"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    internal IntPtr Handle => new(Interlocked.Read(ref _handleValue));
    internal uint ThreadId => Volatile.Read(ref _nativeThreadId);
    internal bool IsAnimationActive => Volatile.Read(ref _animationActive) != 0;
    internal int RegionUpdateCount => Volatile.Read(ref _regionUpdateCount);

    internal static EdgeCapsuleQueueDedicatedInputOwner? TryCreate(
        DeviceScreenRect bounds,
        bool topmost,
        Action<EdgeCapsulePointerDown> interactionRequested,
        Action environmentChanged)
    {
        if (!ValidBounds(bounds)) return null;
        var owner = new EdgeCapsuleQueueDedicatedInputOwner(
            bounds,
            topmost,
            interactionRequested,
            environmentChanged);
        owner._thread.Start();
        try
        {
            if (!owner._started.Task.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Dedicated input owner startup timed out.");
            owner._started.Task.GetAwaiter().GetResult();
            return owner;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Edge queue dedicated input owner creation failed. Exception={0}", ex);
            try { owner.Dispose(); } catch { }
            return null;
        }
    }

    internal bool Show(DeviceScreenRect bounds, bool topmost) => Invoke(() =>
    {
        if (!ValidBounds(bounds) || Handle == IntPtr.Zero) return false;
        if (bounds != _bounds)
        {
            StopAnimationCore();
            if (!SetRegionsCore(Array.Empty<DeviceScreenRect>())) return false;
        }
        if (!SetWindowPos(
                Handle,
                topmost ? HwndTopmost : HwndNotTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
        {
            FailClosedCore();
            return false;
        }
        _bounds = bounds;
        _shown = true;
        return true;
    });

    internal void Hide()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Invoke(() =>
        {
            StopAnimationCore();
            _shown = false;
            if (Handle != IntPtr.Zero) _ = ShowWindow(Handle, SwHide);
            return SetRegionsCore(Array.Empty<DeviceScreenRect>());
        });
    }

    internal bool TrySetRegions(IReadOnlyList<DeviceScreenRect> screenBounds)
    {
        ArgumentNullException.ThrowIfNull(screenBounds);
        var requested = screenBounds.ToArray();
        return Invoke(() =>
        {
            // The WPF-side sample timer still runs pointer/business logic. While an immutable
            // native animation ticket is active, an equivalent non-empty refresh is observational
            // only and must not steal timing authority back to the Dispatcher. A shape/member
            // change, empty clear, hide or environment invalidation still supersedes immediately.
            if (_ticket != null && requested.Length > 0 && IsEquivalentAnimationRefresh(requested))
                return true;
            StopAnimationCore();
            return SetRegionsCore(requested);
        });
    }

    internal bool TryStartAnimation(EdgeCapsuleQueueInputAnimationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return Invoke(() =>
        {
            if (!_shown || Handle == IntPtr.Zero) return false;
            StopAnimationCore();
            _ticket = ticket;
            Volatile.Write(ref _animationActive, 1);
            if (!PublishAnimationSampleCore(Stopwatch.GetTimestamp()))
            {
                StopAnimationCore();
                return false;
            }
            if (_ticket == null) return true;
            if (SetTimer(Handle, AnimationTimerId, AnimationTimerMilliseconds, IntPtr.Zero) == 0)
            {
                StopAnimationCore();
                FailClosedCore();
                return false;
            }
            return true;
        });
    }

    internal bool CancelAnimationAndClear() => Invoke(() =>
    {
        StopAnimationCore();
        return SetRegionsCore(Array.Empty<DeviceScreenRect>());
    });

    private bool IsEquivalentAnimationRefresh(DeviceScreenRect[] requested)
    {
        var ticket = _ticket;
        if (ticket == null) return false;
        var sampled = ticket.Sample(Stopwatch.GetTimestamp());
        if (sampled.Length != requested.Length) return false;
        for (var index = 0; index < sampled.Length; index++)
        {
            var a = sampled[index];
            var b = requested[index];
            if (a.Width != b.Width || a.Height != b.Height ||
                Math.Abs(a.Left - b.Left) > 2 || Math.Abs(a.Top - b.Top) > 2 ||
                Math.Abs(a.Right - b.Right) > 2 || Math.Abs(a.Bottom - b.Bottom) > 2)
                return false;
        }
        return true;
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _nativeThreadId, GetCurrentThreadId());
            var exStyle = WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap |
                (_initialTopmost ? WsExTopmost : 0);
            var handle = CreateWindowEx(
                exStyle,
                "Static",
                string.Empty,
                WsPopup,
                _initialBounds.Left,
                _initialBounds.Top,
                _initialBounds.Width,
                _initialBounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Interlocked.Exchange(ref _handleValue, handle.ToInt64());
            _bounds = _initialBounds;
            lock (Instances) Instances[handle] = this;
            _previousWindowProcedure = SetWindowLongPtr(handle, GwlWndProc, WindowProcedurePointer);
            if (_previousWindowProcedure == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetRegionsCore(Array.Empty<DeviceScreenRect>()))
                throw new InvalidOperationException("Failed to install the initial empty input region.");
            _started.TrySetResult(true);

            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result == 0) break;
                if (message.Message == WorkMessage && message.Hwnd == IntPtr.Zero)
                {
                    DrainWork();
                    continue;
                }
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            _started.TrySetException(ex);
            Trace.TraceError("Edge queue dedicated input owner failed. Exception={0}", ex);
            while (_work.TryDequeue(out var item))
                item.Completion.TrySetException(ex);
        }
        finally
        {
            Volatile.Write(ref _animationActive, 0);
            _ticket = null;
            var handle = Handle;
            if (handle != IntPtr.Zero)
            {
                _ = KillTimer(handle, AnimationTimerId);
                _ = ShowWindow(handle, SwHide);
                if (_previousWindowProcedure != IntPtr.Zero)
                    _ = SetWindowLongPtr(handle, GwlWndProc, _previousWindowProcedure);
                _ = DestroyWindow(handle);
                lock (Instances) Instances.Remove(handle);
                Interlocked.Exchange(ref _handleValue, 0);
            }
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var item))
        {
            try { item.Completion.TrySetResult(item.Action()); }
            catch (Exception ex) { item.Completion.TrySetException(ex); }
        }
    }

    private bool Invoke(Func<bool> action)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _disposing) != 0) return false;
        if (_failure != null) return false;
        if (GetCurrentThreadId() == ThreadId)
        {
            try { return action(); }
            catch (Exception ex) { _failure = ex; return false; }
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(new WorkItem { Action = action, Completion = completion });
        if (!PostThreadMessage(ThreadId, WorkMessage, IntPtr.Zero, IntPtr.Zero))
        {
            _work.TryDequeue(out _);
            return false;
        }
        try
        {
            if (!completion.Task.Wait(TimeSpan.FromSeconds(5))) return false;
            return completion.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Edge queue dedicated input owner command failed. Exception={0}", ex);
            return false;
        }
    }

    private bool PublishAnimationSampleCore(long timestamp)
    {
        var ticket = _ticket;
        if (ticket == null) return true;
        var sampleTimestamp = Math.Min(timestamp, ticket.EndTimestamp);
        if (!SetRegionsCore(ticket.Sample(sampleTimestamp))) return false;
        if (timestamp >= ticket.EndTimestamp)
            StopAnimationCore();
        return true;
    }

    private void StopAnimationCore()
    {
        var handle = Handle;
        if (handle != IntPtr.Zero) _ = KillTimer(handle, AnimationTimerId);
        _ticket = null;
        Volatile.Write(ref _animationActive, 0);
    }

    private bool SetRegionsCore(DeviceScreenRect[] screenBounds)
    {
        if (Handle == IntPtr.Zero) return false;
        if (_screenRegions is { } cached && cached.AsSpan().SequenceEqual(screenBounds)) return true;
        var union = CreateRectRgn(0, 0, 0, 0);
        if (union == IntPtr.Zero) return FailClosedCore();
        try
        {
            foreach (var screen in screenBounds)
            {
                var left = Math.Max(screen.Left, _bounds.Left);
                var top = Math.Max(screen.Top, _bounds.Top);
                var right = Math.Min(screen.Right, _bounds.Right);
                var bottom = Math.Min(screen.Bottom, _bounds.Bottom);
                if (right <= left || bottom <= top) continue;
                var part = CreateRectRgn(
                    left - _bounds.Left,
                    top - _bounds.Top,
                    right - _bounds.Left,
                    bottom - _bounds.Top);
                if (part == IntPtr.Zero) return FailClosedCore();
                try
                {
                    if (CombineRgn(union, union, part, RgnOr) == 0)
                        return FailClosedCore();
                }
                finally { _ = DeleteObject(part); }
            }
            if (SetWindowRgn(Handle, union, redraw: false) == 0)
                return FailClosedCore();
            union = IntPtr.Zero; // SetWindowRgn owns the HRGN after success.
            _screenRegions = screenBounds.ToArray();
            Interlocked.Increment(ref _regionUpdateCount);
            return true;
        }
        finally
        {
            if (union != IntPtr.Zero) _ = DeleteObject(union);
        }
    }

    private bool FailClosedCore()
    {
        _screenRegions = null;
        StopAnimationCore();
        if (Handle != IntPtr.Zero) _ = ShowWindow(Handle, SwHide);
        _shown = false;
        return false;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                    // The HRGN itself is the physical truth. Windows does not route points in its
                    // holes to this HWND, so a second managed hit model here would only race it.
                    return new IntPtr(HtClient);
                case WmMouseActivate:
                    return new IntPtr(MaNoActivate);
                case WmEraseBackground:
                    return new IntPtr(1);
                case WmPaint:
                    _ = BeginPaint(hwnd, out var paint);
                    _ = EndPaint(hwnd, ref paint);
                    return IntPtr.Zero;
                case WmTimer when wParam == new IntPtr((long)AnimationTimerId):
                    if (!PublishAnimationSampleCore(Stopwatch.GetTimestamp()))
                        _environmentChanged();
                    return IntPtr.Zero;
                case WmLButtonDown:
                case WmLButtonDoubleClick:
                case WmRButtonDown:
                case WmRButtonDoubleClick:
                case WmMiddleButtonDown:
                case WmMiddleButtonDoubleClick:
                    var packed = lParam.ToInt64();
                    var point = new NativePoint
                    {
                        X = unchecked((short)(packed & 0xFFFF)),
                        Y = unchecked((short)((packed >> 16) & 0xFFFF))
                    };
                    if (ClientToScreen(hwnd, ref point))
                    {
                        _interactionRequested(new EdgeCapsulePointerDown(
                            new DeviceScreenPoint(point.X, point.Y),
                            message,
                            wParam));
                    }
                    return IntPtr.Zero;
                case WmDpiChanged:
                case WmDisplayChange:
                    StopAnimationCore();
                    _ = SetRegionsCore(Array.Empty<DeviceScreenRect>());
                    _environmentChanged();
                    return IntPtr.Zero;
                case WmNcDestroy:
                    var result = _previousWindowProcedure != IntPtr.Zero
                        ? CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam)
                        : DefWindowProc(hwnd, message, wParam, lParam);
                    lock (Instances) Instances.Remove(hwnd);
                    Interlocked.Exchange(ref _handleValue, 0);
                    return result;
                case WmDestroy:
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("Dedicated Edge input callback failed. Message=0x{0:X}; Exception={1}", message, ex);
            FailClosedCore();
            _environmentChanged();
            return IntPtr.Zero;
        }
        return _previousWindowProcedure != IntPtr.Zero
            ? CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static IntPtr DispatchWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        EdgeCapsuleQueueDedicatedInputOwner? owner;
        lock (Instances) Instances.TryGetValue(hwnd, out owner);
        return owner?.WindowMessage(hwnd, message, wParam, lParam) ??
            DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var threadId = ThreadId;
            if (_thread.IsAlive && threadId != 0)
            {
                _ = PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                if (!_thread.Join(TimeSpan.FromSeconds(5)))
                    Trace.TraceWarning("Dedicated Edge input owner did not exit within 5 seconds.");
            }
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _disposing, 0);
        }
    }

    private static bool ValidBounds(DeviceScreenRect bounds) =>
        (long)bounds.Right - bounds.Left is > 0 and <= 0x03ffffff &&
        (long)bounds.Bottom - bounds.Top is > 0 and <= 0x03ffffff;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintState paint);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintState paint);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(IntPtr hwnd, nuint timerId, uint milliseconds, IntPtr timerProc);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, nuint timerId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}