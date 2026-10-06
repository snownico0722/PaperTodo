using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void ProxyInputReadiness()
    {
        TerminalHandoffFrames();
        ProxyCompletionFailureStopsRetrying();
        ProxyOutputWindowVisibility();
        ProxyCrossThreadInputPassthrough();
        ProxyPointerMessageCoordinates();
        ProxyImmediateInputChecks();
        // Exercise the real native mouse-message adapter and proxy callback. Lifecycle fields are
        // injected to cover reentrant publication/retirement without requiring a live DComp device;
        // this is not a substitute for testing the complete compositor handoff on a real desktop.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(EdgeCapsuleQueueCompositionProxy);
        var proxy = (EdgeCapsuleQueueCompositionProxy)RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(proxy, value);
        var resting = EdgeCapsulePresentationFrame.Hidden with
        {
            Surface = EdgeCapsuleSurfaceKind.DockedResting
        };
        void SetPlan(EdgeCapsulePresentationFrame start, EdgeCapsulePresentationFrame target) =>
            Set("_plan", new EdgeCapsuleQueueProxyPlan("input-readiness",
                new DeviceScreenRect(0, 0, 100, 40), EdgeCapsuleEdge.Left,
                0, 1, 1, 120, false,
                new[] { new EdgeCapsuleQueueProxyMemberPlan("test", start, start, target) }));
        SetPlan(resting, resting);
        var received = new List<int>();
        Set("_interactionRequested", (Action<EdgeCapsulePointerDown>)(input => received.Add(input.Message)));
        var route = type.GetMethod("HandleInteractionRequested", flags)!
            .CreateDelegate<Action<EdgeCapsulePointerDown>>(proxy);
        using var window = EdgeCapsuleQueueProxyWindow.TryCreate(new DeviceScreenRect(0, 0, 100, 40),
            false, _ => true, route, () => { }, () => { }, () => { });
        Check(window != null, "Create native proxy input regression HWND");
        var messages = new[] { 0x0201, 0x0204, 0x0207 };
        void Click()
        {
            foreach (var message in messages)
                SendProxyInputCheckMessage(window!.Handle, message, IntPtr.Zero, IntPtr.Zero);
        }
        void Ready()
        {
            foreach (var field in new[] { "_disposed", "_starting", "_coverLost", "_sourcesReleased",
                "_finishing", "_successorHeld" }) Set(field, false);
            Set("_coverPublished", true);
        }

        Ready();
        Click();
        Check(received.SequenceEqual(messages), "Published proxy routes native left/right/middle input");
        foreach (var (field, value) in new[]
        {
            ("_coverPublished", false), ("_starting", true), ("_finishing", true),
            ("_successorHeld", true), ("_sourcesReleased", true), ("_coverLost", true), ("_disposed", true)
        })
        {
            Ready();
            Set(field, value);
            var before = received.Count;
            Click();
            Check(received.Count == before, "Native proxy input must wait for published authority: " + field);
            Ready();
            Click();
            Check(received.Count == before + messages.Length, "Ready authority resumes native input: " + field);
        }

        foreach (var retractAtStart in new[] { false, true })
        {
            var retracted = resting with { Surface = EdgeCapsuleSurfaceKind.DockedRetracted };
            SetPlan(retractAtStart ? retracted : resting, retractAtStart ? resting : retracted);
            Ready();
            var before = received.Count;
            Click();
            Check(received.Count == before, "Master collapse/release remains pointer-transparent");
        }
        Console.WriteLine("PASS proxy-native-input-publication-and-retirement");
    }

    private static void ProxyCompletionFailureStopsRetrying()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(EdgeCapsuleQueueCompositionProxy);
        var proxy = (EdgeCapsuleQueueCompositionProxy)RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(proxy, value);

        var frame = EdgeCapsulePresentationFrame.Hidden with
        {
            Surface = EdgeCapsuleSurfaceKind.DockedResting
        };
        Set("_plan", new EdgeCapsuleQueueProxyPlan(
            "retry-stop",
            new DeviceScreenRect(0, 0, 100, 40),
            EdgeCapsuleEdge.Left,
            0,
            1,
            1,
            120,
            false,
            new[] { new EdgeCapsuleQueueProxyMemberPlan("test", frame, frame, frame) }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        Set("_completionTimer", timer);

        var stopped = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Set("_finishing", true);
            proxy.ScheduleCompletionRetry(success: false);
            if (!timer.IsEnabled)
            {
                stopped = true;
                break;
            }
            timer.Stop();
        }

        Check(stopped && !timer.IsEnabled,
            "Repeated completion failure reports finite retry exhaustion");

        Console.WriteLine("PASS proxy completion failure has a finite retry lifecycle");
    }

    private static void ProxyOutputWindowVisibility()
    {
        // These are native visibility/placement checks. An output without a DComp root has no
        // rendered content, so IsWindowVisible is not evidence that any frame reached the screen.
        foreach (var topmost in new[] { false, true })
        {
            var initial = new DeviceScreenRect(100, 100, 200, 140);
            using var output = EdgeCapsuleQueueProxyWindow.TryCreate(initial, topmost,
                _ => true, _ => { }, () => { }, () => { }, () => { });
            Check(output != null, "Create proxy output for native publication checks");
            var handle = output!.Handle;
            Check(handle != IntPtr.Zero && !IsProxyCheckWindowVisible(handle),
                "A newly created proxy output remains hidden until publication");
            var first = new DeviceScreenRect(120, 150, 320, 230);
            var reused = new DeviceScreenRect(140, 180, 360, 280);
            void CheckShown(DeviceScreenRect bounds)
            {
                Check(output.Show(bounds, topmost), "Publish proxy visibility and geometry together");
                Check(IsProxyCheckWindowVisible(handle), "Show makes the native proxy HWND visible");
                Check(GetProxyCheckWindowRect(handle, out var actual) &&
                    new DeviceScreenRect(actual.Left, actual.Top, actual.Right, actual.Bottom) == bounds,
                    "Published native proxy bounds match the requested physical rectangle");
                Check(output.Handle == handle, "Showing the proxy preserves its reusable HWND identity");
            }

            CheckShown(first);
            CheckShown(first);
            output.Hide();
            Check(!IsProxyCheckWindowVisible(handle), "Hide removes native proxy visibility");
            CheckShown(reused);
            output.Hide();
            Check(!IsProxyCheckWindowVisible(handle), "Reused output can be hidden again");
        }
        Console.WriteLine("PASS proxy-native-output-show-hide-and-reuse");
    }

    private static void ProxyCrossThreadInputPassthrough()
    {
        var bounds = new DeviceScreenRect(240, 180, 440, 320);
        EdgeCapsuleQueueProxyWindow? target = null;
        Dispatcher? targetDispatcher = null;
        var targetClicks = 0;
        using var ready = new ManualResetEventSlim();
        var targetThread = new Thread(() =>
        {
            targetDispatcher = Dispatcher.CurrentDispatcher;
            target = EdgeCapsuleQueueProxyWindow.TryCreate(
                bounds,
                topmost: false,
                _ => true,
                input =>
                {
                    if (input.Message == 0x0201)
                    {
                        Interlocked.Increment(ref targetClicks);
                    }
                },
                () => { },
                () => { },
                () => { });
            if (target == null || !target.Show(bounds, topmost: false))
            {
                ready.Set();
                return;
            }
            ready.Set();
            Dispatcher.Run();
        });
        targetThread.SetApartmentState(ApartmentState.STA);
        targetThread.Start();
        Check(ready.Wait(TimeSpan.FromSeconds(5)) && target != null && targetDispatcher != null,
            "Create cross-thread native target below proxy output");

        var routesInput = true;
        var proxyClicks = 0;
        using var proxy = EdgeCapsuleQueueProxyWindow.TryCreate(
            bounds,
            topmost: true,
            _ => routesInput,
            input =>
            {
                if (input.Message == 0x0201)
                {
                    proxyClicks++;
                }
            },
            () => { },
            () => { },
            () => { });
        Check(proxy != null && proxy.Show(bounds, topmost: true),
            "Show cross-thread passthrough proxy above target");
        var liveProxy = proxy!;
        using var visibleCover = new ProxyVisualEvidence(liveProxy.Handle, bounds);

        GetCursorPosForProxyCheck(out var originalCursor);
        try
        {
            var x = bounds.Left + bounds.Width / 2;
            var y = bounds.Top + bounds.Height / 2;
            Check(SetCursorPosForProxyCheck(x, y), "Position pointer over proxy test windows");

            visibleCover.AssertRedAndCloakSource();
            ClickAtCursor();
            Check(WaitForProxyCheck(() => proxyClicks == 1),
                "Interactive proxy receives the native click");
            Check(Volatile.Read(ref targetClicks) == 0,
                "Interactive proxy keeps the lower cross-thread window from receiving the click");

            routesInput = false;
            liveProxy.EnableInputPassthrough();
            visibleCover.AssertLivePassthrough();
            ClickAtCursor();
            Check(WaitForProxyCheck(() => Volatile.Read(ref targetClicks) == 1),
                "Purely visual proxy passes a real click to the lower cross-thread window");
            Check(proxyClicks == 1,
                "Purely visual proxy does not consume the cross-thread click");

            // Passthrough is deliberately one-way for one output HWND. Retire it and prove that a
            // fresh interactive output can own input again, matching QueueHost's runtime policy.
            liveProxy.Hide();
            var replacementClicks = 0;
            using var replacement = EdgeCapsuleQueueProxyWindow.TryCreate(
                bounds,
                topmost: true,
                _ => true,
                input =>
                {
                    if (input.Message == 0x0201)
                    {
                        replacementClicks++;
                    }
                },
                () => { },
                () => { },
                () => { });
            Check(replacement != null && replacement.Show(bounds, topmost: true),
                "Create fresh interactive proxy after retiring master passthrough output");
            ClickAtCursor();
            Check(WaitForProxyCheck(() => replacementClicks == 1),
                "Fresh interactive proxy receives native input after master host retirement");
            replacement!.Hide();

            // Production sets passthrough before publication, not only on a shown output.
            var coldClicks = 0;
            using var cold = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true, _ => false,
                _ => coldClicks++, () => { }, () => { }, () => { });
            Check(cold != null, "Create a cold master output for simultaneous pixel/input evidence");
            cold!.EnableInputPassthrough();
            using var coldPixels = new ProxyVisualEvidence(cold.Handle, bounds);
            Check(cold.Show(bounds, true), "Publish cold passthrough output with a live DComp root");
            coldPixels.AssertRedAndCloakSource();
            coldPixels.AssertLivePassthrough();
            // These are independent single-click checks on the same lower HWND. Move outside
            // the double-click rectangle instead of depending on the runner's click timing.
            Check(SetCursorPosForProxyCheck(x + 20, y + 20), "Separate the cold single-click probe");
            ClickAtCursor();
            Check(WaitForProxyCheck(() => Volatile.Read(ref targetClicks) == 2) && coldClicks == 0,
                "Cold master cover remains visibly live while passing the native click across threads");
        }
        finally
        {
            _ = SetCursorPosForProxyCheck(originalCursor.X, originalCursor.Y);
            liveProxy.Hide();
            targetDispatcher!.BeginInvoke(
                DispatcherPriority.Send,
                (Action)(() =>
                {
                    target?.Dispose();
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Send);
                }));
            targetThread.Join(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("PASS visible live master proxy, cold/warm cross-thread passthrough and fresh interactive replacement");
    }

    private static bool WaitForProxyCheck(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.ElapsedMilliseconds < 1500)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        return predicate();
    }

    private static void ClickAtCursor()
    {
        MouseEventForProxyCheck(0x0002, 0, 0, 0, UIntPtr.Zero);
        MouseEventForProxyCheck(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyCheckNativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyCheckNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProxyCheckWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosForProxyCheck(out ProxyCheckNativePoint point);

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPosForProxyCheck(int x, int y);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEventForProxyCheck(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProxyCheckWindowRect(IntPtr window, out ProxyCheckNativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendProxyInputCheckMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
