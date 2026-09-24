using System.Windows.Interop;
using System.Windows.Threading;

namespace PaperTodo;

internal static partial class WindowNative
{
    // The reproduced failure is the visible PaperWindow + hidden-owner pair. Keep recovery scoped
    // to that proven path: the normal call already verifies Z-order and falls back to HWND_BOTTOM.
    // Only if that whole attempt still fails do we retry once after Windows/WPF has had time to
    // settle. Do not turn this into a general polling or permission-detection mechanism.
    public static bool ApplyTopmostZOrder(
        PaperWindow window,
        bool topmost,
        IntPtr insertAfter)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var applied = ApplyTopmostZOrder(handle, topmost, insertAfter);
        if (!applied &&
            !topmost &&
            insertAfter != IntPtr.Zero &&
            GetForegroundWindow() == insertAfter)
        {
            QueueSingleFullscreenAvoidanceRetry(window, handle, insertAfter);
        }

        return applied;
    }

    private static void QueueSingleFullscreenAvoidanceRetry(
        PaperWindow window,
        IntPtr handle,
        IntPtr insertAfter)
    {
        if (window.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        EventHandler? tick = null;
        tick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= tick;

            if (window.Dispatcher.HasShutdownStarted ||
                window.Topmost ||
                !IsWindow(handle) ||
                !FullscreenForegroundWindowDetector.TryGetFullscreenWindow(
                    out var fullscreenWindow,
                    allowGlobalScan: false) ||
                fullscreenWindow != insertAfter)
            {
                return;
            }

            // IntPtr overload deliberately bypasses this PaperWindow overload, so this is the one
            // and only delayed retry for the failed application.
            _ = ApplyTopmostZOrder(handle, topmost: false, insertAfter);
        };

        timer.Tick += tick;
        timer.Start();
    }
}
