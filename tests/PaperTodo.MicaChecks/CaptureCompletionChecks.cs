using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;

internal static class CaptureCompletionChecks
{
    internal static void Run()
    {
        var window = new Window
        {
            Left = 100, Top = 100, Width = 100, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Red, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            Program.Pump();
            var hwnd = new WindowInteropHelper(window).Handle;
            foreach (var fail in new[] { true, false })
            foreach (var cancelBeforeDelivery in new[] { false, true })
            {
                DesktopBackgroundCapture? capture = null;
                Exception? failure = null;
                DesktopBackgroundCapture.Frame? frame = null;
                var failures = 0;
                var completions = 0;
                try
                {
                    capture = new DesktopBackgroundCapture(hwnd,
                        new DesktopBackgroundCapture.Region(0, 0, fail ? 0 : 80, 60, 0),
                        window.Dispatcher,
                        error =>
                        {
                            failure = error;
                            failures++;
                            capture!.Dispose();
                        },
                        () =>
                        {
                            frame = capture!.TakeLatest();
                            completions++;
                            // BackgroundSession disposes its capture when the ready callback runs.
                            capture.Dispose();
                        });
                    // Finish real worker work before pumping either notification, making their
                    // priority ordering deterministic instead of racing dispatcher execution.
                    Program.Assert(capture.Completion.Wait(TimeSpan.FromSeconds(5)),
                        "capture worker completed before terminal notification delivery");
                    if (cancelBeforeDelivery) capture.Dispose();
                    Program.Pump();
                    Console.WriteLine($"CAPTURE completion: fail={fail} cancelled={cancelBeforeDelivery} " +
                        $"failedCallbacks={failures} readyCallbacks={completions} frame={frame != null}");
                    if (cancelBeforeDelivery)
                        Program.Assert(failures == 0 && completions == 0 && frame == null,
                            "cancelled capture cannot notify or publish into a replacement session");
                    else if (fail)
                        Program.Assert(failures == 1 && completions == 0 &&
                            failure is InvalidOperationException && frame == null,
                            "capture failure must arrive exactly once, not be overtaken by an empty ready callback");
                    else
                        Program.Assert(failures == 0 && completions == 1 && frame != null,
                            "successful capture publishes exactly one immutable frame");
                    Program.Assert(DesktopBackgroundCapture.ReadAffinity(hwnd) == 0,
                        "terminal delivery and cancellation restore window capture affinity");
                }
                finally { capture?.Dispose(); }
            }
            Console.WriteLine("PASS capture terminal result: failure, success and cancellation before delivery");
        }
        finally { window.Close(); }
    }
}
