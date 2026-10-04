using System.Collections;
using System.Reflection;
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
                pointer, controller.MasterCapsuleQueueKey("", EdgeCapsuleEdge.Right), hadMargin, originalMargin)!;
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
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
