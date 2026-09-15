using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    // Opt-in only: this group moves the desktop cursor. Main must dispatch the child argument
    // before creating Application, and call this group only for --proxy-native-input.
    private const string NativeInputChildArgument = "--proxy-native-input-target";
    private static readonly UIntPtr NativeInputTag = new(0x5054494E);

    private static void ProxyNativeInputChecks()
    {
        Check(NativeInputGetCursorPos(out var original), "Native input checks require an interactive desktop");
        Check((NativeInputGetAsyncKeyState(1) & 0x8000) == 0 &&
            (NativeInputGetAsyncKeyState(2) & 0x8000) == 0,
            "Native input checks start with physical mouse buttons released");
        try
        {
            // Fail before any Dispatcher pump: the regressed WM_PAINT handler can otherwise
            // starve the timers that bound the real-input groups below.
            ProxyNativePaintLifecycleChecks();
            var failures = new List<Exception>();
            void RunGroup(string name, Action check)
            {
                try { check(); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"FAIL native-input group={name}: {error}");
                    failures.Add(new Exception(name, error));
                }
                finally
                {
                    try { NativeInputSend(0, 0, 0x0004); }
                    catch (Exception error) { failures.Add(new Exception(name + " release cleanup", error)); }
                }
            }
            RunGroup("settled-input-publication-failures", ProxyNativeSettledInputPublicationFailures);
            RunGroup("real-WPF-controls", ProxyNativeControlGestures);
            RunGroup("selective-real-WPF-controls", () => ProxyNativeControlGestures(selective: true));
            RunGroup("cross-thread-routing", () => ProxyNativeTransparentRouting(separateProcess: false));
            RunGroup("cross-process-routing", () => ProxyNativeTransparentRouting(separateProcess: true));
            RunGroup("ownership-and-damaged-pool", ProxyNativeOwnershipAndPool);
            if (failures.Count > 0) throw new AggregateException("Native proxy input checks failed", failures);
        }
        finally
        {
            NativeInputSend(0, 0, 0x0004); // Always release a test-owned left press after failure.
            NativeInputMove(new DeviceScreenPoint(original.X, original.Y));
        }
        Console.WriteLine("PASS proxy-native-WPF-hover-complete-gestures-and-cross-process-routing");
    }

    private static void ProxyNativePaintLifecycleChecks()
    {
        // Match the persistent spare host: a hidden layered/transparent output, an empty
        // input HWND, and a real DComp target. Never enter GetMessage/Dispatcher.PushFrame;
        // each paint is delivered synchronously once, so the old handler fails finitely.
        var bounds = new DeviceScreenRect(-32000, -32000, -31996, -31996);
        var paintCount = 0;
        using var output = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true,
            _ => false, _ => { }, () => { }, () => paintCount++, () => { }) ??
            throw new InvalidOperationException("Could not create native paint-check output");
        Check(!IsProxyCheckWindowVisible(output.Handle) && !IsProxyCheckWindowVisible(output.InputHandle),
            "The paint regression starts with both production proxy HWNDs hidden");
        Check(NativeInputRegionEmpty(output.InputHandle), "The hidden paint-check input has an empty native region");

        var iid = typeof(IDCompositionDesktopDevice).GUID;
        Marshal.ThrowExceptionForHR(NativeInputDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
        using var device = new IDCompositionDesktopDevice(pointer);
        device.CreateTargetForHwnd(output.Handle, topmost: true, out var target).CheckError();
        using (target)
        {
            device.CreateVisual(out var root).CheckError();
            using (root)
            {
                target.SetRoot(root).CheckError();
                try
                {
                    device.Commit().CheckError();
                    Marshal.ThrowExceptionForHR(NativeInputDwmFlush());
                    Check(NativeInputGetUpdateRect(output.Handle, out _, false),
                        "A real hidden layered DComp output has a pending initial native update region");
                    ConsumePaint("initial hidden output");

                    for (var cycle = 1; cycle <= 2; cycle++)
                    {
                        var shownBounds = new DeviceScreenRect(bounds.Left, bounds.Top,
                            bounds.Right + cycle * 8, bounds.Bottom + cycle * 4);
                        Check(output.Show(shownBounds, true) && IsProxyCheckWindowVisible(output.Handle),
                            $"Paint cycle {cycle}: the same output can be shown and resized after its hidden paint");
                        Check(NativeInputInvalidateRect(output.Handle, IntPtr.Zero, true) &&
                            NativeInputGetUpdateRect(output.Handle, out _, false),
                            $"Paint cycle {cycle}: explicit visible invalidation creates real paint work");
                        ConsumePaint($"visible reuse {cycle}");
                        output.Hide();
                        Check(!IsProxyCheckWindowVisible(output.Handle) && !IsProxyCheckWindowVisible(output.InputHandle),
                            $"Paint cycle {cycle}: returning the pair to the pool hides both HWNDs");
                        ConsumePaint($"hidden reuse {cycle}");
                        Check(NativeInputRegionEmpty(output.InputHandle),
                            $"Paint cycle {cycle}: paint completion does not restore hidden native input ownership");
                    }
                }
                finally
                {
                    // Detach before releasing the native target/output, including assertion
                    // failure with an unconsumed update region in the old implementation.
                    _ = target.SetRoot(null!);
                    _ = device.Commit();
                }
            }
        }
        Console.WriteLine("PASS proxy-native-hidden-DComp-paint-consumption-and-pool-reuse");

        void ConsumePaint(string phase)
        {
            var before = paintCount;
            _ = NativeInputSendMessage(output.Handle, 0x000F, IntPtr.Zero, IntPtr.Zero);
            Check(paintCount == before + 1, phase + ": exactly one explicit WM_PAINT reaches the production handler");
            Check(!NativeInputGetUpdateRect(output.Handle, out var remaining, false),
                $"{phase}: native paint completion clears the update region " +
                $"(remaining={remaining.Left},{remaining.Top},{remaining.Right},{remaining.Bottom})");
            Check(!NativeInputGetUpdateRect(output.InputHandle, out _, false),
                phase + ": the separate empty input HWND has no pending paint work");
        }
    }

    private static void ProxyNativeSettledInputPublicationFailures()
    {
        using var fixture = new NativeInputHostFixture();
        fixture.CheckSettledInputPublicationFailures();
        Console.WriteLine("PASS proxy-native-settled-input-publication-and-safe-rollback-order");
    }

    private static void ProxyNativeControlGestures() => ProxyNativeControlGestures(selective: false);

    private static void ProxyNativeControlGestures(bool selective)
    {
        using var fixture = new NativeInputHostFixture(withPeer: selective);
        foreach (var control in new ButtonBase[] { fixture.Button, fixture.CheckBox })
        {
            var counts = new NativeInputCounts();
            void WaitForControl(Func<bool> condition, string milestone)
            {
                NativeInputUntil(condition, control.GetType().Name + ": " + milestone,
                    () => NativeInputControlStatus(control, counts, fixture));
                Console.WriteLine($"PASS native-control {control.GetType().Name}: {milestone} " +
                    $"down={counts.Down} up={counts.Up} clicks={counts.Click}");
            }
            control.AddHandler(Mouse.PreviewMouseDownEvent,
                new MouseButtonEventHandler((_, e) => { if (e.ChangedButton == MouseButton.Left) counts.Down++; }), true);
            control.AddHandler(Mouse.PreviewMouseUpEvent,
                new MouseButtonEventHandler((_, e) => { if (e.ChangedButton == MouseButton.Left) counts.Up++; }), true);
            control.Click += (_, _) => counts.Click++;
            var toggles = 0;
            if (control is CheckBox checkBox)
            {
                checkBox.Checked += (_, _) => toggles++;
                checkBox.Unchecked += (_, _) => toggles++;
            }

            fixture.RetainAwayFromControl();
            fixture.ObserveSettledPointerEntry(control);
            WaitForControl(() => fixture.Released && control.IsMouseOver,
                "Entering a retained preview yields its real source and establishes WPF IsMouseOver");
            fixture.ThrowIfFailed();
            Check(fixture.ReleaseCount == 1 && fixture.ProxyPresses == 0,
                "Hover releases authority before a button press; no synthetic DOWN supplies this gesture");
            Check(!NativeInputIsCloaked(fixture.Host.Handle), "Hover restores the source HWND from DWM cloak");
            if (selective) fixture.AssertPeerRetained(control);

            NativeInputSend(0, 0, 0x0002);
            WaitForControl(() => control.IsPressed && control.IsMouseCaptured && counts.Down == 1,
                "Real OS DOWN sets the WPF control's pressed state");
            Check(counts.Click == 0 && toggles == 0, "DOWN alone is not a click or checkbox toggle");
            NativeInputSend(0, 0, 0x0004);
            WaitForControl(() => !control.IsPressed && counts.Up == 1 && counts.Click == 1,
                "Real OS UP completes exactly one WPF click");
            NativeInputPumpFor(60);
            Check(counts.Down == 1 && counts.Up == 1 && counts.Click == 1 &&
                (control is not CheckBox || toggles == 1),
                "A retained-source gesture does not replay a press or double-toggle a checkbox");

            // A newly acquired generation must also allow normal capture cancellation.
            fixture.RetainAwayFromControl();
            fixture.ObserveSettledPointerEntry(control);
            WaitForControl(() => fixture.Released && control.IsMouseOver,
                "A later retained generation restores hover on the same live control");
            if (selective) fixture.AssertPeerRetained(control);
            NativeInputSend(0, 0, 0x0002);
            WaitForControl(() => control.IsPressed && control.IsMouseCaptured && counts.Down == 2,
                "The next real press acquires the control's native WPF capture");
            NativeInputMove(fixture.Outside);
            // ButtonBase capture deliberately keeps IsMouseOver true. Its UpdateIsPressed uses
            // Mouse.GetPosition(this) instead: dotnet/wpf v10.0.0 ButtonBase.cs, lines 96-144.
            // Verify both actual input position and retained capture rather than accepting capture
            // loss or an artificial UP as evidence that moving out cancelled the pressed state.
            WaitForControl(() => !control.IsPressed && control.IsMouseCaptured &&
                ReferenceEquals(Mouse.Captured, control) && Mouse.LeftButton == MouseButtonState.Pressed &&
                NativeInputPositionOutside(control) && NativeInputCursorNear(fixture.Outside),
                "Held movement outside clears IsPressed while retaining real WPF capture");
            NativeInputSend(0, 0, 0x0004);
            WaitForControl(() => counts.Up == 2 && Mouse.Captured == null && !control.IsPressed &&
                !control.IsMouseOver && Mouse.LeftButton == MouseButtonState.Released,
                "Outside UP releases the actual WPF capture");
            Check(counts.Click == 1 && (control is not CheckBox || toggles == 1),
                "Move-out cancellation performs no action and changes no checkbox state");

            NativeInputMove(NativeInputCenter(control));
            WaitForControl(() => control.IsMouseOver, "Re-entering after cancellation restores native hover");
            NativeInputClick();
            WaitForControl(() => counts.Click == 2 && counts.Up == 3,
                "The control remains usable after cancellation");
            NativeInputPumpFor(60);
            Check(counts.Down == 3 && counts.Click == 2 &&
                (control is not CheckBox || toggles == 2),
                "Three complete gestures produce two actions and one cancelled gesture");
            if (selective)
            {
                fixture.AssertPeerRetained(control);
                fixture.CompleteRemainingPeer();
                Check(counts.Down == 3 && counts.Up == 3 && counts.Click == 2,
                    "Completing the remaining proxy does not synthesize input on the real control");
            }
        }
        if (selective) Console.WriteLine("PASS selective-proxy-native-hover-full-gestures-peer-retention-and-final-release");
    }

    private static void ProxyNativeTransparentRouting(bool separateProcess)
    {
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Native routing test monitor");
        var work = monitor.WorkArea;
        var bounds = new DeviceScreenRect(work.Left + 80, work.Top + 80, work.Left + 300, work.Top + 220);
        using var target = new NativeInputRemoteTarget(bounds, separateProcess);
        NativeInputUntil(() => target.Handle != IntPtr.Zero, "Independent native target publishes its HWND");
        NativeInputGetWindowThreadProcessId(target.Handle, out var processId);
        Check(separateProcess ? processId != Environment.ProcessId : processId == Environment.ProcessId,
            "The target process matches the requested native-routing boundary");
        Check(NativeInputGetWindowThreadProcessId(target.Handle, out _) != NativeInputGetCurrentThreadId(),
            "The lower HWND has a different native input thread");
        var point = new DeviceScreenPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        NativeInputMove(point);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 1 && target.Snapshot.Up == 1,
            "The separate target receives a baseline complete OS gesture");
        Check(target.Snapshot.TaggedDown == 1 && target.Snapshot.TaggedUp == 1,
            "The target observes this test's SendInput tag on real native button messages");

        var proxyPresses = 0;
        var taggedProxyPresses = 0;
        var strips = new[]
        {
            new DeviceScreenRect(bounds.Left, bounds.Top, bounds.Left + 70, bounds.Bottom),
            new DeviceScreenRect(bounds.Right - 70, bounds.Top, bounds.Right, bounds.Bottom)
        };
        using var output = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true,
            candidate => strips.Any(rect => EdgeCapsuleGeometry.Contains(rect, candidate)),
            _ => { proxyPresses++; if (NativeInputGetMessageExtraInfo() == (IntPtr)(long)NativeInputTag.ToUInt64()) taggedProxyPresses++; },
            () => { }, () => { }, () => { });
        Check(output != null && output.TrySetInputRegions(strips) && output.Show(bounds, true),
            "Show production display and input HWNDs with an actual union-shaped hit region");
        Check(IsProxyCheckWindowVisible(output!.Handle), "The transparent-area test keeps the proxy HWND published");
        var inputHandle = output.InputHandle;
        Check(inputHandle != IntPtr.Zero && inputHandle != output.Handle,
            "Display and native input use distinct production HWNDs");
        NativeInputMove(point with { X = point.X + 1 });
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 2 && target.Snapshot.Up == 2,
            "A transparent output area routes OS DOWN and UP to the independent lower target");
        NativeInputPumpFor(60);
        var result = target.Snapshot;
        Check(result.Down == 2 && result.Up == 2 && result.Click == 2 &&
            result.TaggedDown == 2 && result.TaggedUp == 2 && proxyPresses == 0,
            "Transparent routing completes exactly once without forwarding messages through the proxy");
        Check(IsProxyCheckWindowVisible(output.Handle), "Passing a blank-area click does not retire the output HWND");

        var inside = new DeviceScreenPoint(bounds.Left + 30, bounds.Top + bounds.Height / 2);
        NativeInputMove(inside);
        NativeInputClick();
        NativeInputUntil(() => proxyPresses == 1, "OS input inside the actual region reaches the production input HWND");
        NativeInputPumpFor(60);
        Check(taggedProxyPresses == 1 && target.Snapshot == result,
            "An occupied region receives the tagged native press without leaking DOWN or UP to another thread");

        Check(output.TrySetInputRegions(Array.Empty<DeviceScreenRect>()), "An empty current shape removes all native input ownership");
        NativeInputMove(inside with { X = inside.X + 1 });
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 3, "An empty hit region routes a whole OS gesture through the former occupied area");
        Check(target.Snapshot.Down == 3 && target.Snapshot.Up == 3 && proxyPresses == 1,
            "Empty-region routing neither duplicates input nor preserves stale blockers");

        Check(output.TrySetInputRegions(new[] { strips[1] }), "Replace the hit region with the remaining visible member");
        NativeInputMove(inside with { X = inside.X + 2 });
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 4, "Removing one member restores OS input through its old region");
        output.Hide();
        Check(!IsProxyCheckWindowVisible(output.Handle) && !IsProxyCheckWindowVisible(inputHandle),
            "Hiding a pooled proxy hides both native windows");
        Check(output.TrySetInputRegions(strips) && output.Show(bounds, true) && output.InputHandle == inputHandle,
            "Reusing the proxy republishes current regions on the same input HWND");
        NativeInputMove(point);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 5, "A reused proxy still routes the union's internal hole through Windows");
        NativeInputPumpFor(60);
        Check(target.Snapshot == new NativeInputSnapshot(5, 5, 5, 5, 5) && proxyPresses == 1,
            "Thread/process routing preserves all complete gestures across region replacement and reuse");

        var movedBounds = new DeviceScreenRect(bounds.Left + 20, bounds.Top + 10, bounds.Right + 40, bounds.Bottom + 20);
        Check(output.Show(movedBounds, true), "Reposition and resize the pooled native output");
        NativeInputMove(point with { X = point.X + 1 });
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 6, "A moved pooled output clears the old screen-space input region");
        var shiftedRegion = new DeviceScreenRect(bounds.Left + 20, bounds.Top + 30, bounds.Left + 60, bounds.Top + 110);
        Check(output.TrySetInputRegions(new[] { shiftedRegion }), "Publish a new absolute screen region after the output origin changed");
        NativeInputMove(inside with { X = inside.X + 3 });
        NativeInputClick();
        NativeInputUntil(() => proxyPresses == 2, "The new region uses the new HWND origin, not the previous local coordinates");
        NativeInputPumpFor(60);
        Check(target.Snapshot == new NativeInputSnapshot(6, 6, 6, 6, 6) && taggedProxyPresses == 2,
            "A moved input HWND receives the new occupied area without leaking a gesture");
        NativeInputMove(point);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 7, "The new region's outside area still passes complete OS gestures");
        Check(target.Snapshot == new NativeInputSnapshot(7, 7, 7, 7, 7) && proxyPresses == 2,
            "Resizing preserves exact native routing and does not reuse stale blockers");
        Console.WriteLine($"PASS proxy-transparent-OS-routing boundary={(separateProcess ? "process" : "thread")}");
    }

    private static void ProxyNativeOwnershipLifecycle()
    {
        using var host = NewProxyCheckHost("native-input-lifecycle");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Input lifecycle monitor");
        var frame = ProxyCheckFrame(monitor, EdgeCapsuleEdge.Left, 80, preview: true);
        Check(host.Apply(frame), "Apply the live source for native input ownership checks");
        var paper = NewProxyCheckPaperWindow(host);
        using var fixture = new ProxyLifecycleFixture(60000, frame.HostBounds);
        var member = new EdgeCapsuleQueueProxyMemberPlan("input-owner", frame, frame, frame);
        fixture.Set("_plan", new EdgeCapsuleQueueProxyPlan("input-owner", frame.HostBounds, frame.Edge,
            frame.WallDeviceX, frame.DpiScaleX, frame.DpiScaleY, 60000, false, new[] { member }));
        fixture.Set("_members", new[] { new EdgeCapsuleQueueCompositionProxyMember(paper, member, host.Handle) });
        var output = fixture.Get<EdgeCapsuleQueueProxyWindow>("_window");
        var point = new DeviceScreenPoint(frame.InteractiveBounds.Left + frame.InteractiveBounds.Width / 2,
            frame.InteractiveBounds.Top + frame.InteractiveBounds.Height / 2);
        fixture.Proxy.RetainForQueueBrowsing();
        Check(NativeInputRegionContains(output.InputHandle, point), "Retention publishes the source's actual native region");
        Check(fixture.Proxy.TryReserveForSuccessor() && NativeInputRegionContains(output.InputHandle, point),
            "A successor reservation preserves the visible cover's native input shield");
        Check(!fixture.Proxy.TryResolveInputTarget(point, out _, out _),
            "Successor hold rejects business input while its source remains covered");
        fixture.Proxy.CompleteAfterFailedSuccessor(success: true);
        Check(NativeInputRegionContains(output.InputHandle, point),
            "A failed successor with remaining animation restores the current source region before resuming");

        bool? callbackSawShield = null;
        fixture.Set("_completed", (Action<EdgeCapsuleQueueCompositionProxy, bool, bool>)((_, _, _) =>
            callbackSawShield = NativeInputRegionContains(output.InputHandle, point)));
        fixture.Proxy.CompleteNow(success: true);
        Check(callbackSawShield == true, "Completion preserves the visible cover's shield during its handoff callback");
        Check(NativeInputRegionContains(output.InputHandle, point), "Pending completion cannot expose the application behind a visible card");
        fixture.Proxy.ScheduleCompletionRetry(success: true);
        Check(fixture.CompletionTimer.IsEnabled && NativeInputRegionContains(output.InputHandle, point),
            "A scheduled completion retry keeps shielding the still-visible card");
        Check(!fixture.Proxy.TryResolveInputTarget(point, out _, out _),
            "Pending retry closes routing even after its finishing callback returns");
        fixture.CompletionTimer.Stop();
        fixture.Proxy.RetainForQueueBrowsing();
        Check(NativeInputRegionContains(output.InputHandle, point), "Verified retention restores current native input after retry ownership ends");

        using var successor = new ProxyLifecycleFixture();
        var owner = fixture.Get<object>("_host");
        owner.GetType().GetField("<Current>k__BackingField", CapacityCheckFields)!.SetValue(owner, successor.Proxy);
        fixture.Set("_completed", (Action<EdgeCapsuleQueueCompositionProxy, bool, bool>)((_, _, _) => { }));
        fixture.Proxy.CompleteNow(success: true);
        Check(NativeInputRegionContains(output.InputHandle, point),
            "A retired predecessor cannot clear the new owner's shared native region");
        owner.GetType().GetField("<Current>k__BackingField", CapacityCheckFields)!.SetValue(owner, fixture.Proxy);
        fixture.Set("_coverLost", true);
        var refresh = typeof(EdgeCapsuleQueueCompositionProxy).GetMethod("RefreshNativeInputRegion", CapacityCheckFields)!;
        Check((bool)refresh.Invoke(fixture.Proxy, null)! && NativeInputRegionEmpty(output.InputHandle),
            "Loss of the actual visual cover removes its input shield");
        Console.WriteLine("PASS proxy-native-region-shield-business-ownership-and-cover-loss");
    }

    private static void ProxyNativeOwnershipAndPool()
    {
        using var fixture = new NativeInputHostFixture();
        var bounds = fixture.HostBounds;
        using var target = new NativeInputRemoteTarget(bounds, separateProcess: true);
        NativeInputUntil(() => target.Handle != IntPtr.Zero, "Ownership target starts on its independent process");
        var interactive = fixture.InteractiveBounds;
        var point = new DeviceScreenPoint(interactive.Left + interactive.Width / 2, interactive.Top + interactive.Height / 2);
        var hole = new DeviceScreenPoint((interactive.Right + bounds.Right) / 2, point.Y);
        Check(EdgeCapsuleGeometry.Contains(bounds, hole) && !EdgeCapsuleGeometry.Contains(interactive, hole),
            "The independent target spans both the actual card and a real unused source-capacity hole");
        NativeInputMove(point);
        NativeInputClick();
        NativeInputUntil(() => target.Snapshot.Click == 1, "Ownership target accepts the baseline whole gesture");
        // The peer starts topmost to establish its baseline over the user's desktop. From here
        // it represents an ordinary application behind the proxy; activating a topmost peer in
        // a hole would legitimately raise it above the cards and invalidate that test premise.
        Check(NativeInputSetWindowPos(target.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
            "Place the real external target in the ordinary non-topmost window band");

        fixture.RetainAwayFromControl();
        fixture.Proxy.RetainForQueueBrowsing();
        fixture.PausePointerSampling();
        using var probe = new NativeInputWindowProbe(fixture.Output.InputHandle);
        var expectedInputGestures = 0;
        var expectedTargetGestures = 1;
        void CheckShieldAndHole(string phase)
        {
            NativeInputMove(point);
            NativeInputClick();
            expectedInputGestures++;
            NativeInputUntil(() => probe.Counts.Down == expectedInputGestures && probe.Counts.Up == expectedInputGestures,
                phase + ": the actual input HWND receives a complete OS gesture over the visible card",
                () => $"input={probe.Counts.Down}/{probe.Counts.Up} expected={expectedInputGestures} " +
                    $"peer={target.Snapshot} business={fixture.ProxyPresses} region={NativeInputRegionContains(fixture.Output.InputHandle, point)} " +
                    $"cloaked={NativeInputIsCloaked(fixture.Host.Handle)}");
            NativeInputPumpFor(60);
            Check(probe.Counts.TaggedDown == expectedInputGestures && probe.Counts.TaggedUp == expectedInputGestures &&
                fixture.ProxyPresses == 0 && target.Snapshot == new NativeInputSnapshot(
                    expectedTargetGestures, expectedTargetGestures, expectedTargetGestures, expectedTargetGestures, expectedTargetGestures),
                phase + ": a covered card drops business input without clicking the external application");
            NativeInputMove(hole);
            NativeInputClick();
            expectedTargetGestures++;
            NativeInputUntil(() => target.Snapshot == new NativeInputSnapshot(
                    expectedTargetGestures, expectedTargetGestures, expectedTargetGestures, expectedTargetGestures, expectedTargetGestures),
                phase + ": the actual empty area still passes tagged DOWN and UP to the independent process");
            Check(probe.Counts.Down == expectedInputGestures && probe.Counts.Up == expectedInputGestures,
                phase + ": an empty-area gesture does not enter the input HWND");
            Console.WriteLine("PASS native-input shield and hole: " + phase + "; messages=" + string.Join(",", probe.Messages));
        }
        Check(fixture.Proxy.TryReserveForSuccessor(), "Reserve the real retained DComp source for a successor");
        CheckShieldAndHole("successor hold");
        Check(NativeInputIsCloaked(fixture.Host.Handle) && fixture.ProxyPresses == 0,
            "A held source stays cloaked while its visible card shields the application behind it");

        // Finish the held generation through its real failure recovery, then deliberately leave
        // the next source handoff pending. No fixture-side Hide/region replacement proves this path.
        fixture.SuspendCompletion = true;
        fixture.Proxy.CompleteAfterFailedSuccessor(success: true);
        fixture.Proxy.CompleteNow(success: true);
        CheckShieldAndHole("pending completion");
        fixture.Proxy.ScheduleCompletionRetry(success: true);
        // Freeze only the test's completion deadline while observing ownership; readiness remains
        // the production retry count + enabled timer, not a test-invented routing flag.
        fixture.CompletionTimer.Interval = TimeSpan.FromSeconds(60);
        CheckShieldAndHole("completion retry");

        fixture.SuspendCompletion = false;
        fixture.Proxy.CompleteNow(success: true, allowBrowseRetention: true);
        fixture.PausePointerSampling();
        NativeInputMove(point);
        NativeInputClick();
        NativeInputUntil(() => fixture.ProxyPresses == 1, "A verified retained owner restores its current native region");
        NativeInputPumpFor(60);
        Check(probe.Counts.Down == 4 && probe.Counts.Up == 4 && target.Snapshot == new NativeInputSnapshot(4, 4, 4, 4, 4),
            "Restored ownership does not leak either half of a gesture to the external process");

        fixture.Proxy.CompleteNow(success: true);
        var brokenOutput = fixture.Output;
        var brokenInput = brokenOutput.InputHandle;
        fixture.Proxy.Dispose();
        probe.Dispose();
        Check(NativeInputDestroyWindow(brokenInput) && brokenOutput.InputHandle == IntPtr.Zero &&
            WindowNative.IsWindowHandleAlive(brokenOutput.Handle),
            "Simulate loss of only the input partner of an idle pooled output");
        fixture.RetainAwayFromControl();
        Check(!ReferenceEquals(fixture.Output, brokenOutput) && brokenOutput.Handle == IntPtr.Zero &&
            WindowNative.IsWindowHandleAlive(fixture.Output.Handle) &&
            WindowNative.IsWindowHandleAlive(fixture.Output.InputHandle),
            "Actual DComp reacquisition retires a damaged idle pair and uses two valid native windows");
        Console.WriteLine("PASS proxy-native-ownership-retry-and-damaged-pool");
    }

    private sealed class NativeInputHostFixture : IDisposable
    {
        internal readonly EdgeCapsuleHost Host = NewProxyCheckHost("native-input-checks");
        internal readonly Button Button = new() { Content = "Native button", Width = 180, Height = 44, Margin = new Thickness(12) };
        internal readonly CheckBox CheckBox = new() { Content = "Native checkbox", Width = 180, Height = 44, Margin = new Thickness(12) };
        private readonly StackPanel _panel = new() { Background = Brushes.White };
        private readonly PaperWindow _paperWindow;
        private readonly EdgeCapsulePresenter _presenter = new();
        private readonly Dictionary<PaperWindow, EdgeCapsuleQueueCompositionProxy> _retained = new();
        private readonly EdgeCapsulePresentationFrame _compact;
        private readonly EdgeCapsulePresentationFrame _preview;
        private readonly EdgeCapsuleLayoutSnapshot _layout;
        private EdgeCapsuleQueueCompositionProxy? _proxy;
        private readonly NativeInputHostFixture? _peer;
        private EdgeCapsuleQueueCompositionProxy? _originalGeneration;
        private IntPtr _originalOutput;
        private IntPtr _originalInput;
        private Exception? _failure;
        internal DeviceScreenPoint Outside { get; }
        internal int ReleaseCount { get; private set; }
        internal int ProxyPresses { get; private set; }
        internal bool Released { get { ThrowIfFailed(); return ReleaseCount == 1; } }
        internal bool SuspendCompletion { get; set; }
        internal EdgeCapsuleQueueCompositionProxy Proxy => _proxy!;
        internal DeviceScreenRect InteractiveBounds => _preview.InteractiveBounds;
        internal DeviceScreenRect HostBounds => _preview.HostBounds;
        internal EdgeCapsuleQueueProxyWindow Output =>
            (EdgeCapsuleQueueProxyWindow)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_window", CapacityCheckFields)!.GetValue(_proxy)!;
        internal DispatcherTimer CompletionTimer =>
            (DispatcherTimer)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_completionTimer", CapacityCheckFields)!.GetValue(_proxy)!;
        internal void PausePointerSampling() =>
            ((DispatcherTimer)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_sampleTimer", CapacityCheckFields)!.GetValue(_proxy)!).Stop();

        internal NativeInputHostFixture(bool withPeer = false, double topDip = 80)
        {
            Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Real control test monitor");
            _compact = ProxyCheckFrame(monitor, EdgeCapsuleEdge.Left, topDip);
            _preview = ProxyCheckFrame(monitor, EdgeCapsuleEdge.Left, topDip, preview: true);
            _layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, topDip, 0,
                100, 28, 40, 300, 360, false, 1, null, 328, 360);
            Outside = new DeviceScreenPoint(monitor.WorkArea.Right - 16, monitor.WorkArea.Top + 16);
            _panel.Children.Add(Button);
            _panel.Children.Add(CheckBox);

            // Only the application/data adapter is isolated. HWND creation, preview tree, DComp
            // acquisition, cloak/reveal, pointer timer and native/WPF input are all production paths.
            _paperWindow = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
            typeof(DispatcherObject).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_paperWindow, Dispatcher.CurrentDispatcher);
            var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            var paper = new PaperData { Id = "native-input-check-" + topDip.ToString(CultureInfo.InvariantCulture),
                Title = "Native input", IsVisible = true, IsCollapsed = true };
            typeof(AppController).GetProperty(nameof(AppController.State))!.SetValue(controller,
                new AppState { ExperimentalEdgeCapsuleHoverPreview = true, Papers = new() { paper } });
            SetCapacityCheckField(controller, "_windows", new Dictionary<string, PaperWindow>());
            SetCapacityCheckField(controller, "_advancedTransparentCapsuleIds", new HashSet<string>());
            SetCapacityCheckField(controller, "_edgeCapsuleQueueCompositionProxyByWindow", _retained);
            SetCapacityCheckField(_paperWindow, "_paper", paper);
            SetCapacityCheckField(_paperWindow, "_controller", controller);
            SetCapacityCheckField(_paperWindow, "_edgeCapsule", _presenter);
            SetCapacityCheckField(_paperWindow, "_edgeCapsuleHost", Host);
            var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", CapacityCheckFields)!;
            lifecycle.SetValue(_paperWindow, Enum.Parse(lifecycle.FieldType, "Alive"));
            Check(_presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false)).Accepted,
                "Attach the source adapter to an eligible docked queue");
            Host.SetTopmost(true, new IntPtr(-1));
            // A compact peer above the preview stays independently visible without depending on
            // a large desktop. Both hosts use the same monitor, edge and bounded surface size.
            if (withPeer) _peer = new NativeInputHostFixture(topDip: 20);
        }

        private EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty) =>
            _presenter.Reconcile(dirty, () => _layout, () => null, frame => frame, Host.Apply);

        private void Settle(bool preview)
        {
            Check(_presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(preview)).Accepted,
                "The real source Presenter accepts its preview state");
            _presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Preview));
            _presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, Host.Dispatcher, Reconcile);
            // Host.Apply commits frame properties synchronously, while an unchanged native capacity
            // can still have pending WPF layout for its newly expanded VisualSurface. Use the same
            // real render/layout boundary as handoff before checking terminal native geometry.
            Check(Host.PrepareCompositionSourceForHandoff(), "Flush real WPF layout before verifying the source endpoint");
            var expected = preview ? _preview : _compact;
            var settled = _presenter.IsSettledForPreacquisition;
            var matches = Host.MatchesPresentation(expected);
            if (!settled || !matches)
            {
                Host.TryGetAppliedPresentation(out var applied);
                GetProxyCheckWindowRect(Host.Handle, out var native);
                var surface = (FrameworkElement)typeof(EdgeCapsuleHost)
                    .GetProperty("VisualSurface", CapacityCheckFields)!.GetValue(Host)!;
                Console.Error.WriteLine($"native-input settle preview={preview} settled={settled} matches={matches} " +
                    $"transition={_presenter.HasActiveTransition} dirty={GetCapacityCheckField<EdgeCapsuleDirty>(_presenter, "_dirty")} " +
                    $"scheduled={GetCapacityCheckField<bool>(_presenter, "_reconcileScheduled")} " +
                    $"deferrals={GetCapacityCheckField<int>(_presenter, "_visualTransactionDeferrals")} " +
                    $"native={native.Left},{native.Top},{native.Right},{native.Bottom} " +
                    $"surface={surface.ActualWidth}x{surface.ActualHeight}\n" +
                    $"expected={expected}\npresenter={_presenter.AppliedPresentation}\nhost={applied}");
            }
            Check(settled && matches,
                "The real Presenter and native source finish the requested endpoint");
        }

        internal void RetainAwayFromControl()
        {
            _proxy?.Dispose();
            _proxy = null;
            _retained.Clear();
            ReleaseCount = 0;
            ProxyPresses = 0;
            NativeInputMove(Outside);
            NativeInputPumpFor(30);
            Settle(preview: false);
            _peer?.Settle(preview: false);
            var sources = _peer == null ? new[] { this } : new[] { this, _peer };
            foreach (var source in sources) source._retained.Clear();
            var candidates = sources.Select(source => new EdgeCapsuleQueueProxyCandidate(
                source._paperWindow.EdgeCapsulePreviewPaperId, "native-input-check",
                source._compact, source._compact, source._compact,
                EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State),
                HostReady: true, Topmost: true, RetainedByCurrentProxy: false)).ToArray();
            var plan = EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention("native-input-check", candidates);
            Check(plan != null, "Create a real static acquisition plan with unchanged source identity");
            var members = sources.Select((source, index) => new EdgeCapsuleQueueCompositionProxyMember(
                source._paperWindow, plan!.Members[index], source.Host.Handle)).ToArray();
            _proxy = EdgeCapsuleQueueCompositionProxy.TryCreate(EdgeCapsuleQueueCompositionProxy.ReserveSessionOrdinal(),
                plan!, members, null, _ => sources.All(source => source.Host.MatchesPresentation(source._compact)), _ => true,
                _ => ProxyPresses++, () => { }, Publish,
                (_, predecessor) => RestoreMappings(predecessor), Complete);
            Check(_proxy != null && _proxy.TryStart(out _), "Acquire and publish an actual live DComp proxy");
            ThrowIfFailed();
            Check(_proxy!.IsRetainedForQueueBrowsing && NativeInputIsCloaked(Host.Handle),
                "The source really is retained and DWM-cloaked before pointer entry");
            _originalGeneration = _proxy;
            _originalOutput = Output.Handle;
            _originalInput = Output.InputHandle;
            if (_peer != null)
            {
                Check(_proxy.Members.Count == 2 && NativeInputIsCloaked(_peer.Host.Handle),
                    "Two real live WPF sources share the initially retained generation");
                _proxy.SettledInputRequested = ReleaseSettledInput;
            }
            // Keep setup work from generating unrelated controller hover work. The next stage
            // restores the production timer after preparing the already-settled pointer sample.
            var timer = (DispatcherTimer)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_sampleTimer", CapacityCheckFields)!.GetValue(_proxy)!;
            timer.Stop();
            // Applying a terminal compact frame deliberately detaches its former preview tree.
            // Stage the current tree after compact acquisition, exactly before opening it again.
            Check(Host.StagePreviewContent(_panel, 292, 352) && Host.OwnsPreviewContent(_panel),
                "Mount real interactive controls after compact-source acquisition");
            Settle(preview: true);
            Check(_proxy.HasCompatibleEndpoint(_paperWindow, _preview), "The later preview remains a compatible retained endpoint");
            CheckReleaseAdmission();
        }

        internal void CheckSettledInputPublicationFailures()
        {
            RetainAwayFromControl();
            PausePointerSampling();
            CompletionTimer.Stop();
            var helper = typeof(EdgeCapsuleQueueCompositionProxy).GetMethod("PublishSettledInputCover",
                BindingFlags.Static | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("The settled-input publication helper is unavailable");
            var device = (IDCompositionDesktopDevice)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_device", CapacityCheckFields)!.GetValue(_proxy)!;
            var target = (IDCompositionTarget)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_target", CapacityCheckFields)!.GetValue(_proxy)!;
            var originalRoot = (IDCompositionVisual)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_root", CapacityCheckFields)!.GetValue(_proxy)!;
            var reveal = new[] { new WindowNative.WindowCloakChange(Host.Handle,
                Cloaked: false, RollbackCloaked: true) };
            var scenarios = new[]
            {
                (Name: "publish returns false", Publish: false, PublishThrows: false, RollbackThrows: false,
                    Result: WindowNative.WindowCloakBatchResult.RolledBack, Cloaked: true),
                (Name: "publish throws", Publish: false, PublishThrows: true, RollbackThrows: false,
                    Result: WindowNative.WindowCloakBatchResult.RolledBack, Cloaked: true),
                (Name: "visual rollback throws", Publish: false, PublishThrows: false, RollbackThrows: true,
                    Result: WindowNative.WindowCloakBatchResult.RollbackFailed, Cloaked: false),
                (Name: "publication succeeds", Publish: true, PublishThrows: false, RollbackThrows: false,
                    Result: WindowNative.WindowCloakBatchResult.Success, Cloaked: false)
            };
            foreach (var scenario in scenarios)
            {
                Check(NativeInputIsCloaked(Host.Handle) && Host.MatchesPresentation(_preview),
                    scenario.Name + ": the real source starts cloaked at its verified retained endpoint");
                var publishCalls = 0;
                var rollbackCalls = 0;
                var publishSawVisibleSource = false;
                var rollbackSawVisibleSource = false;
                var rootRemoved = false;
                var rootRestored = false;
                bool PublishTestCover()
                {
                    publishCalls++;
                    publishSawVisibleSource = !NativeInputIsCloaked(Host.Handle);
                    // Exercise a real committed cover removal, not just a callback flag. This
                    // fixture has one source, which must already be visible underneath it.
                    target.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                    rootRemoved = true;
                    if (scenario.PublishThrows)
                        throw new InvalidOperationException("Injected settled-input publication failure");
                    return scenario.Publish;
                }
                void RollbackTestCover()
                {
                    rollbackCalls++;
                    rollbackSawVisibleSource = !NativeInputIsCloaked(Host.Handle);
                    if (scenario.RollbackThrows)
                        throw new InvalidOperationException("Injected settled-input cover restoration failure");
                    target.SetRoot(originalRoot).CheckError();
                    device.Commit().CheckError();
                    rootRestored = true;
                }
                try
                {
                    // No pumping or timer-based timeout: the helper completes a finite number
                    // of native transactions. Assertions stay outside callbacks because the
                    // production helper intentionally catches their exceptions.
                    var result = (WindowNative.WindowCloakBatchResult)helper.Invoke(null,
                        new object[] { reveal, (Func<bool>)PublishTestCover, (Action)RollbackTestCover })!;
                    Check(result == scenario.Result, scenario.Name + ": publication reports the expected native outcome");
                    Check(publishCalls == 1 && publishSawVisibleSource && rootRemoved,
                        scenario.Name + ": publication observes the revealed source before removing the real DComp root");
                    Check(rollbackCalls == (scenario.Publish ? 0 : 1),
                        scenario.Name + ": rollback runs exactly once on failure and never on success");
                    Check(scenario.Publish || rollbackSawVisibleSource,
                        scenario.Name + ": the outgoing source stays visible throughout the rollback callback");
                    Check(rootRestored == (!scenario.Publish && !scenario.RollbackThrows),
                        scenario.Name + ": only a successful rollback restores the actual compositor root");
                    Check(NativeInputIsCloaked(Host.Handle) == scenario.Cloaked,
                        scenario.Name + ": the source is re-cloaked only after its old cover was restored");
                    Check(Host.MatchesPresentation(_preview),
                        scenario.Name + ": native authority changes leave the real WPF endpoint unchanged");
                }
                finally
                {
                    // The helper is tested independently of controller ownership. Restore the
                    // retained fixture's actual cover before restoring its original cloak lease,
                    // including the intentionally failed rollback and successful release cases.
                    target.SetRoot(originalRoot).CheckError();
                    device.Commit().CheckError();
                    Check(WindowNative.TryFlushDesktopComposition(), "Fault-test cleanup confirms the original live cover");
                    Check(WindowNative.TrySetWindowCloakedBatchDetailed(new[]
                    {
                        new WindowNative.WindowCloakChange(Host.Handle, Cloaked: true, RollbackCloaked: false)
                    }) == WindowNative.WindowCloakBatchResult.Success,
                        "Fault-test cleanup restores the original retained source lease after its cover");
                }
            }
            ThrowIfFailed();
        }

        private void CheckReleaseAdmission()
        {
            var point = NativeInputCenter(CheckBox);
            Check(_proxy!.ShouldReleaseForPointerInput(point),
                "A settled real Presenter admits input on its latest preview, beyond the old compact shape");
            Check(!_proxy.ShouldReleaseForPointerInput(null) && !_proxy.ShouldReleaseForPointerInput(Outside),
                "Unavailable or outside input keeps the reusable proxy");
            using (_presenter.DeferReconcileToVisualTransaction())
                Check(!_proxy.ShouldReleaseForPointerInput(point), "A real transaction barrier blocks source-input handoff");
            _presenter.InvalidateBeforeNextRender(EdgeCapsuleDirty.Pointer, Host.Dispatcher, Reconcile);
            Check(!_proxy.ShouldReleaseForPointerInput(point), "Queued real Presenter input work blocks source handoff");
            _presenter.Flush(EdgeCapsuleDirty.None, Host.Dispatcher, Reconcile);
            Check(_proxy.ShouldReleaseForPointerInput(point), "The same pointer becomes eligible after the real reconcile settles");
            Check(Host.Apply(_preview with { IsHitTestVisible = false, InteractiveBounds = default }) &&
                !_proxy.ShouldReleaseForPointerInput(point), "Applied WPF input eligibility controls source release");
            Check(Host.Apply(_preview), "Restore the actual preview after its input eligibility check");
            typeof(EdgeCapsuleQueueCompositionProxy).GetField("_retainedAfterAnimation", CapacityCheckFields)!.SetValue(_proxy, false);
            Check(!_proxy.ShouldReleaseForPointerInput(point), "An active translation never yields by the settled-input route");
            typeof(EdgeCapsuleQueueCompositionProxy).GetField("_retainedAfterAnimation", CapacityCheckFields)!.SetValue(_proxy, true);
        }

        internal void ObserveSettledPointerEntry(FrameworkElement control)
        {
            // Model the follow-up tick after controller pointer reconciliation has already settled.
            // The preceding controller preview/queue operation is tested elsewhere; this fixture
            // must not duplicate that operation or replace it with a synthetic control event.
            NativeInputMove(NativeInputCenter(control));
            NativeInputPumpFor(30);
            Check(NativeInputGetCursorPos(out var cursor), "Observe the OS position after entering the retained preview");
            var point = new DeviceScreenPoint(cursor.X, cursor.Y);
            Check(_proxy!.ShouldReleaseForPointerInput(point) && NativeInputIsCloaked(Host.Handle),
                "The pointer is physically inside a settled, still-cloaked production source");
            typeof(EdgeCapsuleQueueCompositionProxy).GetField("_hasRetainedPointerSample", CapacityCheckFields)!.SetValue(_proxy, true);
            typeof(EdgeCapsuleQueueCompositionProxy).GetField("_lastRetainedPointer", CapacityCheckFields)!.SetValue(_proxy, point);
            typeof(EdgeCapsuleQueueCompositionProxy).GetField("_lastRetainedPointerFrames", CapacityCheckFields)!
                .SetValue(_proxy, _proxy.Members.Select(member =>
                {
                    Check(member.Window.TryGetEdgeCapsuleQueueProxyAppliedPresentation(out var frame),
                        "Seed the already processed physical pointer sample from each real source");
                    return frame;
                }).ToArray());
            var timer = (DispatcherTimer)typeof(EdgeCapsuleQueueCompositionProxy)
                .GetField("_sampleTimer", CapacityCheckFields)!.GetValue(_proxy)!;
            timer.Start();
        }

        private bool Publish(EdgeCapsuleQueueCompositionProxy proxy, EdgeCapsuleQueueCompositionProxy? previous)
        {
            RestoreMappings(proxy);
            return true;
        }

        private void RestoreMappings(EdgeCapsuleQueueCompositionProxy? proxy)
        {
            _retained.Clear();
            _peer?._retained.Clear();
            _proxy = proxy;
            if (proxy == null) return;
            foreach (var member in proxy.Members)
            {
                var source = SourceFixture(member.Window);
                source._retained[member.Window] = proxy;
            }
        }

        private NativeInputHostFixture SourceFixture(PaperWindow window) =>
            ReferenceEquals(window, _paperWindow) ? this :
            _peer != null && ReferenceEquals(window, _peer._paperWindow) ? _peer :
            throw new InvalidOperationException("A native fixture generation contains an unknown source");

        private void ReleaseSettledInput(EdgeCapsuleQueueCompositionProxy current, PaperWindow target)
        {
            try
            {
                Check(ReferenceEquals(current, _proxy) && ReferenceEquals(target, _paperWindow),
                    "The real sampler requests only the current generation's physical target");
                Check(current.TryReleaseSettledInput(target, out var successor,
                    Publish, (_, predecessor) => RestoreMappings(predecessor), Complete) && successor != null,
                    "The production settled-input API publishes a peer-only successor");
                _proxy = successor;
                ReleaseCount++;
                // The fixture has no real queue preview controller. Pointer entry above is the
                // actual production timer; after handoff, freeze its unchanged compact peer so
                // subsequent native control gestures cannot dispatch into that isolated adapter.
                // The target's hover, capture and DOWN/UP are entirely normal OS/WPF behavior.
                PausePointerSampling();
            }
            catch (Exception error) { _failure ??= error; }
        }

        internal void AssertPeerRetained(FrameworkElement targetControl)
        {
            ThrowIfFailed();
            Check(_peer != null, "Selective native checks require a second live source");
            Check(_proxy != null && !ReferenceEquals(_proxy, _originalGeneration) &&
                _proxy.Members.Count == 1 && ReferenceEquals(_proxy.Members[0].Window, _peer!._paperWindow),
                "The input handoff installs a distinct generation containing only the peer");
            Check(Output.Handle == _originalOutput && Output.InputHandle == _originalInput &&
                IsProxyCheckWindowVisible(Output.Handle) && IsProxyCheckWindowVisible(Output.InputHandle),
                "The selective successor preserves the same published output and native input HWNDs");
            Check(_proxy!.IsRetainedForQueueBrowsing && !_proxy.RetainsSource(_paperWindow) &&
                _proxy.RetainsSource(_peer!._paperWindow) && !NativeInputIsCloaked(Host.Handle) &&
                NativeInputIsCloaked(_peer.Host.Handle),
                "Only the target returns to real WPF ownership; the peer remains DWM-cloaked under live cover");
            Check(_retained.Count == 0 && _peer!._retained.TryGetValue(_peer._paperWindow, out var mapped) &&
                ReferenceEquals(mapped, _proxy) && !_proxy.TryGetPresentation(_paperWindow, out _) &&
                _proxy.TryGetPresentation(_peer._paperWindow, out var peerFrame) && peerFrame == _peer._compact,
                "Controller mappings and presentation lookup follow the successor's actual source set");
            Check(_proxy!.SnapshotCloakedSourceHandles().SetEquals(new[] { _peer!.Host.Handle }),
                "The successor owns exactly one native cloak lease after the original generation retires");
            var bounds = _peer._compact.InteractiveBounds;
            var peerPoint = new DeviceScreenPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Check(!NativeInputRegionContains(Output.InputHandle, NativeInputCenter(targetControl)) &&
                NativeInputRegionContains(Output.InputHandle, peerPoint),
                "Native HRGN opens the target control while retaining the actual peer hit area");
            Check(Host.MatchesPresentation(_preview) && _peer.Host.MatchesPresentation(_peer._compact),
                "Both real HWNDs retain their verified endpoint geometry after selective handoff");
            Check(ReleaseCount == 1 && ProxyPresses == 0,
                "Native WPF gestures require exactly one selective handoff and no forwarded proxy press");
        }

        internal void CompleteRemainingPeer()
        {
            ThrowIfFailed();
            Check(_peer != null && _proxy != null, "The final release starts with a retained peer generation");
            Check(!_proxy!.TryReleaseSettledInput(_peer!._paperWindow, out var unsupportedSuccessor,
                Publish, (_, predecessor) => RestoreMappings(predecessor), Complete) &&
                unsupportedSuccessor == null && NativeInputIsCloaked(_peer.Host.Handle),
                "The final remaining source rejects selective release without mutating its cloak authority");
            _proxy.CompleteNow(success: true);
            ThrowIfFailed();
            Check(ReleaseCount == 2 && !NativeInputIsCloaked(Host.Handle) &&
                !NativeInputIsCloaked(_peer.Host.Handle) && _retained.Count == 0 && _peer._retained.Count == 0,
                "Ordinary final handoff restores the remaining source and clears both controller mappings");
            Check(!IsProxyCheckWindowVisible(Output.Handle) && !IsProxyCheckWindowVisible(Output.InputHandle),
                "Normal completion hides both pooled HWNDs after the last peer source is restored");
        }

        private void Complete(EdgeCapsuleQueueCompositionProxy proxy, bool success, bool allowRetention)
        {
            try
            {
                if (!success) throw new Exception("Native fixture proxy completion reported failure");
                if (allowRetention)
                {
                    foreach (var member in proxy.Members)
                    {
                        var source = SourceFixture(member.Window);
                        var expected = source._presenter.Preview == EdgeCapsulePreviewState.Open ? source._preview : source._compact;
                        if (!source.Host.MatchesPresentation(expected) || !proxy.HasCompatibleEndpoint(member.Window, expected))
                            throw new Exception("Retained endpoint was not applied or lost source identity");
                    }
                    if (_peer != null) proxy.SettledInputRequested = ReleaseSettledInput;
                    proxy.RetainForQueueBrowsing();
                    return;
                }
                if (SuspendCompletion) return;
                foreach (var member in proxy.Members)
                {
                    var source = SourceFixture(member.Window);
                    var expected = source._presenter.Preview == EdgeCapsulePreviewState.Open ? source._preview : source._compact;
                    if (!source.Host.PrepareCompositionSourceForHandoff() || !source.Host.MatchesPresentation(expected))
                        throw new Exception("Production source endpoint preparation failed");
                }
                if (!proxy.TryReleaseForHandoff()) throw new Exception("Production source handoff failed");
                _retained.Clear();
                _peer?._retained.Clear();
                ReleaseCount++;
            }
            catch (Exception error) { _failure ??= error; }
        }

        internal void ThrowIfFailed() { if (_failure != null) throw new Exception("Native host fixture failed", _failure); }
        public void Dispose()
        {
            _proxy?.Dispose();
            _retained.Clear();
            _presenter.CancelTransition();
            _presenter.ClearDeferredWork();
            Host.Dispose();
            _peer?.Dispose();
        }
    }

    private sealed record NativeInputSnapshot(int Down, int Up, int Click, int TaggedDown, int TaggedUp);
    private sealed class NativeInputCounts { internal int Down, Up, Click, TaggedDown, TaggedUp; }

    // Passive OS-message observation: retain the production WndProc and input policy.
    private sealed class NativeInputWindowProbe : IDisposable
    {
        private IntPtr _handle;
        private readonly NativeInputSubclassProc _callback;
        internal NativeInputCounts Counts { get; } = new();
        internal List<string> Messages { get; } = new();
        internal NativeInputWindowProbe(IntPtr handle)
        {
            _handle = handle;
            _callback = Observe;
            if (!NativeInputSetWindowSubclass(handle, _callback, NativeInputTag, UIntPtr.Zero))
                throw new InvalidOperationException("Could not observe the actual proxy input HWND");
        }
        private IntPtr Observe(IntPtr hwnd, uint message, IntPtr keys, IntPtr position,
            UIntPtr subclassId, UIntPtr reference)
        {
            var tagged = NativeInputGetMessageExtraInfo() == (IntPtr)(long)NativeInputTag.ToUInt64();
            if (message is 0x0201 or 0x0203) { Counts.Down++; if (tagged) Counts.TaggedDown++; }
            if (message == 0x0202) { Counts.Up++; if (tagged) Counts.TaggedUp++; }
            if (message is 0x0201 or 0x0202 or 0x0203) Messages.Add($"0x{message:X4}");
            if (message == 0x0082) _handle = IntPtr.Zero;
            return NativeInputDefSubclassProc(hwnd, message, keys, position);
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = NativeInputRemoveWindowSubclass(_handle, _callback, NativeInputTag);
                _handle = IntPtr.Zero;
            }
            GC.KeepAlive(_callback);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr NativeInputSubclassProc(IntPtr hwnd, uint message, IntPtr keys,
        IntPtr position, UIntPtr subclassId, UIntPtr reference);
    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    private static extern bool NativeInputSetWindowSubclass(IntPtr hwnd, NativeInputSubclassProc callback,
        UIntPtr subclassId, UIntPtr reference);
    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    private static extern bool NativeInputRemoveWindowSubclass(IntPtr hwnd, NativeInputSubclassProc callback,
        UIntPtr subclassId);
    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern IntPtr NativeInputDefSubclassProc(IntPtr hwnd, uint message, IntPtr keys, IntPtr position);

    private sealed class NativeInputRemoteTarget : IDisposable
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly Thread? _thread;
        private readonly Process? _process;
        private Dispatcher? _dispatcher;
        private Window? _window;
        private IntPtr _handle;
        private NativeInputSnapshot _snapshot = new(0, 0, 0, 0, 0);
        internal IntPtr Handle { get { ReadMessages(); return _handle; } }
        internal NativeInputSnapshot Snapshot { get { ReadMessages(); return _snapshot; } }

        internal NativeInputRemoteTarget(DeviceScreenRect bounds, bool separateProcess)
        {
            if (separateProcess)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true
                };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add(NativeInputChildArgument);
                foreach (var value in new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height })
                    start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));
                _process = new Process { StartInfo = start };
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) _messages.Enqueue(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _messages.Enqueue("ERROR " + e.Data); };
                if (!_process.Start()) throw new Exception("Independent native input target failed to start");
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            else
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        _window = NativeInputCreateTarget(bounds, _messages.Enqueue);
                        Dispatcher.Run();
                    }
                    catch (Exception error) { _messages.Enqueue("ERROR " + error); }
                }) { IsBackground = true, Name = "Proxy input independent STA" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        private void ReadMessages()
        {
            while (_messages.TryDequeue(out var line))
            {
                if (line.StartsWith("ERROR ", StringComparison.Ordinal)) throw new Exception(line);
                var values = line.Split(' ');
                if (values[0] == "READY") _handle = new IntPtr(long.Parse(values[1], CultureInfo.InvariantCulture));
                if (values[0] == "INPUT") _snapshot = new(int.Parse(values[1]), int.Parse(values[2]),
                    int.Parse(values[3]), int.Parse(values[4]), int.Parse(values[5]));
            }
            if (_process is { HasExited: true }) throw new Exception($"Native input target exited early: {_process.ExitCode}");
        }

        public void Dispose()
        {
            if (_process != null)
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine("close");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit(2000)) { _process.Kill(); _process.WaitForExit(2000); }
                }
                _process.Dispose();
            }
            if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => _window?.Close()));
            if (_thread != null && !_thread.Join(2000)) throw new Exception("Independent input STA did not stop");
        }
    }

    private static bool TryRunProxyNativeInputChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != NativeInputChildArgument) return false;
        try
        {
            if (args.Length != 5) throw new ArgumentException("Native input target requires x, y, width, height");
            var numbers = args.Skip(1).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = NativeInputCreateTarget(new DeviceScreenRect(numbers[0], numbers[1],
                numbers[0] + numbers[2], numbers[1] + numbers[3]), line => { Console.WriteLine(line); Console.Out.Flush(); });
            _ = Task.Run(() =>
            {
                _ = Console.ReadLine();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)window.Close);
            });
            app.Run();
        }
        catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
        return true;
    }

    private static Window NativeInputCreateTarget(DeviceScreenRect bounds, Action<string> report)
    {
        var counts = new NativeInputCounts();
        void Report() => report($"INPUT {counts.Down} {counts.Up} {counts.Click} {counts.TaggedDown} {counts.TaggedUp}");
        var button = new Button { Content = "Independent OS input target", Background = Brushes.White };
        var window = new Window
        {
            Title = "PaperTodo native input check target", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Width = bounds.Width, Height = bounds.Height, Content = button
        };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)!;
        source.AddHook((IntPtr hwnd, int message, IntPtr keys, IntPtr position, ref bool handled) =>
        {
            // Observe only: do not mark DOWN handled or manufacture a WPF event.
            if (message == 0x0201) { counts.Down++; if (NativeInputGetMessageExtraInfo() == (IntPtr)(long)NativeInputTag.ToUInt64()) counts.TaggedDown++; Report(); }
            if (message == 0x0202) { counts.Up++; if (NativeInputGetMessageExtraInfo() == (IntPtr)(long)NativeInputTag.ToUInt64()) counts.TaggedUp++; Report(); }
            return IntPtr.Zero;
        });
        button.Click += (_, _) => { counts.Click++; Report(); };
        window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        window.Show();
        if (!NativeInputSetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        window.UpdateLayout();
        report("READY " + handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        return window;
    }

    private static DeviceScreenPoint NativeInputCenter(FrameworkElement control)
    {
        Check(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0, "Real control has arranged native geometry");
        var point = control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
        return DeviceScreenPoint.FromPoint(point);
    }

    private static bool NativeInputPositionOutside(FrameworkElement control)
    {
        var position = Mouse.GetPosition(control);
        return position.X < 0 || position.Y < 0 ||
            position.X > control.ActualWidth || position.Y > control.ActualHeight;
    }

    private static bool NativeInputCursorNear(DeviceScreenPoint expected) =>
        NativeInputGetCursorPos(out var actual) &&
        Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2;

    private static string NativeInputControlStatus(ButtonBase control, NativeInputCounts counts,
        NativeInputHostFixture fixture)
    {
        var position = Mouse.GetPosition(control);
        var cursorAvailable = NativeInputGetCursorPos(out var cursor);
        var screenPosition = cursorAvailable ? control.PointFromScreen(new Point(cursor.X, cursor.Y)) : new Point(double.NaN, double.NaN);
        return $"control={control.GetType().Name} name={control.Name} content={control.Content} " +
            $"visible={control.IsVisible} enabled={control.IsEnabled} over={control.IsMouseOver} pressed={control.IsPressed} " +
            $"captured={control.IsMouseCaptured} Mouse.Captured={Mouse.Captured?.GetType().Name ?? "<null>"} " +
            $"ownsCapture={ReferenceEquals(Mouse.Captured, control)} nativeCapture=0x{NativeInputGetCapture().ToInt64():X} " +
            $"left={Mouse.LeftButton} osLeft={(NativeInputGetAsyncKeyState(1) & 0x8000) != 0} " +
            $"cursorAvailable={cursorAvailable} cursor={cursor.X},{cursor.Y} getPosition={position.X:F2},{position.Y:F2} " +
            $"screenToControl={screenPosition.X:F2},{screenPosition.Y:F2} size={control.ActualWidth:F2}x{control.ActualHeight:F2} " +
            $"down={counts.Down} up={counts.Up} clicks={counts.Click} " +
            $"releaseCount={fixture.ReleaseCount} proxyPresses={fixture.ProxyPresses}";
    }

    private static void NativeInputClick() { NativeInputSend(0, 0, 0x0002); NativeInputSend(0, 0, 0x0004); }
    private static void NativeInputMove(DeviceScreenPoint point)
    {
        var left = NativeInputGetSystemMetrics(76); var top = NativeInputGetSystemMetrics(77);
        var width = NativeInputGetSystemMetrics(78); var height = NativeInputGetSystemMetrics(79);
        if (width <= 1 || height <= 1 || point.X < left || point.Y < top || point.X >= left + width || point.Y >= top + height)
            throw new InvalidOperationException("Native input target lies outside the virtual desktop");
        NativeInputSend((int)Math.Round((point.X - left) * 65535d / (width - 1)),
            (int)Math.Round((point.Y - top) * 65535d / (height - 1)), 0x0001 | 0x8000 | 0x4000 | 0x2000);
    }

    private static void NativeInputSend(int x, int y, uint flags)
    {
        var input = new NativeInputPacket { Type = 0, Mouse = new() { X = x, Y = y, Flags = flags, ExtraInfo = NativeInputTag } };
        if (NativeInputSendInput(1, new[] { input }, Marshal.SizeOf<NativeInputPacket>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput did not accept the test input");
    }

    private static void NativeInputUntil(Func<bool> predicate, string message, Func<string>? describeFailure = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.ElapsedMilliseconds < 2500) NativeInputPumpFor(10);
        var completed = predicate();
        if (!completed && describeFailure != null)
            Console.Error.WriteLine($"TIMEOUT native-input: {message}; {describeFailure()}");
        Check(completed, message);
    }

    private static void NativeInputPumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }

    private static bool NativeInputIsCloaked(IntPtr handle)
    {
        var result = NativeInputDwmGetWindowAttribute(handle, 14, out var flags, sizeof(int));
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        return (flags & 1) != 0;
    }

    private static bool NativeInputRegionEmpty(IntPtr handle)
    {
        var region = NativeInputCreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return NativeInputGetWindowRgn(handle, region) == 1; }
        finally { NativeInputDeleteObject(region); }
    }

    private static bool NativeInputRegionContains(IntPtr handle, DeviceScreenPoint point)
    {
        var region = NativeInputCreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return NativeInputGetWindowRgn(handle, region) > 1 &&
                GetProxyCheckWindowRect(handle, out var bounds) && NativeInputPtInRegion(region,
                    (int)Math.Round(point.X - bounds.Left), (int)Math.Round(point.Y - bounds.Top));
        }
        finally { NativeInputDeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeInputPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInputRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInputPacket { internal uint Type; internal NativeInputMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInputMouse
    { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr ExtraInfo; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint NativeInputSendInput(uint count, NativeInputPacket[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool NativeInputGetCursorPos(out NativeInputPoint point);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short NativeInputGetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int NativeInputGetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetMessageExtraInfo")] private static extern IntPtr NativeInputGetMessageExtraInfo();
    [DllImport("user32.dll", EntryPoint = "GetCapture")] private static extern IntPtr NativeInputGetCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint NativeInputGetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static extern uint NativeInputGetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] private static extern bool NativeInputSetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int NativeInputDwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)] private static extern bool NativeInputDestroyWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetUpdateRect")] private static extern bool NativeInputGetUpdateRect(IntPtr handle, out NativeInputRect rect, bool erase);
    [DllImport("user32.dll", EntryPoint = "InvalidateRect", SetLastError = true)] private static extern bool NativeInputInvalidateRect(IntPtr handle, IntPtr rect, bool erase);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)] private static extern IntPtr NativeInputSendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")] private static extern int NativeInputDwmFlush();
    [DllImport("dcomp.dll", EntryPoint = "DCompositionCreateDevice2", ExactSpelling = true)] private static extern int NativeInputDCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, out IntPtr device);
    [DllImport("user32.dll", EntryPoint = "GetWindowRgn")] private static extern int NativeInputGetWindowRgn(IntPtr handle, IntPtr region);
    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn", SetLastError = true)] private static extern IntPtr NativeInputCreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", EntryPoint = "PtInRegion")] private static extern bool NativeInputPtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool NativeInputDeleteObject(IntPtr item);
}
