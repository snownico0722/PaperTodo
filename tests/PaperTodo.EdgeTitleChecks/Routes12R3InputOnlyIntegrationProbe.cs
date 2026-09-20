using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static class Routes12R3Entry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunRoutes12R3(args);
}

internal static partial class Program
{
    private readonly record struct Routes12R3Span(int Top, int Bottom, int Count)
    {
        internal bool IsEmpty => Count <= 0;
        public override string ToString() => $"[{Top},{Bottom}) count={Count}";
    }

    private readonly record struct Routes12R3Observation(long Timestamp, Routes12R3Span Pixels, Routes12R3Span Input);

    internal static int RunRoutes12R3(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit)) return childExit;
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Routes12PolicyChecks();
            ProxyH8InputTimingChecks();
            Routes12R3InputOnlyProductionBoundaryCase();
            Console.WriteLine($"ROUTES12 R3: {assertions} assertions passed. Input-only native ownership is wired into the production queue window and DComp animation clock; full application capture/drag handoff, mixed-DPI and physical high-refresh acceptance remain open.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static void Routes12R3InputOnlyProductionBoundaryCase()
    {
        const string route = "route1-input-only-production-boundary";
        Check(NativeInputGetCursorPos(out var cursor), route + ": interactive desktop available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 180,
            monitor.WorkArea.Top + 160,
            monitor.WorkArea.Left + 500,
            monitor.WorkArea.Top + 500);
        var initial = new DeviceScreenRect(bounds.Left + 16, bounds.Top + 20, bounds.Left + 64, bounds.Top + 68);
        var targetRect = new DeviceScreenRect(initial.Left + 120, initial.Top, initial.Right + 120, initial.Bottom);
        var sampleY = initial.Top + 24;
        var environmentChanges = 0;
        var presses = 0;
        var applied = new[] { initial };

        using var lower = new NativeInputRemoteTarget(bounds, separateProcess: true);
        NativeInputUntil(() => lower.Handle != IntPtr.Zero, route + ": lower process ready");
        var baselinePoint = new DeviceScreenPoint(bounds.Right - 28, bounds.Bottom - 28);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => lower.Snapshot.Click == 1 && lower.Snapshot.Up == 1,
            route + ": lower process receives baseline gesture");
        Check(NativeInputSetWindowPos(lower.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            route + ": lower process leaves the topmost band");

        var source = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Left = -32000,
            Top = -32000,
            Width = 48,
            Height = 48,
            Topmost = true,
            Content = new Border { Background = Brushes.Red }
        };
        EdgeCapsuleQueueProxyWindow? pair = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        try
        {
            pair = EdgeCapsuleQueueProxyWindow.TryCreate(
                bounds,
                true,
                point => applied.Any(rect => EdgeCapsuleGeometry.Contains(rect, point)),
                _ => presses++,
                () => environmentChanges++,
                static () => { },
                static () => { });
            Check(pair != null, route + ": production queue HWND wrapper created");
            Check(pair!.InputOwnerThreadId != 0 && pair.InputOwnerThreadId != NativeInputGetCurrentThreadId(),
                route + ": only the input HWND belongs to the dedicated native owner thread");
            Check(NativeInputGetWindowThreadProcessId(pair.Handle, out _) == NativeInputGetCurrentThreadId(),
                route + ": DComp output HWND remains owned by the WPF/UI thread");
            Check(NativeInputGetWindowThreadProcessId(pair.InputHandle, out _) == pair.InputOwnerThreadId,
                route + ": finite input HWND belongs to the dedicated native thread");
            Check(pair.TrySetInputRegions(applied) && pair.Show(bounds, true),
                route + ": initial finite input region and output publication succeed");

            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), route + ": WPF source positioned");
            source.UpdateLayout();
            NativeInputPumpFor(80);
            var sourceHandle = new WindowInteropHelper(source).Handle;

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(pair.Handle, true, out compositionTarget).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError();
            surface = live;
            visual.SetContent(live).CheckError();
            visual.SetOffsetX(initial.Left - bounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - bounds.Top).CheckError();
            compositionTarget.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, route + ": cover is visible before the real WPF source is cloaked");
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());

            var initialPixels = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(initialPixels.Count >= 40 && Math.Abs(initialPixels.Left - initial.Left) <= 3,
                $"{route}: physical desktop sees initial live pixels ({initialPixels})");

            var startFrame = Routes12R3Frame(initial);
            var targetFrame = Routes12R3Frame(targetRect);
            var member = new EdgeCapsuleQueueProxyMemberPlan("r3", startFrame, startFrame, targetFrame);
            var startedAt = Stopwatch.GetTimestamp();
            const int durationMilliseconds = 900;
            var ticket = new EdgeCapsuleQueueInputAnimationTicket(startedAt, durationMilliseconds, [member]);
            animation = device.CreateAnimation();
            var from = (float)(initial.Left - bounds.Left);
            var travel = targetRect.Left - initial.Left;
            var durationSeconds = durationMilliseconds / 1000.0;
            animation.SetAbsoluteBeginTime(startedAt).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * travel / durationSeconds),
                (float)(-3 * travel / (durationSeconds * durationSeconds)),
                (float)(travel / (durationSeconds * durationSeconds * durationSeconds))).CheckError();
            animation.End(durationSeconds, from + travel).CheckError();
            visual.SetOffsetX(animation).CheckError();

            // Deliberately hold the DComp transaction before Commit. The dedicated input owner must
            // remain at the resting HRGN and inactive; starting it before Commit would reproduce an
            // input-ahead window whenever DComp submission stalls.
            var preCommitInput = Routes12CaptureInputSpan(pair.InputHandle, bounds, sampleY);
            Thread.Sleep(90);
            var delayedInput = Routes12CaptureInputSpan(pair.InputHandle, bounds, sampleY);
            Check(!pair.IsInputAnimationActive && !preCommitInput.IsEmpty &&
                delayedInput.Left == preCommitInput.Left && delayedInput.Right == preCommitInput.Right,
                route + ": delayed DComp commit cannot advance native input authority early");

            device.Commit().CheckError();
            Check(pair.TryStartInputAnimation(ticket),
                route + ": production input owner activates the shared absolute-QPC ticket only after DComp commit");
            Thread.Sleep(30);
            var updatesBeforeRefresh = pair.InputRegionUpdateCount;
            Check(pair.TrySetInputRegions([initial]) && pair.IsInputAnimationActive &&
                pair.InputRegionUpdateCount == updatesBeforeRefresh,
                route + ": stale non-empty UI refresh cannot steal the active native animation clock");

            var updatesBeforeStall = pair.InputRegionUpdateCount;
            var observer = Task.Run(() => Routes12ObserveDynamic(pair.InputHandle, bounds, sampleY, 430));
            Thread.Sleep(430);
            var observations = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(pair.InputRegionUpdateCount >= updatesBeforeStall + 4,
                route + ": native HRGN keeps advancing while the WPF/UI thread is unavailable");
            var dynamicSummary = Routes12Summarize(observations);
            Check(dynamicSummary.Samples >= 5, route + ": observer captured dynamic physical-pixel/HRGN samples");
            Check(dynamicSummary.PixelTravel >= 24 && dynamicSummary.InputTravel >= 24,
                route + ": both physical pixels and finite input region moved materially during UI stall");

            Thread.Sleep(520);
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
            applied = [targetRect];
            Check(pair.TrySetInputRegions(applied), route + ": exact terminal region can be republished after autonomous motion");
            var terminalPixels = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(terminalPixels.Count >= 40 && Math.Abs(terminalPixels.Left - targetRect.Left) <= 4,
                $"{route}: terminal DComp pixels reach the intended endpoint ({terminalPixels})");

            // Re-use the production finite-input owner with two static members and a real hole.
            var second = new DeviceScreenRect(targetRect.Left + 96, targetRect.Top, targetRect.Right + 96, targetRect.Bottom);
            applied = [targetRect, second];
            Check(pair.TrySetInputRegions(applied), route + ": two-member finite input region published");
            var y = targetRect.Top + 24;
            var firstPoint = new DeviceScreenPoint(targetRect.Left + 12, y);
            var holePoint = new DeviceScreenPoint((targetRect.Right + second.Left) / 2.0, y);
            Check(NativeInputRegionContains(pair.InputHandle, firstPoint), route + ": first static member is interactive");
            Check(!NativeInputRegionContains(pair.InputHandle, holePoint), route + ": transparent inter-member gap stays a true HRGN hole");

            var lowerBeforeHole = lower.Snapshot;
            NativeInputMove(holePoint);
            NativeInputClick();
            NativeInputUntil(() => lower.Snapshot.Click == lowerBeforeHole.Click + 1 && lower.Snapshot.Up == lowerBeforeHole.Up + 1,
                route + ": real OS click through the HRGN hole reaches the lower process");
            var pressesBeforeMember = presses;
            NativeInputMove(firstPoint);
            NativeInputClick();
            NativeInputUntil(() => presses == pressesBeforeMember + 1,
                route + ": member press synchronously bridges to the responsive UI handoff");
            NativeInputPumpFor(60);
            Check(lower.Snapshot.Click == lowerBeforeHole.Click + 1 && lower.Snapshot.Up == lowerBeforeHole.Up + 1,
                route + ": owned member press does not leak to the lower process");

            // When UI is unavailable, the input owner must remain physical authority but must not
            // late-replay a business press after the handoff timeout.
            var pressesBeforeStalledClick = presses;
            var lowerBeforeStalledClick = lower.Snapshot;
            var stalledClick = Task.Run(() =>
            {
                Thread.Sleep(70);
                NativeInputMove(firstPoint);
                NativeInputClick();
            });
            Thread.Sleep(230);
            stalledClick.Wait(TimeSpan.FromSeconds(2));
            NativeInputPumpFor(80);
            Check(presses == pressesBeforeStalledClick,
                route + ": UI-stalled press is swallowed instead of being replayed after recovery");
            Check(lower.Snapshot == lowerBeforeStalledClick,
                route + ": UI-stalled owned press also cannot leak through to the lower application");

            // Environment invalidation terminates native timing and clears stale interception before
            // the UI-side queue rollback runs.
            var restartTimestamp = Stopwatch.GetTimestamp();
            var restartTicket = new EdgeCapsuleQueueInputAnimationTicket(
                restartTimestamp,
                500,
                [new EdgeCapsuleQueueProxyMemberPlan("r3-env", targetFrame, targetFrame,
                    Routes12R3Frame(new DeviceScreenRect(targetRect.Left, targetRect.Top + 40, targetRect.Right, targetRect.Bottom + 40))) ]);
            Check(pair.TryStartInputAnimation(restartTicket) && pair.IsInputAnimationActive,
                route + ": second native ticket starts before environment invalidation");
            Check(Routes12R3PostMessage(pair.InputHandle, 0x007E, IntPtr.Zero, IntPtr.Zero),
                route + ": WM_DISPLAYCHANGE posted to dedicated input owner");
            NativeInputPumpFor(100);
            Check(!pair.IsInputAnimationActive && environmentChanges >= 1,
                route + ": display generation change stops the old native ticket and notifies queue owner");
            Check(!NativeInputRegionContains(pair.InputHandle, firstPoint),
                route + ": display invalidation clears stale native interception fail-closed");

            Check(pair.Show(bounds, true) && pair.TrySetInputRegions([targetRect]),
                route + ": queue input/output pair can recover after environment invalidation");
            pair.Hide();
            Check(!IsProxyCheckWindowVisible(pair.Handle) && !IsProxyCheckWindowVisible(pair.InputHandle),
                route + ": hide retires both visual output and dedicated input owner visibility");

            Console.WriteLine(
                $"PASS {route} outputThread={NativeInputGetCurrentThreadId()} inputThread={pair.InputOwnerThreadId} " +
                $"{dynamicSummary} regionUpdates={pair.InputRegionUpdateCount} " +
                $"environmentChanges={environmentChanges} holeWidth={second.Left - targetRect.Right}px " +
                "uiStallPress=DROP_WITHOUT_LEAK productionWindowIntegration=TRUE " +
                "fullQueueCaptureHandoff=UNTESTED mixedDpi=UNTESTED physicalHighRefresh=UNTESTED");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        [new WindowNative.WindowCloakChange(new WindowInteropHelper(source).Handle, false, true)]);
                if (compositionTarget != null && device != null)
                {
                    compositionTarget.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            finally
            {
                pair?.Dispose();
                animation?.Dispose();
                visual?.Dispose();
                surface?.Dispose();
                compositionTarget?.Dispose();
                device?.Dispose();
                source.Close();
                NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
            }
        }
    }

    private static EdgeCapsulePresentationFrame Routes12R3Frame(DeviceScreenRect bounds) =>
        new(
            true,
            EdgeCapsuleSurfaceKind.DockedResting,
            bounds,
            bounds,
            bounds,
            EdgeCapsuleEdge.Left,
            bounds.Width,
            bounds.Left,
            1,
            1,
            0,
            1,
            1,
            true,
            true,
            true,
            true);

    private static List<Routes12R3Observation> Routes12R3ObserveVertical(
        IntPtr inputHandle,
        DeviceScreenRect bounds,
        int x,
        int milliseconds)
    {
        var observations = new List<Routes12R3Observation>();
        var end = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * milliseconds / 1000.0);
        while (Stopwatch.GetTimestamp() < end)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var pixels = Routes12R3CaptureRedVertical(bounds, x);
            var input = Routes12R3CaptureInputVertical(inputHandle, bounds, x);
            if (!pixels.IsEmpty && !input.IsEmpty)
                observations.Add(new Routes12R3Observation(timestamp, pixels, input));
            Thread.Sleep(6);
        }
        return observations;
    }

    private readonly record struct Routes12R3Summary(
        int Samples,
        int PixelTravel,
        int InputTravel,
        double MedianAbsSkew,
        double P95AbsSkew,
        int MaxAbsSkew,
        int VisualAhead,
        int InputAhead)
    {
        public override string ToString() =>
            $"samples={Samples} pixelTravel={PixelTravel}px inputTravel={InputTravel}px " +
            $"absSkewPx[p50={MedianAbsSkew:F1},p95={P95AbsSkew:F1},max={MaxAbsSkew}] " +
            $"visualAhead={VisualAhead} inputAhead={InputAhead}";
    }

    private static Routes12R3Summary Routes12R3Summarize(IReadOnlyList<Routes12R3Observation> observations)
    {
        if (observations.Count == 0) return default;
        var deltas = observations.Select(item => item.Input.Top - item.Pixels.Top).ToArray();
        var abs = deltas.Select(Math.Abs).OrderBy(value => value).ToArray();
        return new Routes12R3Summary(
            observations.Count,
            observations.Max(item => item.Pixels.Top) - observations.Min(item => item.Pixels.Top),
            observations.Max(item => item.Input.Top) - observations.Min(item => item.Input.Top),
            Routes12Percentile(abs.Select(value => (double)value).ToArray(), 0.50),
            Routes12Percentile(abs.Select(value => (double)value).ToArray(), 0.95),
            abs[^1],
            deltas.Count(value => value < -1),
            deltas.Count(value => value > 1));
    }

    private static Routes12R3Span Routes12R3CaptureInputVertical(IntPtr inputHandle, DeviceScreenRect bounds, int x)
    {
        var region = NativeInputCreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (NativeInputGetWindowRgn(inputHandle, region) <= 0) return default;
            var top = int.MaxValue;
            var bottom = int.MinValue;
            var count = 0;
            var localX = x - bounds.Left;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                if (!NativeInputPtInRegion(region, localX, y - bounds.Top)) continue;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
                count++;
            }
            return count == 0 ? default : new Routes12R3Span(top, bottom, count);
        }
        finally
        {
            NativeInputDeleteObject(region);
        }
    }

    private static Routes12R3Span Routes12R3CaptureRedVertical(DeviceScreenRect bounds, int x)
    {
        var dc = Routes12R3GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var top = int.MaxValue;
            var bottom = int.MinValue;
            var count = 0;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            {
                var color = Routes12R3GetPixel(dc, x, y);
                if (color == 0xFFFFFFFF) continue;
                var red = (byte)(color & 0xFF);
                var green = (byte)((color >> 8) & 0xFF);
                var blue = (byte)((color >> 16) & 0xFF);
                if (red < 180 || green > 90 || blue > 90) continue;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
                count++;
            }
            return count == 0 ? default : new Routes12R3Span(top, bottom, count);
        }
        finally
        {
            _ = Routes12R3ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Routes12R3PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    private static extern IntPtr Routes12R3GetDC(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int Routes12R3ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint Routes12R3GetPixel(IntPtr dc, int x, int y);
}