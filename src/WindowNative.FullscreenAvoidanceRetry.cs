using System.Windows.Interop;
using System.Windows.Threading;

namespace PaperTodo;

internal static partial class WindowNative
{
    // The reproduced delayed failure is a visible PaperWindow plus its hidden owner. Other
    // surfaces use the normal verified native path; do not introduce a global retry loop.
    public static bool ApplyTopmostZOrder(PaperWindow window, bool topmost, IntPtr insertAfter)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var applied = ApplyTopmostZOrder(handle, topmost, insertAfter);
        if (!applied && !topmost && insertAfter != IntPtr.Zero &&
            GetForegroundWindow() == insertAfter &&
            window.CanRetryFullscreenAvoidance(insertAfter))
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

        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        EventHandler? tick = null;
        tick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= tick;

            if (window.Dispatcher.HasShutdownStarted ||
                !window.CanRetryFullscreenAvoidance(insertAfter) ||
                !IsWindow(handle) ||
                new WindowInteropHelper(window).Handle != handle ||
                GetForegroundWindow() != insertAfter ||
                !FullscreenForegroundWindowDetector.TryGetFullscreenWindow(
                    out var fullscreenWindow, allowGlobalScan: false) ||
                fullscreenWindow != insertAfter)
            {
                return;
            }

            // IntPtr deliberately bypasses the scheduling overload: this retry cannot enqueue
            // another retry, and a hidden/unpinned/moved/replaced target cancels the old request.
            _ = ApplyTopmostZOrder(handle, topmost: false, insertAfter);
        };

        timer.Tick += tick;
        timer.Start();
    }
}
