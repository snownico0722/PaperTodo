using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PaperTodo;

// Shared Win32 window-style / z-order helpers for the app's borderless top-level windows
// (paper windows, the deep-capsule slot host, the master capsule). Previously duplicated
// verbatim across PaperWindow.Native and MasterCapsuleWindow.
internal static class WindowNative
{
    [ThreadStatic]
    private static WindowDeviceBoundsBatch? _currentDeviceBoundsBatch;

    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const uint GwOwner = 4;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private static readonly IntPtr DpiAwarenessContextSystemAware = new(-2);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int DwmWaExtendedFrameBounds = 9;

    // A tiny off-screen TOOLWINDOW serves as the native owner for a paper hidden from
    // Alt+Tab. Each paper must keep its own owner: papers sharing one owner become one
    // native window group, so activating any member can raise the other papers as well.
    private static IntPtr GetOrCreateHiddenOwner(IntPtr hiddenOwner)
    {
        if (hiddenOwner != IntPtr.Zero && IsWindow(hiddenOwner))
        {
            return hiddenOwner;
        }

        return CreateWindowEx(
            WsExToolWindow,
            "Static",
            "",
            0, // WS_OVERLAPPED (no visible chrome)
            -100, -100, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    // WS_EX_NOACTIVATE: the window can never become foreground, so clicking it never steals
    // focus from (and forces a repaint of) whatever app was in front — the click "flash".
    public static void ApplyNoActivateStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate);
    }

    public static bool HasNoActivateStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return (GetWindowLong(handle, GwlExStyle) & WsExNoActivate) != 0;
    }

    public static void SetNoActivateStyle(Window window, bool enabled)
    {
        SetExtendedStyleFlag(window, WsExNoActivate, enabled);
    }

    public static void SetInputPassthrough(Window window, bool enabled)
    {
        SetExtendedStyleFlag(window, WsExTransparent, enabled);
    }

    private static void SetExtendedStyleFlag(Window window, int flag, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        var updated = enabled ? exStyle | flag : exStyle & ~flag;
        if (updated == exStyle)
        {
            return;
        }

        SetWindowLong(handle, GwlExStyle, updated);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate |
            SwpFrameChanged | SwpNoOwnerZOrder);
    }

    public static void ApplyBottomZOrder(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var applied = SetWindowPos(
            handle,
            HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"native.window phase=zorder-bottom hwnd=0x{handle.ToInt64():X} " +
            $"outcome={(applied ? "success" : "failed")} " +
            $"callMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3} " +
            $"positionChanged=false sizeChanged=false visibilityChanged=false zOrderChanged=true");
#endif
    }

    public static void ApplyWindowSwitcherVisibility(
        Window window,
        bool visible,
        ref IntPtr hiddenOwner)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (visible)
        {
            // Remove the hidden owner — the window re-appears in Alt+Tab.
            SetWindowLongPtr(handle, GwlpHwndParent, IntPtr.Zero);
        }
        else
        {
            // Set this paper's hidden TOOLWINDOW as owner — owned windows are excluded from
            // Alt+Tab without needing WS_EX_TOOLWINDOW on the paper itself, so Windows
            // won't skip the paper when choosing the next window to activate.
            hiddenOwner = GetOrCreateHiddenOwner(hiddenOwner);
            SetWindowLongPtr(handle, GwlpHwndParent, hiddenOwner);
        }

        // Ensure WS_EX_TOOLWINDOW is cleared from the paper in both cases. This undoes the
        // style that older versions may have left behind.
        var exStyle = GetWindowLong(handle, GwlExStyle);
        var cleaned = (exStyle & ~WsExToolWindow) & ~WsExAppWindow;
        if (visible)
        {
            // No special ex-style needed when visible in switcher.
            cleaned = exStyle & ~WsExToolWindow;
        }
        if (cleaned != exStyle)
        {
            SetWindowLong(handle, GwlExStyle, cleaned);
        }

        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);

        if (visible && window.IsVisible)
        {
            RefreshShellWindowListEntry(handle);
        }

        if (visible)
        {
            ReleaseWindowSwitcherOwner(ref hiddenOwner);
        }
    }

    public static void DetachAndReleaseWindowSwitcherOwner(
        Window window,
        ref IntPtr hiddenOwner)
    {
        if (hiddenOwner == IntPtr.Zero)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowLongPtr(handle, GwlpHwndParent, IntPtr.Zero);
        }

        ReleaseWindowSwitcherOwner(ref hiddenOwner);
    }

    public static void ReleaseWindowSwitcherOwner(ref IntPtr hiddenOwner)
    {
        if (hiddenOwner != IntPtr.Zero && IsWindow(hiddenOwner))
        {
            _ = DestroyWindow(hiddenOwner);
        }

        hiddenOwner = IntPtr.Zero;
    }

    private static void RefreshShellWindowListEntry(IntPtr handle)
    {
        // The shell may keep Alt+Tab / Task View membership cached after WS_EX_TOOLWINDOW
        // changes. A no-activate hide/show makes it rebuild the entry without stealing focus.
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpHideWindow);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }

    // Set topmost / no-topmost without moving, sizing, or activating the window. Fullscreen
    // avoidance is owner-aware for non-topmost targets because ShowInTaskbar=false gives WPF
    // windows a hidden owner.
    public static void ApplyTopmostZOrder(Window window, bool topmost, IntPtr insertAfter)
    {
        ApplyTopmostZOrder(new WindowInteropHelper(window).Handle, topmost, insertAfter);
    }

    public static void ApplyTopmostZOrder(IntPtr handle, bool topmost, IntPtr insertAfter)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            topmost ? HwndTopmost : HwndNoTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

        if (!topmost && insertAfter != IntPtr.Zero)
        {
            ApplyFullscreenAvoidanceZOrder(handle, insertAfter);
        }
    }

    private static void ApplyFullscreenAvoidanceZOrder(IntPtr handle, IntPtr insertAfter)
    {
        if (insertAfter == handle ||
            !IsWindow(insertAfter) ||
            (GetWindowLong(insertAfter, GwlExStyle) & WsExTopmost) != 0)
        {
            // The caller already removed the visible HWND from the topmost band. That alone
            // places it behind a topmost fullscreen target; invalid targets need no relative move.
            return;
        }

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate;
        var owner = GetWindow(handle, GwOwner);
        if (!IsHiddenOwnerFromSameProcess(handle, owner))
        {
            // Preserve unrelated or visible owners and retain the original single-HWND behavior.
            _ = SetWindowPos(
                handle,
                insertAfter,
                0, 0, 0, 0,
                flags | SwpNoOwnerZOrder);
            return;
        }

        // WPF implements ShowInTaskbar=false with an invisible owner. Move that owner behind
        // the fullscreen target first so target -> visible window -> owner becomes possible.
        var ownerMoved = SetWindowPos(
            owner,
            insertAfter,
            0, 0, 0, 0,
            flags);

        // If the owner move succeeded, freeze it at the committed position while inserting the
        // visible surface. If it failed, let Windows adjust the owner as a bounded fallback.
        _ = SetWindowPos(
            handle,
            insertAfter,
            0, 0, 0, 0,
            flags | (ownerMoved ? SwpNoOwnerZOrder : 0u));
    }

    private static bool IsHiddenOwnerFromSameProcess(IntPtr handle, IntPtr owner)
    {
        if (owner == IntPtr.Zero ||
            !IsWindow(owner) ||
            IsWindowVisible(owner))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        _ = GetWindowThreadProcessId(owner, out var ownerProcessId);
        return processId != 0 && ownerProcessId == processId;
    }

    public static bool IsTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;
    }

    public static void BringToFrontNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            IsTopmost(window) ? HwndTopmost : HwndTop,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public static void TrySetForegroundWindow(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(handle);
        }
    }

    public static void HideWindowImmediately(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, SwHide);
        }
    }

    public static void ClearCurrentThreadKeyboardFocus()
    {
        _ = SetFocus(IntPtr.Zero);
    }

    public static IntPtr ForegroundWindow => GetForegroundWindow();
    public static IntPtr ActiveWindow => GetActiveWindow();
    public static IntPtr KeyboardFocusWindow => GetFocus();

    public static void ClearCurrentThreadInputActivation(IntPtr externalForegroundWindow)
    {
        _ = SetFocus(IntPtr.Zero);
        // Passing a window owned by another input thread clears this thread's active HWND.
        _ = SetActiveWindow(externalForegroundWindow);
    }

    public static bool TryGetCursorScreenPosition(out DeviceScreenPoint point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new DeviceScreenPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    // The detached drag capsule deliberately uses the stable System Aware behavior of the
    // pre-PMv2 implementation. Only its HWND is created in this temporary context; the process,
    // docked hosts and every later caller remain PerMonitorV2.
    public static IntPtr CreateSystemAwareTopLevelWindowHandle(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The system-aware window handle must be created before first use.");
        }

        var previousContext = SetThreadDpiAwarenessContext(DpiAwarenessContextSystemAware);
        if (previousContext == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Windows could not enter the system-aware DPI context.");
        }

        try
        {
            var handle = helper.EnsureHandle();
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Windows could not create the floating capsule window.");
            }
            return handle;
        }
        finally
        {
            _ = SetThreadDpiAwarenessContext(previousContext);
        }
    }

    // Commit position and size as one native operation. Edge surfaces use physical screen pixels
    // as their source of truth; assigning WPF Left/Top/Width separately creates observable
    // intermediate HWND rectangles and was the direct cause of one-frame edge clipping.
    public static bool TrySetWindowDeviceBounds(Window window, DeviceScreenRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
        if (handle != IntPtr.Zero &&
            window.IsVisible &&
            _currentDeviceBoundsBatch is { } batch)
        {
            if (batch.HasFailed)
            {
                return false;
            }
            if (batch.IsAvailable)
            {
                return batch.TryDefer(handle, bounds);
            }
        }

#if DEBUG
        var positionChanged = false;
        var sizeChanged = false;
        var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var currentBounds))
        {
            positionChanged = currentBounds.Left != bounds.Left ||
                currentBounds.Top != bounds.Top;
            sizeChanged = currentBounds.Right - currentBounds.Left != bounds.Width ||
                currentBounds.Bottom - currentBounds.Top != bounds.Height;
        }
        var prepareMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            prepareStartedAt);
        var setStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var applied = handle != IntPtr.Zero && SetWindowPos(
            handle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
#if DEBUG
        var setMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(setStartedAt);
        if (handle != IntPtr.Zero)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"native.window phase=immediate-set hwnd=0x{handle.ToInt64():X} " +
                $"outcome={(applied ? "success" : "failed")} prepareMs={prepareMilliseconds:F3} " +
                $"callMs={setMilliseconds:F3} positionChanged={positionChanged} " +
                $"sizeChanged={sizeChanged} visibilityChanged=false zOrderChanged=false " +
                $"bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}");
        }
#endif
        return applied;
    }

    // A System Aware floating HWND owns its fixed logical size for its entire lifetime. Handoff
    // frames may move it, but must not submit a competing native size.
    public static bool TryMoveWindowDevicePosition(Window window, DeviceScreenPoint position)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(position.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(position.Y, MidpointRounding.AwayFromZero),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    // Centers the System Aware floating window on the live cursor from inside its own coordinate
    // space. WPF's property write converts through the uniform system scale, but the virtual
    // desktop mapping is monitor-anchored, so a pull-out whose cursor already sits on another
    // monitor materializes the pill at the wrong physical spot and size until release. Writing
    // one rectangle in the window's own space lets Windows resolve the exact physical result for
    // the cursor's monitor. The size written is the window's fixed logical size expressed in its
    // own units, so this does not introduce a second native size owner.
    public static bool TryCenterSystemAwareWindowAtCursor(
        Window window,
        double widthDip,
        double heightDip) =>
        TryCenterSystemAwareWindowAtCursor(
            window,
            widthDip,
            heightDip,
            out _);

    // Keep the cursor anchor private to this class: GetCursorPos is intentionally sampled while
    // the thread is System Aware, so these coordinates must never escape as a DeviceScreenPoint
    // or be reused for monitor selection / the final drop position in the PMv2 application.
    public static bool TryBeginSystemAwareWindowCaptionDragFromCursor(
        Window window,
        double widthDip,
        double heightDip)
    {
        return TryCenterSystemAwareWindowAtCursor(
                window,
                widthDip,
                heightDip,
                out var cursorAnchor) &&
            TryBeginWindowCaptionDrag(window, cursorAnchor);
    }

    private static bool TryCenterSystemAwareWindowAtCursor(
        Window window,
        double widthDip,
        double heightDip,
        out CursorPoint cursorPosition)
    {
        cursorPosition = default;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero ||
            widthDip <= 0 ||
            heightDip <= 0)
        {
            return false;
        }

        var previousContext = SetThreadDpiAwarenessContext(DpiAwarenessContextSystemAware);
        if (previousContext == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dpi = GetDpiForWindow(handle);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            if (!GetCursorPos(out cursorPosition))
            {
                return false;
            }

            var width = Math.Max(1, (int)Math.Round(widthDip * scale, MidpointRounding.AwayFromZero));
            var height = Math.Max(1, (int)Math.Round(heightDip * scale, MidpointRounding.AwayFromZero));
            var left = (int)Math.Round(cursorPosition.X - width / 2.0, MidpointRounding.AwayFromZero);
            var top = (int)Math.Round(cursorPosition.Y - height / 2.0, MidpointRounding.AwayFromZero);
            return SetWindowPos(
                handle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }
        finally
        {
            _ = SetThreadDpiAwarenessContext(previousContext);
        }
    }

    public static bool TryGetWindowDeviceBounds(Window window, out DeviceScreenRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero &&
            _currentDeviceBoundsBatch?.TryGetPending(handle, out bounds) == true)
        {
            return true;
        }
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect))
        {
            bounds = new DeviceScreenRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return !bounds.IsEmpty;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Defers all visible HWND bounds submitted on the current UI thread and commits them through
    /// one HDWP. The HDWP is created lazily on the first real bounds change: pure WPF animation
    /// frames therefore do not call EndDeferWindowPos at all.
    /// </summary>
    public static WindowDeviceBoundsBatch BeginWindowDeviceBoundsBatch(int capacity) =>
        new(Math.Max(1, capacity));

    internal sealed class WindowDeviceBoundsBatch : IDisposable
    {
        private readonly bool _ownsCurrentBatch;
        private readonly int _capacity;
        private readonly Dictionary<IntPtr, DeviceScreenRect> _pendingBounds = new();
        private IntPtr _deferredWindowPosition;
        private bool _beginAttempted;
        private bool _nativeCommitAttempted;
        private bool _completed;
#if DEBUG
        private readonly long _createdAt;
        private double _prepareMilliseconds;
        private double _beginMilliseconds;
        private double _deferMilliseconds;
        private double _verifyMilliseconds;
        private int _requestCount;
        private int _positionChangeCount;
        private int _sizeChangeCount;
#endif

        internal WindowDeviceBoundsBatch(int capacity)
        {
            _capacity = capacity;
#if DEBUG
            _createdAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            if (_currentDeviceBoundsBatch != null)
            {
                // A nested visual callback already participates in the outer native transaction.
                return;
            }

            _ownsCurrentBatch = true;
            _currentDeviceBoundsBatch = this;
        }

        internal bool IsAvailable =>
            _ownsCurrentBatch &&
            !_completed &&
            !HasFailed;

        internal bool HasFailed { get; private set; }

        internal int PendingWindowCount => _pendingBounds.Count;

        internal bool PerformedNativeCommit => _nativeCommitAttempted;

        internal bool TryDefer(IntPtr handle, DeviceScreenRect bounds)
        {
            if (!IsAvailable || handle == IntPtr.Zero || bounds.IsEmpty)
            {
                return false;
            }

            if (_pendingBounds.TryGetValue(handle, out var pendingBounds) &&
                pendingBounds == bounds)
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"native.window phase=defer-skip hwnd=0x{handle.ToInt64():X} " +
                    $"reason=same-pending positionChanged=false sizeChanged=false " +
                    $"visibilityChanged=false zOrderChanged=false " +
                    $"bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}");
#endif
                return true;
            }

#if DEBUG
            var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var positionChanged = false;
            var sizeChanged = false;
            if (_pendingBounds.TryGetValue(handle, out var previousPendingBounds))
            {
                positionChanged = previousPendingBounds.Left != bounds.Left ||
                    previousPendingBounds.Top != bounds.Top;
                sizeChanged = previousPendingBounds.Width != bounds.Width ||
                    previousPendingBounds.Height != bounds.Height;
            }
            else if (GetWindowRect(handle, out var currentBounds))
            {
                positionChanged = currentBounds.Left != bounds.Left ||
                    currentBounds.Top != bounds.Top;
                sizeChanged = currentBounds.Right - currentBounds.Left != bounds.Width ||
                    currentBounds.Bottom - currentBounds.Top != bounds.Height;
            }
            _prepareMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                prepareStartedAt);
#endif

            if (!_beginAttempted)
            {
                _beginAttempted = true;
#if DEBUG
                var beginStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                _deferredWindowPosition = BeginDeferWindowPos(_capacity);
#if DEBUG
                _beginMilliseconds += EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    beginStartedAt);
#endif
                if (_deferredWindowPosition == IntPtr.Zero)
                {
#if DEBUG
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"native.batch phase=begin outcome=unavailable " +
                        $"beginMs={_beginMilliseconds:F3} capacity={_capacity}");
#endif
                    // Preserve the existing fallback contract: the caller performs an immediate
                    // SetWindowPos when the system cannot create an HDWP.
                    return false;
                }
            }

            if (_deferredWindowPosition == IntPtr.Zero)
            {
                return false;
            }

#if DEBUG
            var deferStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var updated = DeferWindowPos(
                _deferredWindowPosition,
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
#if DEBUG
            var deferMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                deferStartedAt);
            _deferMilliseconds += deferMilliseconds;
            _requestCount++;
            if (positionChanged)
            {
                _positionChangeCount++;
            }
            if (sizeChanged)
            {
                _sizeChangeCount++;
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"native.window phase=defer hwnd=0x{handle.ToInt64():X} " +
                $"outcome={(updated != IntPtr.Zero ? "queued" : "failed")} " +
                $"callMs={deferMilliseconds:F3} positionChanged={positionChanged} " +
                $"sizeChanged={sizeChanged} visibilityChanged=false zOrderChanged=false " +
                $"bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}");
#endif
            if (updated == IntPtr.Zero)
            {
                HasFailed = true;
                _deferredWindowPosition = IntPtr.Zero;
                _pendingBounds.Clear();
                return false;
            }

            _deferredWindowPosition = updated;
            _pendingBounds[handle] = bounds;
            return true;
        }

        internal bool TryGetPending(IntPtr handle, out DeviceScreenRect bounds)
        {
            if (_ownsCurrentBatch &&
                _pendingBounds.TryGetValue(handle, out bounds))
            {
                return true;
            }

            bounds = default;
            return false;
        }

        public bool Commit()
        {
            if (_completed)
            {
                return !HasFailed;
            }
            _completed = true;

            if (!_ownsCurrentBatch)
            {
                return true;
            }
            if (ReferenceEquals(_currentDeviceBoundsBatch, this))
            {
                _currentDeviceBoundsBatch = null;
            }

            // No visible HWND asked to change bounds. This is the common case for horizontal
            // preview animation, where only the WPF VisualSurface changes. Avoid even creating an
            // HDWP, and especially avoid an empty EndDeferWindowPos synchronization.
            if (!_beginAttempted || _pendingBounds.Count == 0)
            {
#if DEBUG
                TraceCommit("noop", endMilliseconds: 0);
#endif
                return !HasFailed;
            }

            // BeginDeferWindowPos can be unavailable while the caller succeeds through immediate
            // SetWindowPos. There is no deferred native transaction left to commit in that case.
            if (_deferredWindowPosition == IntPtr.Zero)
            {
#if DEBUG
                TraceCommit(HasFailed ? "failed-before-end" : "immediate-fallback", endMilliseconds: 0);
#endif
                return !HasFailed;
            }

            _nativeCommitAttempted = true;
#if DEBUG
            var endStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var committed = EndDeferWindowPos(_deferredWindowPosition);
#if DEBUG
            var endMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endStartedAt);
#endif
            _deferredWindowPosition = IntPtr.Zero;
            if (!committed)
            {
                HasFailed = true;
#if DEBUG
                TraceCommit("end-failed", endMilliseconds);
#endif
                return false;
            }

            foreach (var (handle, expected) in _pendingBounds)
            {
#if DEBUG
                var verifyStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                var matches = GetWindowRect(handle, out var actual) &&
                    new DeviceScreenRect(
                        actual.Left,
                        actual.Top,
                        actual.Right,
                        actual.Bottom) == expected;
#if DEBUG
                var verifyMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    verifyStartedAt);
                _verifyMilliseconds += verifyMilliseconds;
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"native.window phase=verify hwnd=0x{handle.ToInt64():X} " +
                    $"outcome={(matches ? "matched" : "mismatch")} callMs={verifyMilliseconds:F3} " +
                    $"positionChanged=false sizeChanged=false visibilityChanged=false zOrderChanged=false " +
                    $"bounds={expected.Left},{expected.Top},{expected.Width}x{expected.Height}");
#endif
                if (!matches)
                {
                    HasFailed = true;
#if DEBUG
                    TraceCommit("verify-failed", endMilliseconds);
#endif
                    return false;
                }
            }
#if DEBUG
            TraceCommit("committed", endMilliseconds);
#endif
            return true;
        }

#if DEBUG
        private void TraceCommit(string outcome, double endMilliseconds)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"native.batch outcome={outcome} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_createdAt):F3} " +
                $"prepareMs={_prepareMilliseconds:F3} beginMs={_beginMilliseconds:F3} " +
                $"deferMs={_deferMilliseconds:F3} endMs={endMilliseconds:F3} " +
                $"verifyMs={_verifyMilliseconds:F3} capacity={_capacity} " +
                $"requests={_requestCount} windows={_pendingBounds.Count} " +
                $"positionChanged={_positionChangeCount} sizeChanged={_sizeChangeCount} " +
                $"visibilityChanged=0 zOrderChanged=0 nativeCommit={_nativeCommitAttempted}");
        }
#endif

        public void Dispose() => Commit();
    }

    public static bool TryGetWindowScreenBounds(Window window, out Rect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeRect))
        {
            var topLeft = WindowWorkAreaHelper.DeviceScreenPointToDip(new DeviceScreenPoint(nativeRect.Left, nativeRect.Top));
            var bottomRight = WindowWorkAreaHelper.DeviceScreenPointToDip(new DeviceScreenPoint(nativeRect.Right, nativeRect.Bottom));
            bounds = new Rect(topLeft.ToPoint(), bottomRight.ToPoint());
            return true;
        }

        bounds = Rect.Empty;
        return false;
    }

    public static bool TryGetVisibleFrameScreenBounds(Window window, out Rect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero &&
            DwmGetWindowAttribute(handle, DwmWaExtendedFrameBounds, out var nativeRect, Marshal.SizeOf<NativeRect>()) == 0)
        {
            var topLeft = DevicePointToWindowDip(window, new Point(nativeRect.Left, nativeRect.Top));
            var bottomRight = DevicePointToWindowDip(window, new Point(nativeRect.Right, nativeRect.Bottom));
            bounds = new Rect(topLeft, bottomRight);
            return true;
        }

        bounds = Rect.Empty;
        return false;
    }

    // Presenter settle runs after WPF's Render work, but a transparent top-level window's new
    // surface can still be waiting for the desktop compositor. Use this only at a cross-HWND
    // hand-off boundary, never on an animation frame or ordinary presentation update.
    public static void FlushDesktopComposition() => _ = DwmFlush();

    private static Point DevicePointToWindowDip(Window window, Point point)
    {
        if (PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
        {
            return target.TransformFromDevice.Transform(point);
        }

        return WindowWorkAreaHelper.DeviceScreenPointToDip(DeviceScreenPoint.FromPoint(point)).ToPoint();
    }

    public static void BeginWindowCaptionDrag(Window window)
    {
        _ = TryBeginWindowCaptionDrag(window);
    }

    public static bool TryBeginWindowCaptionDrag(Window window)
    {
        return TryGetCursorScreenPosition(out var cursorPosition) &&
            TryBeginWindowCaptionDrag(window, cursorPosition);
    }

    public static bool TryBeginWindowCaptionDrag(
        Window window,
        DeviceScreenPoint cursorPosition)
    {
        var x = (int)Math.Round(cursorPosition.X, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(cursorPosition.Y, MidpointRounding.AwayFromZero);
        return TryBeginWindowCaptionDrag(window, x, y);
    }

    private static bool TryBeginWindowCaptionDrag(
        Window window,
        CursorPoint cursorPosition) =>
        TryBeginWindowCaptionDrag(window, cursorPosition.X, cursorPosition.Y);

    private static bool TryBeginWindowCaptionDrag(
        Window window,
        int cursorX,
        int cursorY)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = ReleaseCapture();
        var packedPosition = PackScreenPoint(cursorX, cursorY);
        _ = SendMessage(
            handle,
            WmNcLButtonDown,
            new IntPtr(HtCaption),
            packedPosition);
        return true;
    }

    private static IntPtr PackScreenPoint(int x, int y)
    {
        var packed = unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
        return new IntPtr(packed);
    }

    // Restore a natively maximized or snapped window at the Win32 level (SW_RESTORE) so the hwnd
    // leaves that state even when WPF's WindowState no longer agrees. Used while collapsing so a
    // capsule dragged afterward isn't "restored to full size" by the shell mid-drag.
    public static void RestoreNativeWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, SwRestore);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
    private const int SwRestore = 9;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DeferWindowPos(
        IntPtr hWinPosInfo,
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out NativeRect pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
