using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    internal static int RunPr260H8CrossThreadRegionEntry(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit))
            return childExit;

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            ProbePr260H8CrossThreadRegionUpdates();
            Console.WriteLine($"PR260 H8 cross-thread region probe: {assertions} assertions passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static void ProbePr260H8CrossThreadRegionUpdates()
    {
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "H8 worker probe requires a monitor work area");

        var work = monitor.WorkArea;
        var outputBounds = new DeviceScreenRect(
            work.Left + 220,
            work.Top + 220,
            work.Left + 220 + Pr260H8SourceSize + Pr260H8Travel + 40,
            work.Top + 220 + Pr260H8SourceSize + 24);
        var initialBounds = new DeviceScreenRect(
            outputBounds.Left + 12,
            outputBounds.Top + 12,
            outputBounds.Left + 12 + Pr260H8SourceSize,
            outputBounds.Top + 12 + Pr260H8SourceSize);
        var sampleY = initialBounds.Top + Pr260H8SourceSize / 2;

        var root = new Border
        {
            Width = Pr260H8SourceSize,
            Height = Pr260H8SourceSize,
            Background = Brushes.Red,
            SnapsToDevicePixels = true
        };
        var sourceWindow = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = true,
            Left = -32000,
            Top = -32000,
            Width = Pr260H8SourceSize,
            Height = Pr260H8SourceSize,
            Content = root
        };

        EdgeCapsuleQueueProxyWindow? output = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? compositionRoot = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        Thread? updater = null;
        using var stop = new ManualResetEventSlim(false);
        var attempts = 0;
        var completed = 0;
        var successful = 0;
        var lastCompletedLeft = initialBounds.Left;
        var firstCompletionTimestamp = 0L;
        try
        {
            sourceWindow.Show();
            WindowNative.ApplyNoActivateStyle(sourceWindow);
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            Check(sourceHandle != IntPtr.Zero, "H8 worker probe WPF source HWND was created");
            Check(WindowNative.TrySetWindowDeviceBounds(sourceWindow, initialBounds),
                "H8 worker probe WPF source can be positioned in device pixels");
            sourceWindow.UpdateLayout();
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            Pr260H8FlushDesktop("worker-probe initial WPF render");

            output = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                topmost: true,
                _ => true,
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            Check(output != null, "H8 worker probe production proxy HWND pair can be created");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(output!.Handle, topmost: true, out compositionTarget).CheckError();
            device.CreateVisual(out compositionRoot).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var liveSurface).CheckError();
            surface = liveSurface;
            device.CreateVisual(out visual).CheckError();
            visual.SetContent(liveSurface).CheckError();
            visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            visual.SetOffsetX(initialBounds.Left - outputBounds.Left).CheckError();
            visual.SetOffsetY(initialBounds.Top - outputBounds.Top).CheckError();
            compositionRoot.AddVisual(visual, insertAbove: true, referenceVisual: null!).CheckError();
            compositionTarget.SetRoot(compositionRoot).CheckError();
            device.Commit().CheckError();

            Check(output.TrySetInputRegions(new[] { initialBounds }),
                "H8 worker probe publishes the initial input region");
            Check(output.Show(outputBounds, topmost: true),
                "H8 worker probe output/input HWND pair is visible");
            Pr260H8FlushDesktop("worker-probe initial proxy publication");

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        sourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false)
                }) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "H8 worker probe real WPF source can be cloaked");
            Pr260H8FlushDesktop("worker-probe source cloak");

            animation = device.CreateAnimation();
            var from = (float)(initialBounds.Left - outputBounds.Left);
            var to = from + Pr260H8Travel;
            var animationStarted = Stopwatch.GetTimestamp();
            animation.SetAbsoluteBeginTime(animationStarted).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Pr260H8Travel / Pr260H8DurationSeconds),
                (float)(-3 * Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds)),
                (float)(Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds * Pr260H8DurationSeconds))).CheckError();
            animation.End(Pr260H8DurationSeconds, to).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();

            // Test the smallest possible alternative before considering a dedicated input-HWND
            // owner thread: keep the production input HWND on the UI thread, but ask a worker to
            // update its HRGN from the same QPC animation clock while this thread is unavailable.
            // SetWindowRgn may synchronously message the HWND owner; this probe determines whether
            // such a worker can actually make progress during the exact H8 stall window.
            updater = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    Interlocked.Increment(ref attempts);
                    var elapsedSeconds = Math.Max(0,
                        (Stopwatch.GetTimestamp() - animationStarted) / (double)Stopwatch.Frequency);
                    var t = Math.Clamp(elapsedSeconds / Pr260H8DurationSeconds, 0, 1);
                    var eased = 1 - Math.Pow(1 - t, 3);
                    var left = initialBounds.Left + (int)Math.Round(Pr260H8Travel * eased);
                    var region = new DeviceScreenRect(
                        left,
                        initialBounds.Top,
                        left + Pr260H8SourceSize,
                        initialBounds.Bottom);
                    var ok = output.TrySetInputRegions(new[] { region });
                    var completedNow = Interlocked.Increment(ref completed);
                    if (ok)
                    {
                        Interlocked.Increment(ref successful);
                        Volatile.Write(ref lastCompletedLeft, left);
                    }
                    if (completedNow == 1)
                        Volatile.Write(ref firstCompletionTimestamp, Stopwatch.GetTimestamp());
                    if (t >= 1)
                        break;
                    Thread.Sleep(4);
                }
            })
            {
                IsBackground = true,
                Name = "PR260-H8-input-region-probe"
            };
            updater.Start();

            Thread.Sleep(Pr260H8StallMilliseconds);
            var attemptsAtWake = Volatile.Read(ref attempts);
            var completedAtWake = Volatile.Read(ref completed);
            var successfulAtWake = Volatile.Read(ref successful);
            var leftAtWake = Volatile.Read(ref lastCompletedLeft);
            var firstCompletedAtWake = Volatile.Read(ref firstCompletionTimestamp);
            var pixelsAtWake = Pr260H8FindRedSpan(outputBounds, sampleY);
            var leadingPoint = new DeviceScreenPoint(
                Math.Max(initialBounds.Right + 4, pixelsAtWake.Right - 8),
                sampleY);
            var regionOwnsLeadingPixelAtWake =
                pixelsAtWake.Count > 0 &&
                leadingPoint.X >= pixelsAtWake.Left && leadingPoint.X < pixelsAtWake.Right &&
                NativeInputRegionContains(output.InputHandle, leadingPoint);

            Console.WriteLine(
                $"PROBE pr260-h8-cross-thread-region attemptsAtWake={attemptsAtWake} " +
                $"completedAtWake={completedAtWake} successfulAtWake={successfulAtWake} " +
                $"lastCompletedLeft={leftAtWake} initialLeft={initialBounds.Left} " +
                $"pixelsAtWake={pixelsAtWake} ownsLeadingPixel={regionOwnsLeadingPixelAtWake} " +
                $"firstCompletionTimestamp={firstCompletedAtWake} stallMs={Pr260H8StallMilliseconds}");

            Check(attemptsAtWake > 0,
                "H8 worker probe attempted a cross-thread HRGN update during the UI stall");
            Check(pixelsAtWake.Right > initialBounds.Right + 12,
                "H8 worker probe compositor still advances while the UI thread is stalled");

            // This experiment intentionally accepts either outcome. The logged completion counters
            // decide the next production experiment: if zero, a worker cannot rescue a UI-owned
            // input HWND; if positive and the leading pixel is owned, a smaller worker-based design
            // remains viable. Do not encode the platform assumption into the test itself.
            stop.Set();
            NativeInputPumpFor(120);
            updater.Join(1000);
            Check(!updater.IsAlive,
                "H8 worker probe update thread exits after the UI thread resumes");
        }
        finally
        {
            stop.Set();
            try { NativeInputPumpFor(60); } catch { }
            try { updater?.Join(500); } catch { }
            try
            {
                if (sourceCloaked)
                {
                    var handle = new WindowInteropHelper(sourceWindow).Handle;
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        new[]
                        {
                            new WindowNative.WindowCloakChange(
                                handle,
                                Cloaked: false,
                                RollbackCloaked: true)
                        });
                }
            }
            catch { }
            try
            {
                if (compositionTarget != null && device != null)
                {
                    compositionTarget.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            catch { }
            try { output?.Hide(); } catch { }
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionRoot?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { sourceWindow.Hide(); } catch { }
            try { sourceWindow.Close(); } catch { }
        }
    }
}

internal static class Pr260H8CrossThreadRegionEntry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunPr260H8CrossThreadRegionEntry(args);
}
