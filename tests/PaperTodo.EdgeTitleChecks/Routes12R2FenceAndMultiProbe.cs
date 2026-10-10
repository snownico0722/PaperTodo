using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static class Routes12R2Entry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunRoutes12R2(args);
}

internal static partial class Program
{
    internal static int RunRoutes12R2(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit)) return childExit;
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Routes12PolicyChecks();
            ProxyH8InputTimingChecks();
            Routes12Route1MultiRegionCase();
            Routes12Route2VisualFenceGapCase();
            Routes12Route2VisualFenceDynamicCase();
            Console.WriteLine($"ROUTES12 R2: {assertions} assertions passed. Route1 multi-region/supersede and Route2 visual-fence ordering were exercised; production integration, capture/handoff, mixed-DPI and physical high-refresh acceptance remain open.");
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

    private static void Routes12Route1MultiRegionCase()
    {
        const string route = "route1-native-owner-multi";
        Check(NativeInputGetCursorPos(out var cursor), route + ": interactive desktop available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 180,
            monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 500,
            monitor.WorkArea.Top + 300);
        var first = new DeviceScreenRect(bounds.Left + 16, bounds.Top + 18, bounds.Left + 64, bounds.Top + 66);
        var second = new DeviceScreenRect(bounds.Left + 112, bounds.Top + 18, bounds.Left + 160, bounds.Top + 66);

        using var lower = new NativeInputRemoteTarget(bounds, separateProcess: true);
        NativeInputUntil(() => lower.Handle != IntPtr.Zero, route + ": independent lower process publishes its HWND");
        var baselinePoint = new DeviceScreenPoint(bounds.Left + 250, bounds.Top + 42);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => lower.Snapshot.Click == 1 && lower.Snapshot.Up == 1,
            route + ": lower process receives baseline complete gesture");
        Check(NativeInputSetWindowPos(lower.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            route + ": lower target moves below the topmost input owner");

        Routes12NativeOwner? owner = null;
        try
        {
            owner = new Routes12NativeOwner(bounds, first);
            Check(NativeInputGetWindowThreadProcessId(owner.InputHandle, out _) != NativeInputGetCurrentThreadId(),
                route + ": input HWND is owned by the independent native thread");

            var ticket = new Routes12Ticket(
                100,
                Stopwatch.GetTimestamp(),
                (long)(Stopwatch.Frequency * 0.70),
                [first, second],
                72);
            Check(owner.Publish(ticket), route + ": multi-member ticket accepted");
            var updatesBefore = owner.Updates;
            Thread.Sleep(220);
            Check(owner.Updates >= updatesBefore + 4,
                route + ": independent owner advances a multi-member HRGN without the WPF dispatcher");
            var frozen = owner.Freeze(101);
            Check(frozen.Length == 2, route + ": freeze returns both member rectangles");
            Check(frozen[0].Width == first.Width && frozen[1].Width == second.Width,
                route + ": translation preserves member widths");
            Check(frozen[1].Left - frozen[0].Right == second.Left - first.Right,
                route + ": translation preserves the transparent hole between members");

            var y = frozen[0].Top + frozen[0].Height / 2;
            var member1 = new DeviceScreenPoint(frozen[0].Left + 12, y);
            var member2 = new DeviceScreenPoint(frozen[1].Left + 12, y);
            var hole = new DeviceScreenPoint((frozen[0].Right + frozen[1].Left) / 2.0, y);
            Check(NativeInputRegionContains(owner.InputHandle, member1), route + ": first member remains interactive");
            Check(NativeInputRegionContains(owner.InputHandle, member2), route + ": second member remains interactive");
            Check(!NativeInputRegionContains(owner.InputHandle, hole), route + ": gap stays a real native HRGN hole");

            var pressesBeforeHole = owner.Presses;
            NativeInputMove(hole);
            NativeInputClick();
            NativeInputUntil(() => lower.Snapshot.Click == 2 && lower.Snapshot.Up == 2,
                route + ": click through the transparent hole reaches the independent lower process");
            Check(owner.Presses == pressesBeforeHole,
                route + ": transparent hole does not produce a proxy press");

            var pressesBeforeMember = owner.Presses;
            NativeInputMove(member1);
            NativeInputClick();
            NativeInputUntil(() => owner.Presses == pressesBeforeMember + 1,
                route + ": click in a member is owned by the native input window");
            NativeInputPumpFor(60);
            Check(lower.Snapshot.Click == 2 && lower.Snapshot.Up == 2,
                route + ": member click does not leak to the lower process");

            var ticketA = new Routes12Ticket(
                110,
                Stopwatch.GetTimestamp(),
                (long)(Stopwatch.Frequency * 0.70),
                frozen,
                80);
            Check(owner.Publish(ticketA), route + ": long-running epoch A accepted");
            Thread.Sleep(140);
            var handoffTimestamp = Stopwatch.GetTimestamp();
            var bStart = ticketA.Sample(handoffTimestamp);
            var ticketB = new Routes12Ticket(
                111,
                handoffTimestamp,
                (long)(Stopwatch.Frequency * 0.18),
                bStart,
                -18);
            Check(owner.Publish(ticketB), route + ": newer epoch B supersedes A while A is live");
            Check(!owner.Publish(ticketA), route + ": superseded epoch A cannot be republished");
            Thread.Sleep(320);
            var stoppedUpdates = owner.Updates;
            Thread.Sleep(50);
            Check(owner.Updates == stoppedUpdates,
                route + ": completed superseding ticket stops its timer instead of reviving A");
            var terminal = owner.Freeze(112);
            var expectedB = ticketB.Sample(ticketB.Start + ticketB.Duration);
            var oldAEndpoint = ticketA.Sample(ticketA.Start + ticketA.Duration);
            Check(terminal.Length == 2 && Math.Abs(terminal[0].Left - expectedB[0].Left) <= 2,
                route + ": live supersede terminates at epoch B's endpoint");
            Check(Math.Abs(terminal[0].Left - oldAEndpoint[0].Left) >= 20,
                route + ": old epoch A cannot resume after B completes");
            Check(terminal[1].Left - terminal[0].Right == second.Left - first.Right,
                route + ": supersede preserves the multi-member hole geometry");

            Console.WriteLine(
                $"PASS {route} firstFrozenX={frozen[0].Left} secondFrozenX={frozen[1].Left} " +
                $"holeWidth={frozen[1].Left - frozen[0].Right}px presses={owner.Presses} updates={owner.Updates} " +
                $"supersedeTerminalX={terminal[0].Left} oldAEndpointX={oldAEndpoint[0].Left} " +
                "productionIntegration=UNTESTED captureHandoff=UNTESTED environmentGeneration=UNTESTED");
        }
        finally
        {
            owner?.Dispose();
            NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
        }
    }

    private static void Routes12Route2VisualFenceGapCase()
    {
        const string route = "route2-visual-fence-gap";
        Check(NativeInputGetCursorPos(out var cursor), route + ": interactive desktop available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 180,
            monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 480,
            monitor.WorkArea.Top + 300);
        var initial = new DeviceScreenRect(bounds.Left + 16, bounds.Top + 18, bounds.Left + 64, bounds.Top + 66);
        var next = new DeviceScreenRect(initial.Left + 110, initial.Top, initial.Right + 110, initial.Bottom);
        var y = initial.Top + 24;

        using var lower = new NativeInputRemoteTarget(bounds, separateProcess: true);
        NativeInputUntil(() => lower.Handle != IntPtr.Zero, route + ": independent lower process ready");
        var baselinePoint = new DeviceScreenPoint(bounds.Right - 24, y);
        NativeInputMove(baselinePoint);
        NativeInputClick();
        NativeInputUntil(() => lower.Snapshot.Click == 1 && lower.Snapshot.Up == 1,
            route + ": lower process receives baseline gesture");
        Check(NativeInputSetWindowPos(lower.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            route + ": lower target leaves the topmost band");

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
        IDCompositionTarget? target = null;
        IDCompositionVisual2? visual = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        var presses = 0;
        var applied = new[] { initial };
        try
        {
            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), route + ": source positioned");
            source.UpdateLayout();
            NativeInputPumpFor(80);
            var sourceHandle = new WindowInteropHelper(source).Handle;

            pair = EdgeCapsuleQueueProxyWindow.TryCreate(
                bounds,
                true,
                p => applied.Any(r => EdgeCapsuleGeometry.Contains(r, p)),
                _ => presses++,
                static () => { },
                static () => { },
                static () => { });
            Check(pair != null && pair.TrySetInputRegions(applied) && pair.Show(bounds, true),
                route + ": UI-owned pair ready");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(pair!.Handle, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError();
            surface = live;
            visual.SetContent(live).CheckError();
            visual.SetOffsetX(initial.Left - bounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - bounds.Top).CheckError();
            target.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, route + ": source cloaked only after initial cover is physically flushed");
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());

            var initialPixels = Routes12CaptureRedSpan(bounds, y, out _);
            Check(initialPixels.Count >= 40 && NativeInputRegionContains(pair.InputHandle,
                    new DeviceScreenPoint(initial.Left + 12, y)),
                route + ": initial pixels and HRGN agree before the fence test");

            visual.SetOffsetX(next.Left - bounds.Left).CheckError();
            device.Commit().CheckError();
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());

            var nextPixels = Routes12CaptureRedSpan(bounds, y, out _);
            var newPoint = new DeviceScreenPoint(next.Left + 12, y);
            var oldPoint = new DeviceScreenPoint(initial.Left + 12, y);
            Check(nextPixels.Count >= 40 && Math.Abs(nextPixels.Left - next.Left) <= 3,
                $"{route}: DwmFlush confirms the visual has reached the new position ({nextPixels})");
            Check(Pr260H8IsRedPixel(newPoint) && !NativeInputRegionContains(pair.InputHandle, newPoint),
                route + ": visual-fenced new pixels are visible before the old HRGN is replaced");
            Check(!Pr260H8IsRedPixel(oldPoint) && NativeInputRegionContains(pair.InputHandle, oldPoint),
                route + ": old HRGN still blocks a now-empty location during the same fence window");

            NativeInputMove(newPoint);
            NativeInputClick();
            NativeInputUntil(() => lower.Snapshot.Click == 2 && lower.Snapshot.Up == 2,
                route + ": visible-but-not-owned click leaks to the lower process after visual fence");
            Check(presses == 0, route + ": visible new pixels do not reach the stale input HWND");

            NativeInputMove(oldPoint);
            NativeInputClick();
            NativeInputUntil(() => presses == 1,
                route + ": visually empty old region still intercepts a click before HRGN publication");
            NativeInputPumpFor(60);
            Check(lower.Snapshot.Click == 2 && lower.Snapshot.Up == 2,
                route + ": empty stale region blocks the lower process");

            applied = [next];
            Check(pair.TrySetInputRegions(applied), route + ": HRGN can be advanced only after the visual fence");
            Check(NativeInputRegionContains(pair.InputHandle, newPoint) && !NativeInputRegionContains(pair.InputHandle, oldPoint),
                route + ": post-fence HRGN eventually matches the visual endpoint");

            Console.WriteLine(
                $"PASS {route} initial={initialPixels} fenced={nextPixels} shift={next.Left - initial.Left}px " +
                "failure=VISUAL_AHEAD_VISIBLE_BUT_NOT_OWNED_AND_OLD_EMPTY_REGION_BLOCKED " +
                "atomicPublication=FALSE");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        [new WindowNative.WindowCloakChange(new WindowInteropHelper(source).Handle, false, true)]);
                if (target != null && device != null)
                {
                    target.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            finally
            {
                pair?.Dispose();
                visual?.Dispose();
                surface?.Dispose();
                target?.Dispose();
                device?.Dispose();
                source.Close();
                NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
            }
        }
    }

    private static void Routes12Route2VisualFenceDynamicCase()
    {
        const string route = "route2-commit-flush-region-dynamic";
        Check(NativeInputGetCursorPos(out var cursor), route + ": interactive desktop available");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 180,
            monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 430,
            monitor.WorkArea.Top + 280);
        var initial = new DeviceScreenRect(bounds.Left + 16, bounds.Top + 18, bounds.Left + 64, bounds.Top + 66);
        var sampleY = initial.Top + 24;
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
        IDCompositionTarget? target = null;
        IDCompositionVisual2? visual = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        var applied = new[] { initial };
        try
        {
            source.Show();
            WindowNative.ApplyNoActivateStyle(source);
            Check(WindowNative.TrySetWindowDeviceBounds(source, initial), route + ": source positioned");
            source.UpdateLayout();
            NativeInputPumpFor(80);
            var sourceHandle = new WindowInteropHelper(source).Handle;
            pair = EdgeCapsuleQueueProxyWindow.TryCreate(
                bounds,
                true,
                p => applied.Any(r => EdgeCapsuleGeometry.Contains(r, p)),
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            Check(pair != null && pair.TrySetInputRegions(applied) && pair.Show(bounds, true),
                route + ": UI-owned pair ready");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(pair!.Handle, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var live).CheckError();
            surface = live;
            visual.SetContent(live).CheckError();
            visual.SetOffsetX(initial.Left - bounds.Left).CheckError();
            visual.SetOffsetY(initial.Top - bounds.Top).CheckError();
            target.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                [new WindowNative.WindowCloakChange(sourceHandle, true, false)]) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, route + ": source cloaked behind the published cover");
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());

            var duration = (long)(Stopwatch.Frequency * 0.90);
            var ticket = new Routes12Ticket(200, Stopwatch.GetTimestamp(), duration, [initial], 120);
            var observer = Task.Run(() => Routes12ObserveDynamic(pair.InputHandle, bounds, sampleY, 820));
            var cadenceTicks = Math.Max(1L, Stopwatch.Frequency / 120);
            var nextTick = ticket.Start;
            var stepTimestamps = new List<long>();
            var stepCount = 0;
            var maxCommitUs = 0.0;
            var maxFlushMs = 0.0;
            var maxRegionUs = 0.0;
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                if (now - ticket.Start >= ticket.Duration) break;
                applied = ticket.Sample(now);
                visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
                var commitStart = Stopwatch.GetTimestamp();
                device.Commit().CheckError();
                maxCommitUs = Math.Max(maxCommitUs,
                    (Stopwatch.GetTimestamp() - commitStart) * 1_000_000.0 / Stopwatch.Frequency);
                var flushStart = Stopwatch.GetTimestamp();
                Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
                maxFlushMs = Math.Max(maxFlushMs,
                    (Stopwatch.GetTimestamp() - flushStart) * 1000.0 / Stopwatch.Frequency);
                var regionStart = Stopwatch.GetTimestamp();
                Check(pair.TrySetInputRegions(applied), route + ": visual-fenced sample publishes finite HRGN");
                maxRegionUs = Math.Max(maxRegionUs,
                    (Stopwatch.GetTimestamp() - regionStart) * 1_000_000.0 / Stopwatch.Frequency);
                stepTimestamps.Add(Stopwatch.GetTimestamp());
                stepCount++;
                nextTick += cadenceTicks;
                Routes12WaitUntil(nextTick);
            }

            applied = ticket.Sample(ticket.Start + ticket.Duration);
            visual.SetOffsetX(applied[0].Left - bounds.Left).CheckError();
            device.Commit().CheckError();
            Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
            Check(pair.TrySetInputRegions(applied), route + ": exact terminal visual-fenced sample published");
            var observations = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var summary = Routes12Summarize(observations);
            Check(stepCount >= 10, route + ": DwmFlush-gated loop still produces enough updates for timing evidence");
            Check(summary.Samples >= 5, route + ": observer captured simultaneous physical-pixel/HRGN samples");
            Check(summary.PixelTravel >= 24 && summary.InputTravel >= 24,
                route + ": both physical pixels and HRGN travel materially");

            var stepIntervals = stepTimestamps.Zip(stepTimestamps.Skip(1), (a, b) =>
                    (b - a) * 1000.0 / Stopwatch.Frequency)
                .OrderBy(value => value)
                .ToArray();
            var p50StepMs = Routes12Percentile(stepIntervals, 0.50);
            var p95StepMs = Routes12Percentile(stepIntervals, 0.95);

            Console.WriteLine(
                $"PASS {route} {summary} updates={stepCount} " +
                $"appStepMs[p50={p50StepMs:F2},p95={p95StepMs:F2}] " +
                $"maxCommitUs={maxCommitUs:F1} maxDwmFlushMs={maxFlushMs:F2} maxRegionUs={maxRegionUs:F1} " +
                "ordering=COMMIT_DWMFLUSH_REGION physicalRefreshAcceptance=UNTESTED productionIntegration=UNTESTED");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        [new WindowNative.WindowCloakChange(new WindowInteropHelper(source).Handle, false, true)]);
                if (target != null && device != null)
                {
                    target.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            finally
            {
                pair?.Dispose();
                visual?.Dispose();
                surface?.Dispose();
                target?.Dispose();
                device?.Dispose();
                source.Close();
                NativeInputMove(new DeviceScreenPoint(cursor.X, cursor.Y));
            }
        }
    }
}
