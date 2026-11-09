using System.Runtime.InteropServices;

namespace PaperTodo;

internal static partial class WindowNative
{
    internal static bool TryHandoffForegroundBeforeClose(
        IntPtr closingWindow,
        Func<IntPtr, bool>? canActivate = null) =>
        WindowCloseActivationPolicy.TryHandoff(
            closingWindow,
            GetForegroundWindow,
            static window => GetWindow(window, GwHwndNext),
            window => IsCloseActivationTarget(window, closingWindow) &&
                (canActivate?.Invoke(window) ?? true),
            SetForegroundWindow);

    internal static bool IsCloseActivationTarget(IntPtr window, IntPtr closingWindow)
    {
        if (window == IntPtr.Zero || window == closingWindow ||
            !IsWindow(window) || !IsWindowVisible(window) ||
            !IsWindowEnabled(window) || IsIconic(window) ||
            (GetWindowLong(window, GwlExStyle) & (WsExNoActivate | WsExToolWindow)) != 0)
        {
            return false;
        }

        // Cloaked HWNDs can retain WS_VISIBLE (for example on another virtual desktop).
        if (DwmGetWindowAttribute(window, DwmWaCloaked, out int cloaked, sizeof(int)) >= 0 &&
            cloaked != 0)
        {
            return false;
        }

        // Exclude only windows that will disappear with this paper. Do not exclude our entire
        // process or all owned windows: other papers intentionally have their own hidden owner.
        var owner = GetWindow(window, GwOwner);
        for (var inspected = 0; owner != IntPtr.Zero && inspected < 64; inspected++)
        {
            if (owner == closingWindow)
            {
                return false;
            }
            owner = GetWindow(owner, GwOwner);
        }
        return owner == IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}

// Decide while the closing HWND still owns foreground, before WPF tears down its hidden owner.
// Delegates keep the ordering and no-steal contract executable without a desktop session.
internal static class WindowCloseActivationPolicy
{
    internal static bool TryHandoff(
        IntPtr closingWindow,
        Func<IntPtr> foregroundWindow,
        Func<IntPtr, IntPtr> nextWindow,
        Func<IntPtr, bool> canActivate,
        Func<IntPtr, bool> activate)
    {
        if (closingWindow == IntPtr.Zero || foregroundWindow() != closingWindow)
        {
            return false;
        }

        var candidate = nextWindow(closingWindow);
        for (var inspected = 0; candidate != IntPtr.Zero && inspected < 4096; inspected++)
        {
            if (candidate == closingWindow)
            {
                return false;
            }

            if (canActivate(candidate))
            {
                // Never overwrite a foreground change that happened while inspecting the stack.
                // If Windows rejects this target, let normal close activation handle the fallback;
                // trying further windows would jump over the intended next window again.
                return foregroundWindow() == closingWindow && activate(candidate);
            }

            var next = nextWindow(candidate);
            if (next == candidate)
            {
                return false;
            }
            candidate = next;
        }

        return false;
    }
}
