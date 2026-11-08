using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PaperTodo;

internal static class MasterQueueDragPreparationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(AppController controller)
    {
        var masters = (IDictionary)typeof(AppController).GetField("_masterCapsules", Private)!
            .GetValue(controller)!;
        var master = (MasterCapsuleWindow)masters["|" + DeepCapsuleSides.Right]!;
        Require(controller.TryCreateMasterQueueFloatingDragHostOptions(
                "", EdgeCapsuleEdge.Right, "▾", "5", out var options),
            "master drag fixture did not create floating options");
        Require(EdgeCapsuleDragWindow.TryPrewarmInfrastructure(options),
            "master drag fixture did not prewarm its real HWND");
        var host = EdgeCapsuleDragWindow.Rent(options);
        host.ReturnToPool();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(host).Handle)!;
        var prepared = false;
        var nativeDragStarted = false;
        DispatcherOperation? layout = null;
        var originalSides = controller.State.Papers.Select(paper => paper.CapsuleSide).ToArray();
        var originalMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
        var hadMargin = controller.HasDeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);

        IntPtr NativeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x00A1) // WM_NCLBUTTONDOWN: observe entry without taking over the user's mouse.
            {
                nativeDragStarted = true;
                handled = true;
            }
            return IntPtr.Zero;
        }

        void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!host.IsVisible || layout != null) return;
            // Show queues WPF work at Render priority. It must run while the transfer still owns
            // this floating surface, before centering/entering the native caption move loop.
            layout = master.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                prepared = host.IsVisible && !master.IsVisible &&
                    ReferenceEquals(host, typeof(MasterCapsuleWindow)
                        .GetField("_floatingDragHost", Private)!.GetValue(master)) &&
                    typeof(MasterCapsuleWindow).GetField("_queueTransferSnapshot", Private)!
                        .GetValue(master) != null;
                master.CancelQueueTransfer(); // exercise cancellation re-entering through that render turn
            }));
        }

        source.AddHook(NativeHook);
        host.IsVisibleChanged += OnVisibleChanged;
        try
        {
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
                "master drag fixture has no monitor");
            var pointer = new DeviceScreenPoint(monitor.WorkArea.Right - 100, monitor.WorkArea.Top + 100);
            var sessionType = typeof(MasterCapsuleWindow).GetNestedType("MasterDragSession", Private)!;
            var session = Activator.CreateInstance(sessionType,
                pointer, controller.MasterCapsuleQueueKey("", EdgeCapsuleEdge.Right), hadMargin, originalMargin, null)!;
            Require((bool)typeof(MasterCapsuleWindow).GetMethod("TryRunQueueTransfer", Private)!
                    .Invoke(master, [pointer, session])!,
                "master drag fixture did not consume the transfer gesture");
            master.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            Require(prepared, "master transferred ownership before its floating WPF render work completed");
            Require(!nativeDragStarted, "master entered native dragging after render-turn cancellation");
            Require(controller.State.Papers.Select(paper => paper.CapsuleSide).SequenceEqual(originalSides),
                "render-turn cancellation changed queue membership");
            Require(controller.HasDeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) == hadMargin &&
                    Math.Abs(controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) - originalMargin) < 0.01,
                "render-turn cancellation changed the source queue anchor");
            Require(master.IsVisible && !host.IsVisible,
                "render-turn cancellation did not restore master visual authority");
            var nextLease = EdgeCapsuleDragWindow.Rent(options);
            Require(ReferenceEquals(host, nextLease), "cancelled master drag stranded its pooled HWND lease");
            nextLease.ReturnToPool();
        }
        finally
        {
            layout?.Abort();
            host.IsVisibleChanged -= OnVisibleChanged;
            source.RemoveHook(NativeHook);
        }

        CheckNativeCaptionAnchor(options);
    }

    private static void CheckNativeCaptionAnchor(EdgeCapsuleDragWindowOptions options)
    {
        const int probeMessage = 0x8000 + 73;
        Require(WindowNative.TryGetCursorScreenPosition(out var saved), "save drag-check cursor");
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "caption anchor fixture needs a monitor");
        var x = (monitor.WorkArea.Left + monitor.WorkArea.Right) / 2;
        var y = (monitor.WorkArea.Top + monitor.WorkArea.Bottom) / 2;
        var host = EdgeCapsuleDragWindow.Rent(options);
        try
        {
            foreach (var advance in new[] { 0, 120, -120 })
            {
                Require(SetPhysicalCursorPos(x, y), "position caption anchor fixture");
                var hwnd = new WindowInteropHelper(host).Handle;
                var source = HwndSource.FromHwnd(hwnd)!;
                var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => frame.Continue = false;
                var received = false;
                var started = false;
                var hasBounds = false;
                var hasShownBounds = false;
                var shownBounds = default(NativeRect);
                var queued = default(NativePoint);
                var live = default(NativePoint);
                var caption = default(NativePoint);
                var bounds = default(NativeRect);
                IntPtr Hook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (message == probeMessage)
                    {
                        queued = Unpack(GetMessagePos());
                        Require(SetPhysicalCursorPos(x + advance, y), "advance physical pointer during handoff");
                        GetCursorPos(out live);
                        host.ShowWithEntrance(
                            new DeviceScreenPoint(x, y), false, 1, 0,
                            alignToDragMessage: true);
                        hasShownBounds = GetWindowRect(h, out shownBounds);
                        host.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
                        started = host.RunNativeDragFromCursor().Result == EdgeCapsuleNativeDragResult.Completed;
                        handled = true;
                        frame.Continue = false;
                    }
                    else if (message == 0x00A1)
                    {
                        // Observe the real HWND handoff without entering a modal move loop.
                        // The queued point, synthetic caption press and shown rectangle must agree.
                        received = true;
                        caption = Unpack(unchecked((uint)l.ToInt64()));
                        hasBounds = GetWindowRect(h, out bounds);
                        handled = true;
                    }
                    return IntPtr.Zero;
                }
                source.AddHook(Hook);
                try
                {
                    Require(PostMessage(hwnd, probeMessage, IntPtr.Zero, IntPtr.Zero), "queue caption input epoch");
                    timeout.Start();
                    Dispatcher.PushFrame(frame);
                    Require(started && received && hasBounds && hasShownBounds,
                        "floating show or caption anchor handoff was not observed");
                    Require(advance == 0 || Math.Abs(live.X - queued.X) > 20,
                        "caption fixture did not separate queued and live cursor positions");
                    Require(Math.Abs((shownBounds.Left + shownBounds.Right) / 2.0 - queued.X) <= 1 &&
                            Math.Abs((shownBounds.Top + shownBounds.Bottom) / 2.0 - queued.Y) <= 1,
                        "floating capsule became visible at the newer live cursor before native dragging");
                    Console.WriteLine($"caption handoff: advance={advance}, queued={queued.X},{queued.Y}, live={live.X},{live.Y}, shownCenter={(shownBounds.Left + shownBounds.Right) / 2.0},{(shownBounds.Top + shownBounds.Bottom) / 2.0}, caption={caption.X},{caption.Y}, center={(bounds.Left + bounds.Right) / 2.0},{(bounds.Top + bounds.Bottom) / 2.0}");
                    Require(caption.X == queued.X && caption.Y == queued.Y,
                        "native caption anchor followed the live cursor instead of the queued message");
                    Require(Math.Abs((bounds.Left + bounds.Right) / 2.0 - queued.X) <= 1 &&
                            Math.Abs((bounds.Top + bounds.Bottom) / 2.0 - queued.Y) <= 1,
                        "floating HWND and native caption anchor described different input epochs");
                    Console.WriteLine($"PASS caption anchor: physical advance={advance}, queued={queued.X},{queued.Y}, live={live.X},{live.Y}");
                }
                finally
                {
                    timeout.Stop();
                    source.RemoveHook(Hook);
                    host.Opacity = 0;
                    host.Hide();
                }
            }
        }
        finally
        {
            host.ReturnToPool();
            SetPhysicalCursorPos((int)saved.X, (int)saved.Y);
        }
    }

    private static NativePoint Unpack(uint packed) => new()
    {
        X = unchecked((short)packed), Y = unchecked((short)(packed >> 16))
    };
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
