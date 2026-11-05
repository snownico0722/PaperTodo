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
