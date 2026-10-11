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
    private const int Pr260H8SourceSize = 48;
    private const int Pr260H8Travel = 120;
    private const double Pr260H8DurationSeconds = 0.8;
    private const int Pr260H8StallMilliseconds = 360;

    internal static int RunPr260H8InputTimingEntry(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit))
            return childExit;

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            ProxyH8InputTimingChecks();
            Console.WriteLine($"PR260 H8 checks: {assertions} assertions passed.");
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

    private static void ProxyH8InputTimingChecks()
    {
        Check(NativeInputGetCursorPos(out var originalCursor),
            "H8 timing checks require an interactive desktop");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "H8 timing checks require a monitor work area");

        var work = monitor.WorkArea;
        var outputBounds = new DeviceScreenRect(
            work.Left + 180,
            work.Top + 180,
            work.Left + 180 + Pr260H8SourceSize + Pr260H8Travel + 40,
            work.Top + 180 + Pr260H8SourceSize + 24);
        var initialBounds = new DeviceScreenRect(
            outputBounds.Left + 12,
            outputBounds.Top + 12,
            outputBounds.Left + 12 + Pr260H8SourceSize,
            outputBounds.Top + 12 + Pr260H8SourceSize);
        var sampleY = initialBounds.Top + Pr260H8SourceSize / 2;

        using var target = new NativeInputRemoteTarget(outputBounds, separateProcess: true);
        NativeInputUntil(() => target.Handle != IntPtr.Zero,
            "H8 independent lower process publishes its HWND");
        NativeInputGetWindowThreadProcessId(target.Handle, out var targetProcessId);
        Check(targetProcessId != Environment.ProcessId,
            "H8 lower input target runs in a different process");
        Check(NativeInputGetWindowThreadProcessId(target.Handle, out _) != NativeInputGetCurrentThreadId(),
            "H8 lower input target also uses a different native input thread");

        var baselinePoint = new DeviceScreenPoint(
            outputBounds.Left + outputBounds.Width / 2,
            outputBounds.Top + outputBounds.Height / 2);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 1 && target.Snapshot.Up == 1,
            "H8 lower process receives a baseline complete tagged gesture");
        Check(target.Snapshot == new NativeInputSnapshot(1, 1, 1, 1, 1),
            "H8 baseline gesture is observed exactly once by the lower process");
        Check(NativeInputSetWindowPos(target.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            "H8 lower target moves into the ordinary non-topmost band before proxy publication");

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
        NativeInputWindowProbe? inputProbe = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? compositionRoot = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        var proxyPresses = 0;
        try
        {
            sourceWindow.Show();
            WindowNative.ApplyNoActivateStyle(sourceWindow);
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            Check(sourceHandle != IntPtr.Zero, "H8 WPF source HWND was created");
            Check(WindowNative.TrySetWindowDeviceBounds(sourceWindow, initialBounds),
                "H8 WPF source HWND can be positioned in device pixels");
            sourceWindow.UpdateLayout();
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            Pr260H8FlushDesktop("initial WPF source render");

            output = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                topmost: true,
                point => EdgeCapsuleGeometry.Contains(initialBounds, point),
                _ => proxyPresses++,
                static () => { },
                static () => { },
                static () => { });
            Check(output != null, "H8 production proxy HWND pair can be created");

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
                "H8 production input HWND publishes the initial card region");
            Check(output.Show(outputBounds, topmost: true),
                "H8 production output/input HWND pair is visible");
            inputProbe = new NativeInputWindowProbe(output.InputHandle);
            Pr260H8FlushDesktop("initial proxy publication");

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        sourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false)
                }) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "H8 real WPF source can be cloaked behind the proxy");
            Pr260H8FlushDesktop("source cloak");

            var initialPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(initialPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 observes the initial redirected red source (count={initialPixels.Count})");
            Check(initialPixels.Right <= initialBounds.Right + 3,
                $"H8 initial redirected source stays within the published input region: {initialPixels}");

            animation = device.CreateAnimation();
            var from = (float)(initialBounds.Left - outputBounds.Left);
            var to = from + Pr260H8Travel;
            animation.SetAbsoluteBeginTime(Stopwatch.GetTimestamp()).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Pr260H8Travel / Pr260H8DurationSeconds),
                (float)(-3 * Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds)),
                (float)(Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds * Pr260H8DurationSeconds))).CheckError();
            animation.End(Pr260H8DurationSeconds, to).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();

            // Reproduce the real failure mode: the app/Dispatcher thread is unavailable while
            // DirectComposition keeps moving the visual on the compositor. A production 16 ms
            // DispatcherTimer cannot refresh the native HRGN during this interval.
            Thread.Sleep(Pr260H8StallMilliseconds);
            Pr260H8FlushDesktop("stalled compositor sample");

            var stalledPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(stalledPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 redirected source remains visible through the UI stall (count={stalledPixels.Count})");
            Check(stalledPixels.Right > initialBounds.Right + 12,
                $"H8 compositor advances visible pixels beyond the stale input region: initialRight={initialBounds.Right} stalled={stalledPixels}");
            Check(stalledPixels.Left > initialBounds.Left + 12,
                $"H8 compositor also leaves a visibly stale portion of the old input region: initialLeft={initialBounds.Left} stalled={stalledPixels}");

            // Freeze the already-observed compositor position so the following OS gestures test
            // a stable mismatched state rather than racing a continuously moving leading edge.
            visual.SetOffsetX(stalledPixels.Left - outputBounds.Left).CheckError();
            device.Commit().CheckError();
            Pr260H8FlushDesktop("freeze reproduced mismatch for native gesture routing");
            var frozenPixels = Pr260H8FindRedSpan(outputBounds, sampleY);

            var leakX = Math.Max(initialBounds.Right + 6, frozenPixels.Left + 6);
            Check(leakX < frozenPixels.Right - 2,
                $"H8 can choose a visible leading-edge point outside the stale HRGN: frozen={frozenPixels}");
            var leakPoint = new DeviceScreenPoint(leakX, sampleY);
            Check(Pr260H8IsRedPixel(leakPoint),
                "H8 leading-edge gesture point is visibly red on the composed desktop");
            Check(!NativeInputRegionContains(output.InputHandle, leakPoint),
                "H8 visible leading-edge gesture point is outside the stale native input HRGN");

            NativeInputMove(leakPoint);
            NativeInputClick();
            NativeInputUntil(() => target.Snapshot.Click == 2 && target.Snapshot.Up == 2,
                "H8 visible leading-edge gesture reaches the independent lower process");
            NativeInputPumpFor(60);
            Check(target.Snapshot == new NativeInputSnapshot(2, 2, 2, 2, 2) &&
                inputProbe.Counts.Down == 0 && inputProbe.Counts.Up == 0 && proxyPresses == 0,
                "H8 visible card pixels outside the stale HRGN leak one complete tagged gesture to the lower process");

            var stalePoint = new DeviceScreenPoint(initialBounds.Left + 6, sampleY);
            Check(NativeInputRegionContains(output.InputHandle, stalePoint),
                "H8 old card location remains inside the stale native input HRGN");
            Check(!Pr260H8IsRedPixel(stalePoint),
                "H8 old card location is visually empty after the compositor moved the card");
            NativeInputMove(stalePoint);
            NativeInputClick();
            NativeInputUntil(() => inputProbe.Counts.Down == 1 && inputProbe.Counts.Up == 1,
                "H8 visually empty stale region receives a complete tagged gesture on the input HWND");
            NativeInputPumpFor(60);
            Check(inputProbe.Counts.TaggedDown == 1 && inputProbe.Counts.TaggedUp == 1 &&
                target.Snapshot == new NativeInputSnapshot(2, 2, 2, 2, 2) && proxyPresses == 1,
                "H8 stale HRGN blocks the lower process even where the card is no longer visible");

            Console.WriteLine(
                $"PASS pr260-h8-real-dcomp-ui-stall-cross-process-routing initial={initialPixels} " +
                $"stalled={stalledPixels} frozen={frozenPixels} staleRight={initialBounds.Right} " +
                $"leakPoint={leakPoint.X:F0},{leakPoint.Y:F0} stallMs={Pr260H8StallMilliseconds}");
        }
        finally
        {
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
            try { inputProbe?.Dispose(); } catch { }
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionRoot?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { sourceWindow.Hide(); } catch { }
            try { sourceWindow.Close(); } catch { }
            try { NativeInputMove(new DeviceScreenPoint(originalCursor.X, originalCursor.Y)); } catch { }
        }
    }

    private static Pr260H8RedSpan Pr260H8FindRedSpan(DeviceScreenRect bounds, int y)
    {
        var dc = Pr260H8GetDC(IntPtr.Zero);
        Check(dc != IntPtr.Zero, "H8 GetDC(NULL) succeeds");
        try
        {
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var color = Pr260H8GetPixel(dc, x, y);
                if (!Pr260H8IsRed(color))
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0
                ? new Pr260H8RedSpan(0, 0, 0)
                : new Pr260H8RedSpan(left, right, count);
        }
        finally
        {
            _ = Pr260H8ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool Pr260H8IsRedPixel(DeviceScreenPoint point)
    {
        var dc = Pr260H8GetDC(IntPtr.Zero);
        Check(dc != IntPtr.Zero, "H8 GetDC(NULL) succeeds for point sampling");
        try
        {
            return Pr260H8IsRed(Pr260H8GetPixel(dc, (int)Math.Round(point.X), (int)Math.Round(point.Y)));
        }
        finally
        {
            _ = Pr260H8ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool Pr260H8IsRed(uint color)
    {
        if (color == 0xFFFFFFFF)
            return false;
        var r = (byte)(color & 0xFF);
        var g = (byte)((color >> 8) & 0xFF);
        var b = (byte)((color >> 16) & 0xFF);
        return r >= 180 && g <= 90 && b <= 90;
    }

    private static void Pr260H8FlushDesktop(string phase)
    {
        Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
        Console.WriteLine($"TRACE pr260-h8 phase={phase}");
    }

    private readonly record struct Pr260H8RedSpan(int Left, int Right, int Count)
    {
        public override string ToString() => $"[{Left},{Right}) count={Count}";
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr Pr260H8GetDC(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int Pr260H8ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint Pr260H8GetPixel(IntPtr dc, int x, int y);
}

internal static class Pr260H8InputTimingEntry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunPr260H8InputTimingEntry(args);
}
