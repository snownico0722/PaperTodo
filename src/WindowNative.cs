using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PaperTodo;

// Shared Win32 window-style / z-order helpers for the app's borderless top-level windows
// (paper windows, the deep-capsule slot host, the master capsule). Previously duplicated
// verbatim across PaperWindow.Native and MasterCapsuleWindow.
internal static partial class WindowNative
{
    [ThreadStatic]
    private static WindowDeviceBoundsBatch? _currentDeviceBoundsBatch;
#if DEBUG
    [ThreadStatic]
    private static NativeGeometryMessageProbe _activeNativeGeometryMessageProbe;

    private const int WmMove = 0x0003;
    private const int WmSize = 0x0005;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmWindowPosChanged = 0x0047;

    private struct NativeGeometryMessageProbe
    {
        internal bool Active { get; set; }
        internal IntPtr Handle { get; set; }
        internal int WindowPosChangingCount { get; set; }
        internal int WindowPosChangedCount { get; set; }
        internal int MoveCount { get; set; }
        internal int SizeCount { get; set; }
    }
#endif

    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int FullscreenAvoidanceFallbackAttempts = 2;
    private static readonly IntPtr DpiAwarenessContextSystemAware = new(-2);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = WindowNativeBoundsPolicy.SwpNoSize;
    private const uint SwpNoMove = WindowNativeBoundsPolicy.SwpNoMove;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int DwmWaExtendedFrameBounds = 9;
    private const int DwmWaCloak = 13;
    private const int DwmWaCloaked = 14;
    private const int DwmCloakedApp = 0x00000001;

#if DEBUG
    internal static void ObserveNativeGeometryMessage(IntPtr handle, int message)
    {
        var probe = _activeNativeGeometryMessageProbe;
        if (!probe.Active ||
            (probe.Handle != IntPtr.Zero && probe.Handle != handle))
        {
            return;
        }

        switch (message)
        {
            case WmWindowPosChanging:
                _activeNativeGeometryMessageProbe.WindowPosChangingCount++;
                break;
            case WmWindowPosChanged:
                _activeNativeGeometryMessageProbe.WindowPosChangedCount++;
                break;
            case WmMove:
                _activeNativeGeometryMessageProbe.MoveCount++;
                break;
            case WmSize:
                _activeNativeGeometryMessageProbe.SizeCount++;
                break;
        }
    }

    private static NativeGeometryMessageProbe BeginNativeGeometryMessageProbe(
        IntPtr handle)
    {
        var previous = _activeNativeGeometryMessageProbe;
        _activeNativeGeometryMessageProbe = new NativeGeometryMessageProbe
        {
            Active = true,
            Handle = handle
        };
        return previous;
    }

    private static NativeGeometryMessageProbe EndNativeGeometryMessageProbe(
        NativeGeometryMessageProbe previous)
    {
        var completed = _activeNativeGeometryMessageProbe;
        _activeNativeGeometryMessageProbe = previous;
        return completed;
    }
#endif

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

        SetWindowPos(
            handle,
            HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
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

        // The hidden-owner technique needs the paper itself to stop forcing an app-window entry.
        // A visible paper keeps its normal WPF extended styles; no cross-version style cleanup is
        // needed because HWND state does not survive the process that created it.
        if (!visible)
        {
            var exStyle = GetWindowLong(handle, GwlExStyle);
            var cleaned = exStyle & ~WsExAppWindow;
            if (cleaned != exStyle)
            {
                SetWindowLong(handle, GwlExStyle, cleaned);
            }
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
        // The shell may keep Alt+Tab / Task View membership cached after owner/style changes.
        // A no-activate hide/show makes it rebuild the entry without stealing focus.
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
    // windows a hidden owner. The native result is verified so a denied cross-integrity relative
    // insertion can fall back to lowering only PaperTodo's own HWND group.
    public static bool ApplyTopmostZOrder(Window window, bool topmost, IntPtr insertAfter)
    {
        return ApplyTopmostZOrder(new WindowInteropHelper(window).Handle, topmost, insertAfter);
    }

    public static bool ApplyTopmostZOrder(IntPtr handle, bool topmost, IntPtr insertAfter)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var bandApplied = SetWindowPos(
            handle,
            topmost ? HwndTopmost : HwndNoTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

        if (topmost || insertAfter == IntPtr.Zero)
        {
            return bandApplied;
        }

        return ApplyFullscreenAvoidanceZOrder(handle, insertAfter);
    }

    private static bool ApplyFullscreenAvoidanceZOrder(IntPtr handle, IntPtr insertAfter)
    {
        if (insertAfter == handle || !IsWindow(insertAfter))
        {
            // The target disappeared (or is the surface itself). Demotion is still a valid safe
            // state, but there is no live foreign HWND left whose relative order can be verified.
            return (GetWindowLong(handle, GwlExStyle) & WsExTopmost) == 0;
        }

        if ((GetWindowLong(insertAfter, GwlExStyle) & WsExTopmost) != 0)
        {
            // A non-topmost PaperTodo HWND is necessarily behind a topmost fullscreen target.
            return (GetWindowLong(handle, GwlExStyle) & WsExTopmost) == 0;
        }

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate;
        var owner = GetWindow(handle, GwOwner);
        var hasHiddenOwner = IsHiddenOwnerFromSameProcess(handle, owner);
        if (!hasHiddenOwner)
        {
            // Preserve unrelated or visible owners and retain the original single-HWND behavior.
            _ = SetWindowPos(
                handle,
                insertAfter,
                0, 0, 0, 0,
                flags | SwpNoOwnerZOrder);
        }
        else
        {
            // WPF implements ShowInTaskbar=false with an invisible owner. Move that owner behind
            // the fullscreen target first so target -> visible window -> owner becomes possible.
            var ownerMoved = SetWindowPos(
                owner,
                insertAfter,
                0, 0, 0, 0,
                flags);

            // If the owner move succeeded, freeze it at the committed position while inserting
            // the visible surface. If it failed, let Windows adjust the owner for this first try.
            _ = SetWindowPos(
                handle,
                insertAfter,
                0, 0, 0, 0,
                flags | (ownerMoved ? SwpNoOwnerZOrder : 0u));
        }

        if (IsWindowBehind(handle, insertAfter))
        {
            return true;
        }

        // UIPI can reject using a High-integrity fullscreen HWND as hWndInsertAfter even though
        // PaperTodo may freely reorder its own Medium-integrity windows. Lower only our own group,
        // then read the real Z-order back. Keep the retry bounded so a pathological shell state
        // cannot create an unbounded 1-second controller loop against the same target.
        for (var attempt = 0; attempt < FullscreenAvoidanceFallbackAttempts; attempt++)
        {
            if (hasHiddenOwner)
            {
                _ = SetWindowPos(
                    owner,
                    HwndBottom,
                    0, 0, 0, 0,
                    flags);
            }

            _ = SetWindowPos(
                handle,
                HwndBottom,
                0, 0, 0, 0,
                flags | SwpNoOwnerZOrder);

            if (IsWindowBehind(handle, insertAfter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWindowBehind(IntPtr handle, IntPtr reference)
    {
        if (handle == IntPtr.Zero ||
            reference == IntPtr.Zero ||
            handle == reference ||
            !IsWindow(handle) ||
            !IsWindow(reference))
        {
            return false;
        }

        // GetWindow(GW_HWNDNEXT) walks downward through the top-level Z-order. Cap the traversal
        // defensively even though Windows' Z-order list is expected to be acyclic.
        var current = GetWindow(reference, GwHwndNext);
        for (var inspected = 0; current != IntPtr.Zero && inspected < 4096; inspected++)
        {
            if (current == handle)
            {
                return true;
            }

            current = GetWindow(current, GwHwndNext);
        }

        return false;
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
                if (batch.TryDefer(handle, bounds))
                {
                    return true;
                }
                if (batch.HasFailed)
                {
                    return false;
                }
            }
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

#if DEBUG
        var immediateStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var inspectStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var positionChanged = true;
        var sizeChanged = true;
        var inspected = GetWindowRect(handle, out var before);
#if DEBUG
        var inspectMilliseconds =
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(inspectStartedAt);
#endif
        if (inspected)
        {
            positionChanged = before.Left != bounds.Left || before.Top != bounds.Top;
            sizeChanged = before.Right - before.Left != bounds.Width ||
                before.Bottom - before.Top != bounds.Height;
            if (!positionChanged && !sizeChanged)
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"native.window phase=immediate-skip hwnd=0x{handle.ToInt64():X} " +
                    $"outcome=noop " +
                    $"callMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(immediateStartedAt):F3} " +
                    $"inspectMs={inspectMilliseconds:F3} setMs=0.000 " +
                    $"inspected=true nativeFlags=<none> axisFlags=strict " +
                    $"positionChanged=false sizeChanged=false " +
                    $"visibilityChanged=false zOrderChanged=false " +
                    $"bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}");
#endif
                return true;
            }
        }

        var nativeFlags = WindowNativeBoundsPolicy.FlagsForChanges(
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder,
            positionChanged,
            sizeChanged);
#if DEBUG
        var previousMessageProbe = BeginNativeGeometryMessageProbe(handle);
        var messageProbe = default(NativeGeometryMessageProbe);
        var setStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var setCompletedAt = 0L;
#endif
        bool applied;
#if DEBUG
        try
        {
#endif
            applied = SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                nativeFlags);
#if DEBUG
            setCompletedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        }
        finally
        {
            messageProbe = EndNativeGeometryMessageProbe(previousMessageProbe);
        }
        var setMilliseconds =
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                setStartedAt,
                setCompletedAt);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"native.window phase=immediate-set hwnd=0x{handle.ToInt64():X} " +
            $"outcome={(applied ? "success" : "failed")} " +
            $"callMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(immediateStartedAt):F3} " +
            $"inspectMs={inspectMilliseconds:F3} setMs={setMilliseconds:F3} " +
            $"inspected={inspected} nativeFlags=0x{nativeFlags:X4} axisFlags=strict " +
            $"windowPosChanging={messageProbe.WindowPosChangingCount} " +
            $"windowPosChanged={messageProbe.WindowPosChangedCount} " +
            $"moveMessages={messageProbe.MoveCount} sizeMessages={messageProbe.SizeCount} " +
            $"positionChanged={positionChanged} sizeChanged={sizeChanged} " +
            $"visibilityChanged=false zOrderChanged=false " +
            $"bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}");
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
            out _,
            "show-center");

    // Keep the cursor anchor private to this class: GetCursorPos is intentionally sampled while
    // the thread is System Aware, so these coordinates must never escape as a DeviceScreenPoint
    // or be reused for monitor selection / the final drop position in the PMv2 application.
    public static bool TryBeginSystemAwareWindowCaptionDragFromCursor(
        Window window,
        double widthDip,
        double heightDip)
    {
#if DEBUG
        var callStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var centered = TryCenterSystemAwareWindowAtCursor(
                window,
                widthDip,
                heightDip,
                out var cursorAnchor,
                "pre-loop-center");
#if DEBUG
        var centerMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callStartedAt);
#endif
        if (!centered)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                "drag.native phase=caption-loop-return outcome=center-failed " +
                $"centerMs={centerMs:F3} modalMs=0.000 " +
                "modalIncludesPointerHold=true");
#endif
            return false;
        }

#if DEBUG
        var modalStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var started = TryBeginWindowCaptionDrag(window, cursorAnchor);
#if DEBUG
        var handle = new WindowInteropHelper(window).Handle;
        EdgeCapsulePerformanceDiagnostics.Trace(
            "drag.native phase=caption-loop-return " +
            $"hwnd=0x{handle.ToInt64():X} outcome={(started ? "completed" : "not-started")} " +
            $"centerMs={centerMs:F3} " +
            $"modalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(modalStartedAt):F3} " +
            "modalIncludesPointerHold=true");
#endif
        return started;
    }

    private static bool TryCenterSystemAwareWindowAtCursor(
        Window window,
        double widthDip,
        double heightDip,
        out CursorPoint cursorPosition,
        string diagnosticPhase)
    {
#if DEBUG
        var callStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
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
#if DEBUG
            var previousMessageProbe = BeginNativeGeometryMessageProbe(handle);
            var messageProbe = default(NativeGeometryMessageProbe);
            var setStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var setCompletedAt = 0L;
#endif
            bool applied;
#if DEBUG
            try
            {
#endif
                applied = SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    left,
                    top,
                    width,
                    height,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
#if DEBUG
                setCompletedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            }
            finally
            {
                messageProbe = EndNativeGeometryMessageProbe(previousMessageProbe);
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.native phase={diagnosticPhase} hwnd=0x{handle.ToInt64():X} " +
                $"outcome={(applied ? "success" : "failed")} " +
                $"callMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callStartedAt):F3} " +
                $"setMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(setStartedAt, setCompletedAt):F3} " +
                "nativeFlags=0x0214 axisFlags=strict " +
                $"windowPosChanging={messageProbe.WindowPosChangingCount} " +
                $"windowPosChanged={messageProbe.WindowPosChangedCount} " +
                $"moveMessages={messageProbe.MoveCount} sizeMessages={messageProbe.SizeCount} " +
                $"target={left},{top},{width}x{height}");
#endif
            return applied;
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
    /// Defers visible HWND bounds submitted on the current UI thread and commits real changes
    /// through one HDWP. The HDWP itself is created lazily only after a window differs from its
    /// native rectangle, so pure WPF / unchanged animation frames do not call EndDeferWindowPos.
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
        private readonly Dictionary<IntPtr, WindowBatchDiagnostic> _windowDiagnostics = new();
        private readonly long _startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        private double _beginMilliseconds;
        private double _inspectMilliseconds;
        private double _deferMilliseconds;
        private double _endMilliseconds;
        private double _verifyMilliseconds;
        private int _windowPosChangingMessageCount;
        private int _windowPosChangedMessageCount;
        private int _moveMessageCount;
        private int _sizeMessageCount;

        private sealed class WindowBatchDiagnostic
        {
            public DeviceScreenRect Expected { get; set; }
            public DeviceScreenRect Actual { get; set; }
            public bool HasActual { get; set; }
            public bool SkippedUnchanged { get; set; }
            public bool MoveChanged { get; set; }
            public bool SizeChanged { get; set; }
            public bool ReplacedPendingBounds { get; set; }
            public uint NativeFlags { get; set; }
            public bool Verified { get; set; }
            public double InspectMilliseconds { get; set; }
            public double DeferMilliseconds { get; set; }
            public double VerifyMilliseconds { get; set; }
        }
#endif

        internal WindowDeviceBoundsBatch(int capacity)
        {
            _capacity = capacity;
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
        internal int RequestedWindowCount { get; private set; }
        internal int PendingWindowCount => _pendingBounds.Count;
        internal int UnchangedWindowCount { get; private set; }
        internal int MoveChangeCount { get; private set; }
        internal int SizeChangeCount { get; private set; }
        internal bool PerformedNativeCommit => _nativeCommitAttempted;

        internal bool TryDefer(IntPtr handle, DeviceScreenRect bounds)
        {
            RequestedWindowCount++;
            if (!IsAvailable || handle == IntPtr.Zero || bounds.IsEmpty)
            {
                return false;
            }

            var hasPendingBounds = _pendingBounds.TryGetValue(
                handle,
                out var alreadyPending);
            if (hasPendingBounds && alreadyPending == bounds)
            {
                UnchangedWindowCount++;
                return true;
            }

            // A failed inspection cannot prove either axis is stable. Preserve the historical
            // full-bounds fallback instead of adding no-change flags to an unknown rectangle.
            var moveChanged = true;
            var sizeChanged = true;
            var sameAsNative = false;
#if DEBUG
            var diagnostic = new WindowBatchDiagnostic { Expected = bounds };
            _windowDiagnostics[handle] = diagnostic;
            var inspectStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            if (GetWindowRect(handle, out var currentRect))
            {
                var current = new DeviceScreenRect(
                    currentRect.Left,
                    currentRect.Top,
                    currentRect.Right,
                    currentRect.Bottom);
                sameAsNative = current == bounds;
                moveChanged = current.Left != bounds.Left || current.Top != bounds.Top;
                sizeChanged = current.Width != bounds.Width || current.Height != bounds.Height;
#if DEBUG
                diagnostic.Actual = current;
                diagnostic.HasActual = true;
#endif
            }
#if DEBUG
            diagnostic.InspectMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(inspectStartedAt);
            _inspectMilliseconds += diagnostic.InspectMilliseconds;
#endif

            if (sameAsNative && !hasPendingBounds)
            {
                UnchangedWindowCount++;
#if DEBUG
                diagnostic.SkippedUnchanged = true;
#endif
                return true;
            }

            if (moveChanged)
            {
                MoveChangeCount++;
            }
            if (sizeChanged)
            {
                SizeChangeCount++;
            }
#if DEBUG
            diagnostic.MoveChanged = moveChanged;
            diagnostic.SizeChanged = sizeChanged;
#endif

            var baseNativeFlags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
            // A second request for the same HWND must fully replace the already-deferred
            // rectangle. Preserving an axis relative to the live HWND could accidentally retain
            // that axis from the first pending request instead of the new final target.
            var nativeFlags = hasPendingBounds
                ? baseNativeFlags
                : WindowNativeBoundsPolicy.FlagsForChanges(
                    baseNativeFlags,
                    moveChanged,
                    sizeChanged);
#if DEBUG
            diagnostic.ReplacedPendingBounds = hasPendingBounds;
            diagnostic.NativeFlags = nativeFlags;
#endif

            if (!_beginAttempted)
            {
                _beginAttempted = true;
#if DEBUG
                var beginStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                _deferredWindowPosition = BeginDeferWindowPos(_capacity);
#if DEBUG
                _beginMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(beginStartedAt);
#endif
                if (_deferredWindowPosition == IntPtr.Zero)
                {
                    // Preserve the historical fallback contract. TrySetWindowDeviceBounds will
                    // perform an ordinary SetWindowPos when BeginDeferWindowPos is unavailable.
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
                nativeFlags);
#if DEBUG
            diagnostic.DeferMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(deferStartedAt);
            _deferMilliseconds += diagnostic.DeferMilliseconds;
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

            var outcome = "committed";
            if (!_beginAttempted)
            {
                outcome = "noop";
#if DEBUG
                TraceBatch(outcome);
#endif
                return !HasFailed;
            }

            // BeginDeferWindowPos may be unavailable while the caller succeeds through the
            // immediate SetWindowPos fallback. A failed DeferWindowPos invalidates the whole HDWP.
            if (_deferredWindowPosition == IntPtr.Zero)
            {
                outcome = HasFailed ? "failed-before-end" : "immediate-fallback";
#if DEBUG
                TraceBatch(outcome);
#endif
                return !HasFailed;
            }

            _nativeCommitAttempted = true;
#if DEBUG
            var nativeLatency = EdgeNativeLatencyObservation.BeginBatch(_pendingBounds.Keys);
            var nativeJournal = EdgeNativeLatencyObservation.Enabled
                ? EdgeDiagnosticObservation.Begin("native.end-defer", this) : default;
            var previousMessageProbe = BeginNativeGeometryMessageProbe(IntPtr.Zero);
            var messageProbe = default(NativeGeometryMessageProbe);
            var endStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var endCompletedAt = 0L;
#endif
            bool committed;
#if DEBUG
            try
            {
#endif
                committed = EndDeferWindowPos(_deferredWindowPosition);
#if DEBUG
                endCompletedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            }
            finally
            {
                messageProbe = EndNativeGeometryMessageProbe(previousMessageProbe);
                nativeJournal.Dispose();
                nativeLatency.Dispose();
            }
            _endMilliseconds = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                endStartedAt,
                endCompletedAt);
            _windowPosChangingMessageCount = messageProbe.WindowPosChangingCount;
            _windowPosChangedMessageCount = messageProbe.WindowPosChangedCount;
            _moveMessageCount = messageProbe.MoveCount;
            _sizeMessageCount = messageProbe.SizeCount;
#endif
            _deferredWindowPosition = IntPtr.Zero;
            if (!committed)
            {
                HasFailed = true;
                outcome = "end-failed";
#if DEBUG
                TraceBatch(outcome);
#endif
                return false;
            }

            foreach (var (handle, expected) in _pendingBounds)
            {
#if DEBUG
                var verifyStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                var gotRect = GetWindowRect(handle, out var actual);
                var actualBounds = gotRect
                    ? new DeviceScreenRect(
                        actual.Left,
                        actual.Top,
                        actual.Right,
                        actual.Bottom)
                    : default;
                var verified = gotRect && actualBounds == expected;
#if DEBUG
                var verifyMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(verifyStartedAt);
                _verifyMilliseconds += verifyMs;
                if (!_windowDiagnostics.TryGetValue(handle, out var diagnostic))
                {
                    diagnostic = new WindowBatchDiagnostic { Expected = expected };
                    _windowDiagnostics[handle] = diagnostic;
                }
                diagnostic.Actual = actualBounds;
                diagnostic.HasActual = gotRect;
                diagnostic.Verified = verified;
                diagnostic.VerifyMilliseconds = verifyMs;
#endif
                if (!verified)
                {
                    HasFailed = true;
                    outcome = "verify-failed";
#if DEBUG
                    TraceBatch(outcome);
#endif
                    return false;
                }
            }
#if DEBUG
            TraceBatch(outcome);
#endif
            return true;
        }

#if DEBUG
        private void TraceBatch(string outcome)
        {
            var totalMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_startedAt);
            var shouldTraceDetails = HasFailed || totalMilliseconds >= 2.0;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"native.batch outcome={outcome} requested={RequestedWindowCount} " +
                $"pending={PendingWindowCount} unchanged={UnchangedWindowCount} " +
                $"moveChanges={MoveChangeCount} sizeChanges={SizeChangeCount} " +
                $"visibilityChanges=0 zOrderChanges=0 capacity={_capacity} " +
                $"nativeCommit={_nativeCommitAttempted} beginMs={_beginMilliseconds:F3} " +
                $"inspectMs={_inspectMilliseconds:F3} deferMs={_deferMilliseconds:F3} " +
                $"endMs={_endMilliseconds:F3} verifyMs={_verifyMilliseconds:F3} " +
                $"windowPosChanging={_windowPosChangingMessageCount} " +
                $"windowPosChanged={_windowPosChangedMessageCount} " +
                $"moveMessages={_moveMessageCount} sizeMessages={_sizeMessageCount} " +
                $"totalMs={totalMilliseconds:F3}");
            if (!shouldTraceDetails)
            {
                return;
            }

            foreach (var (handle, diagnostic) in _windowDiagnostics)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"native.batch.window hwnd=0x{handle.ToInt64():X} " +
                    $"expected={FormatBounds(diagnostic.Expected)} " +
                    $"actual={(diagnostic.HasActual ? FormatBounds(diagnostic.Actual) : "<unknown>")} " +
                    $"skipped={diagnostic.SkippedUnchanged} move={diagnostic.MoveChanged} " +
                    $"size={diagnostic.SizeChanged} replacedPending={diagnostic.ReplacedPendingBounds} " +
                    $"visibility=false zOrder=false " +
                    $"nativeFlags=0x{diagnostic.NativeFlags:X4} axisFlags=strict " +
                    $"inspectMs={diagnostic.InspectMilliseconds:F3} " +
                    $"deferMs={diagnostic.DeferMilliseconds:F3} " +
                    $"verifyMs={diagnostic.VerifyMilliseconds:F3} verified={diagnostic.Verified}");
            }
        }

        private static string FormatBounds(DeviceScreenRect bounds) =>
            $"{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}";
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
        NativeRect nativeRect;
        if (handle != IntPtr.Zero &&
            DwmGetWindowAttribute(
                handle,
                DwmWaExtendedFrameBounds,
                out nativeRect,
                Marshal.SizeOf<NativeRect>()) == 0)
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

    public static bool TryPostMouseButtonDown(
        IntPtr handle, EdgeCapsulePointerDown input, DeviceScreenPoint screenPoint)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) ||
            input.Message is not (0x0201 or 0x0204 or 0x0207)) return false;
        var clientPoint = new CursorPoint
        {
            X = (int)Math.Round(screenPoint.X, MidpointRounding.AwayFromZero),
            Y = (int)Math.Round(screenPoint.Y, MidpointRounding.AwayFromZero)
        };
        if (!ScreenToClient(handle, ref clientPoint)) return false;
        return PostMessage(handle, input.Message, input.KeyState,
            PackScreenPoint(clientPoint.X, clientPoint.Y));
    }

    /// <summary>
    /// Keeps a layered source HWND alive and rasterized while DirectComposition presents it from
    /// another target. HideWindow, zero-sized bounds and off-screen parking all invalidate that
    /// contract; DWMWA_CLOAK is the documented hand-off mechanism.
    /// </summary>
    public static bool TrySetWindowCloaked(IntPtr handle, bool cloaked)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return false;
        }

        var value = cloaked ? 1 : 0;
        if (DwmSetWindowAttribute(
            handle,
            DwmWaCloak,
            ref value,
            Marshal.SizeOf<int>()) != 0)
        {
            return false;
        }
        return DwmGetWindowAttribute(
                handle,
                DwmWaCloaked,
                out int flags,
                Marshal.SizeOf<int>()) == 0 &&
            ((flags & DwmCloakedApp) != 0) == cloaked;
    }

    public static bool IsWindowHandleAlive(IntPtr handle) =>
        handle != IntPtr.Zero && IsWindow(handle);

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

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

#if DEBUG
    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush", PreserveSig = true)]
    private static extern int DwmFlushNative();

    private static int DwmFlush()
    {
        using var edgeJournalDwm = EdgeDiagnosticObservation.Begin("native.dwm-flush");
        return DwmFlushNative();
    }
#else
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();
#endif

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(
        IntPtr hWnd,
        ref CursorPoint point);

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
