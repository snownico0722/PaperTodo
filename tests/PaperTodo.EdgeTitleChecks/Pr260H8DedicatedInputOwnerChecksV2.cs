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
    internal static int RunPr260H8DedicatedInputOwnerV2Entry(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit))
            return childExit;

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            ProxyH8DedicatedInputOwnerV2Checks();
            Console.WriteLine($"PR260 H8 dedicated-owner v2 checks: {assertions} assertions passed.");
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

    private static void ProxyH8DedicatedInputOwnerV2Checks()
    {
        Check(NativeInputGetCursorPos(out var originalCursor),
            "H8 owner-v2 checks require an interactive desktop");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "H8 owner-v2 checks require a monitor work area");

        var work = monitor.WorkArea;
        var outputBounds = new DeviceScreenRect(
            work.Left + 220,
            work.Top + 220,
            work.Left + 220 + Pr260H8SourceSize + Pr260H8Travel + 48,
            work.Top + 220 + Pr260H8SourceSize + 28);
        var initialBounds = new DeviceScreenRect(
            outputBounds.Left + 14,
            outputBounds.Top + 14,
            outputBounds.Left + 14 + Pr260H8SourceSize,
            outputBounds.Top + 14 + Pr260H8SourceSize);
        var sampleY = initialBounds.Top + Pr260H8SourceSize / 2;

        using var target = new NativeInputRemoteTarget(outputBounds, separateProcess: true);
        NativeInputUntil(() => target.Handle != IntPtr.Zero,
            "H8 owner-v2 lower process publishes its HWND");
        NativeInputGetWindowThreadProcessId(target.Handle, out var targetProcessId);
        Check(targetProcessId != Environment.ProcessId,
            "H8 owner-v2 lower target is a separate process");
        Check(NativeInputGetWindowThreadProcessId(target.Handle, out _) != NativeInputGetCurrentThreadId(),
            "H8 owner-v2 lower target is a separate native thread");

        var baselinePoint = new DeviceScreenPoint(
            outputBounds.Left + outputBounds.Width / 2,
            outputBounds.Top + outputBounds.Height / 2);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 1 && target.Snapshot.Up == 1,
            "H8 owner-v2 lower process receives baseline tagged gesture");
        Check(target.Snapshot == new NativeInputSnapshot(1, 1, 1, 1, 1),
            "H8 owner-v2 baseline lower-process gesture is observed exactly once");
        Check(NativeInputSetWindowPos(target.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            "H8 owner-v2 lower target moves to the ordinary non-topmost band");

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
        Pr260H8DedicatedInputOwner? inputOwner = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? compositionRoot = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        Task<Pr260H8OwnerV2Observation>? observerTask = null;
        var sourceCloaked = false;
        try
        {
            sourceWindow.Show();
            WindowNative.ApplyNoActivateStyle(sourceWindow);
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            Check(sourceHandle != IntPtr.Zero, "H8 owner-v2 WPF source HWND was created");
            Check(WindowNative.TrySetWindowDeviceBounds(sourceWindow, initialBounds),
                "H8 owner-v2 WPF source can be positioned in device pixels");
            sourceWindow.UpdateLayout();
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            Pr260H8FlushDesktop("owner-v2 initial WPF source render");

            output = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                topmost: true,
                _ => false,
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            Check(output != null, "H8 owner-v2 production output pair can be created");
            Check(output!.TrySetInputRegions(Array.Empty<DeviceScreenRect>()),
                "H8 owner-v2 leaves current UI-owned production input HWND empty");
            Check(output.Show(outputBounds, topmost: true),
                "H8 owner-v2 production output pair is visible");
            Check(NativeInputRegionEmpty(output.InputHandle),
                "H8 owner-v2 current UI-owned input HWND remains empty");

            inputOwner = new Pr260H8DedicatedInputOwner(outputBounds, initialBounds);
            Check(inputOwner.Handle != IntPtr.Zero,
                "H8 owner-v2 input-only HWND is created on a dedicated native owner thread");
            Check(inputOwner.OwnerThreadId != NativeInputGetCurrentThreadId(),
                "H8 owner-v2 input HWND owner is distinct from the WPF/Dispatcher thread");
            Check(NativeInputRegionContains(inputOwner.Handle,
                    new DeviceScreenPoint(initialBounds.Left + 10, sampleY)),
                "H8 owner-v2 initial native region owns the visible source");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(output.Handle, topmost: true, out compositionTarget).CheckError();
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

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        sourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false)
                }) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "H8 owner-v2 real WPF source can be cloaked behind proxy output");
            Pr260H8FlushDesktop("owner-v2 source cloak");

            var initialPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(initialPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 owner-v2 sees initial redirected red source (count={initialPixels.Count})");
            Check(initialPixels.Right <= initialBounds.Right + 3,
                $"H8 owner-v2 initial redirected source is within initial native region: {initialPixels}");

            animation = device.CreateAnimation();
            var from = (float)(initialBounds.Left - outputBounds.Left);
            var to = from + Pr260H8Travel;
            var startTimestamp = Stopwatch.GetTimestamp();
            animation.SetAbsoluteBeginTime(startTimestamp).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Pr260H8Travel / Pr260H8DurationSeconds),
                (float)(-3 * Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds)),
                (float)(Pr260H8Travel / (Pr260H8DurationSeconds * Pr260H8DurationSeconds * Pr260H8DurationSeconds))).CheckError();
            animation.End(Pr260H8DurationSeconds, to).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();
            inputOwner.StartAnimation(startTimestamp, Pr260H8Travel, Pr260H8DurationSeconds);

            // The observer never calls GetWindowRgn cross-thread. LastAppliedLeft is published
            // only after SetWindowRgn succeeds on the input HWND's own thread, so it is a
            // non-blocking witness of the actual native publication boundary.
            observerTask = Task.Run(() =>
                Pr260H8ObserveDedicatedOwnerDuringStallV2(
                    inputOwner,
                    outputBounds,
                    initialBounds,
                    sampleY,
                    TimeSpan.FromMilliseconds(300)));

            Thread.Sleep(Pr260H8StallMilliseconds);
            Check(observerTask.Wait(TimeSpan.FromSeconds(3)),
                "H8 owner-v2 finite observer finishes while/after WPF UI stall");
            var observation = observerTask.Result;
            Pr260H8FlushDesktop("owner-v2 stalled compositor sample");

            var stalledPixels = Pr260H8FindRedSpan(outputBounds, sampleY);
            Check(stalledPixels.Count >= Pr260H8SourceSize / 2,
                $"H8 owner-v2 redirected source remains visible during UI stall (count={stalledPixels.Count})");
            Check(stalledPixels.Right > initialBounds.Right + 12,
                $"H8 owner-v2 compositor advances while WPF UI is stalled: initialRight={initialBounds.Right} stalled={stalledPixels}");
            Check(inputOwner.RegionUpdateFailures == 0,
                "H8 owner-v2 owner-thread region publications report no native failures");
            Check(inputOwner.RegionUpdates >= 5,
                $"H8 owner-v2 owner thread keeps publishing regions while WPF UI is stalled (updates={inputOwner.RegionUpdates})");
            Check(observation.Samples >= 5,
                $"H8 owner-v2 observer sampled composed pixels while WPF UI was stalled (samples={observation.Samples})");
            Check(observation.VisibleOwnershipMisses == 0,
                $"H8 owner-v2 has no sampled >12px visible-leading authority misses (misses={observation.VisibleOwnershipMisses})");
            Check(observation.StaleEmptyBlocks == 0,
                $"H8 owner-v2 has no sampled >12px stale trailing authority blocks (blocks={observation.StaleEmptyBlocks})");

            var livePoint = new DeviceScreenPoint(stalledPixels.Left + 12, sampleY);
            var vacatedPoint = new DeviceScreenPoint(stalledPixels.Left - 12, sampleY);
            Check(Pr260H8IsRedPixel(livePoint),
                "H8 owner-v2 wake sample chooses a visibly occupied point");
            Check(NativeInputRegionContains(inputOwner.Handle, livePoint),
                "H8 owner-v2 actual HRGN owns the moved visible source after WPF stall");
            if (vacatedPoint.X >= initialBounds.Left)
            {
                Check(!Pr260H8IsRedPixel(vacatedPoint),
                    "H8 owner-v2 wake sample chooses a visually vacated trailing point");
                Check(!NativeInputRegionContains(inputOwner.Handle, vacatedPoint),
                    "H8 owner-v2 actual HRGN releases the visually vacated trailing point");
            }

            // Freeze both authorities to one sampled position before sending OS gestures.
            // The 300 ms observer above is the part that checks independent progress while
            // the UI is unavailable; this final section checks real cross-process routing.
            var frozenBounds = new DeviceScreenRect(
                stalledPixels.Left,
                initialBounds.Top,
                stalledPixels.Left + Pr260H8SourceSize,
                initialBounds.Bottom);
            visual.SetOffsetX(frozenBounds.Left - outputBounds.Left).CheckError();
            device.Commit().CheckError();
            inputOwner.SetStaticRegion(frozenBounds);
            NativeInputUntil(() => Math.Abs(inputOwner.LastAppliedLeft - frozenBounds.Left) <= 1,
                "H8 owner-v2 freezes native input to sampled composed position");
            Pr260H8FlushDesktop("owner-v2 freeze matched visual/input authorities");
            var frozenPixels = Pr260H8FindRedSpan(outputBounds, sampleY);

            var ownerBefore = inputOwner.Snapshot;
            var targetBefore = target.Snapshot;
            var ownedPoint = new DeviceScreenPoint(frozenPixels.Left + 12, sampleY);
            Check(Pr260H8IsRedPixel(ownedPoint) && NativeInputRegionContains(inputOwner.Handle, ownedPoint),
                "H8 owner-v2 visible gesture point is both visible and owned");
            NativeInputMove(ownedPoint);
            NativeInputClick();
            NativeInputUntil(() => inputOwner.Snapshot.Down == ownerBefore.Down + 1 &&
                                 inputOwner.Snapshot.Up == ownerBefore.Up + 1,
                "H8 owner-v2 visible gesture reaches dedicated input owner");
            NativeInputPumpFor(60);
            Check(target.Snapshot == targetBefore,
                "H8 owner-v2 visible gesture does not leak to lower process");

            var emptyPoint = new DeviceScreenPoint(initialBounds.Left + 6, sampleY);
            Check(!Pr260H8IsRedPixel(emptyPoint) && !NativeInputRegionContains(inputOwner.Handle, emptyPoint),
                "H8 owner-v2 old location is visually empty and not owned");
            NativeInputMove(emptyPoint);
            NativeInputClick();
            NativeInputUntil(() => target.Snapshot.Click == targetBefore.Click + 1 &&
                                 target.Snapshot.Up == targetBefore.Up + 1,
                "H8 owner-v2 vacated-region gesture reaches lower process");
            NativeInputPumpFor(60);
            Check(inputOwner.Snapshot == new Pr260H8OwnerInputSnapshot(ownerBefore.Down + 1, ownerBefore.Up + 1),
                "H8 owner-v2 vacated-region gesture is not intercepted by dedicated owner");

            Console.WriteLine(
                $"PASS pr260-h8-dedicated-input-owner-v2 samples={observation.Samples} " +
                $"visibleMisses={observation.VisibleOwnershipMisses} staleBlocks={observation.StaleEmptyBlocks} " +
                $"regionUpdates={inputOwner.RegionUpdates} failures={inputOwner.RegionUpdateFailures} " +
                $"initial={initialPixels} stalled={stalledPixels} frozen={frozenPixels} " +
                $"lastRegionLeft={inputOwner.LastAppliedLeft}");
        }
        finally
        {
            try { observerTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
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
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionRoot?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { inputOwner?.Dispose(); } catch { }
            try { output?.Hide(); } catch { }
            try { output?.Dispose(); } catch { }
            try { sourceWindow.Hide(); } catch { }
            try { sourceWindow.Close(); } catch { }
            try { NativeInputMove(new DeviceScreenPoint(originalCursor.X, originalCursor.Y)); } catch { }
        }
    }

    private static Pr260H8OwnerV2Observation Pr260H8ObserveDedicatedOwnerDuringStallV2(
        Pr260H8DedicatedInputOwner inputOwner,
        DeviceScreenRect outputBounds,
        DeviceScreenRect initialBounds,
        int sampleY,
        TimeSpan duration)
    {
        var result = new Pr260H8OwnerV2Observation();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var span = Pr260H8FindRedSpanWithoutAssertions(outputBounds, sampleY);
            if (span.Count >= Pr260H8SourceSize / 2 && span.Left > initialBounds.Left + 2)
            {
                var ownerLeft = inputOwner.LastAppliedLeft;
                var ownerRight = ownerLeft + Pr260H8SourceSize;
                var visibleX = span.Left + 12;
                var trailingX = span.Left - 12;
                var visibleOwned = visibleX >= ownerLeft && visibleX < ownerRight;
                var staleBlocked = trailingX >= initialBounds.Left &&
                                   trailingX >= ownerLeft && trailingX < ownerRight;
                result.Record(visibleOwned, staleBlocked);
            }
            Thread.Sleep(4);
        }
        return result;
    }

    private sealed class Pr260H8OwnerV2Observation
    {
        internal int Samples { get; private set; }
        internal int VisibleOwnershipMisses { get; private set; }
        internal int StaleEmptyBlocks { get; private set; }

        internal void Record(bool visibleOwned, bool staleBlocked)
        {
            Samples++;
            if (!visibleOwned) VisibleOwnershipMisses++;
            if (staleBlocked) StaleEmptyBlocks++;
        }
    }
}

internal static class Pr260H8DedicatedInputOwnerV2Entry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunPr260H8DedicatedInputOwnerV2Entry(args);
}
