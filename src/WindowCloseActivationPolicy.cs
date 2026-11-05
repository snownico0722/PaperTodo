namespace PaperTodo;

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
